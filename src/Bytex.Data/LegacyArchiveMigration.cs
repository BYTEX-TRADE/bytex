using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bytex.Core.Migration;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Parquet.Serialization;

namespace Bytex.Data;

/// <summary>Explicit, resumable conversion into a separate archive. Never alters source objects.</summary>
public static class LegacyArchiveMigration
{
    private sealed record Input(string Key, string Hash);

    public static async Task<int> ConvertAsync(IObjectStore source, IObjectStore destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        string from = source.Location.Replace('\\', '/').TrimEnd('/');
        string to = destination.Location.Replace('\\', '/').TrimEnd('/');
        StringComparison comparison = from.Contains("://", StringComparison.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (from.Equals(to, comparison) || from.StartsWith(to + "/", comparison) || to.StartsWith(from + "/", comparison))
        {
            throw new ArgumentException("Migration requires separate, non-nested source and destination stores.");
        }
        List<Input> inputs = [];
        foreach (StoredObject file in source.List(string.Empty).OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            await using Stream stream = await source.OpenReadAsync(file.Key, ct).ConfigureAwait(false);
            inputs.Add(new Input(file.Key, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))));
        }
        string inventory = JsonSerializer.Serialize(inputs);
        const string inventoryKey = "migration/source-inventory.json";
        if (destination.Exists(inventoryKey))
        {
            if (destination.ReadText(inventoryKey) != inventory) { throw new InvalidDataException("Migration source changed since the checkpoint; use a new destination."); }
        }
        else
        {
            if (destination.List(string.Empty).Count != 0) { throw new InvalidDataException("A new migration destination must be empty."); }
            await AtomicTextAsync(destination, inventoryKey, inventory, ct).ConfigureAwait(false);
        }
        int converted = 0;
        foreach (Input input in inputs)
        {
            string[] parts = input.Key.Split('/');
            if (parts.Length == 2 && parts[0] == "instruments" && input.Key.EndsWith(".json", StringComparison.Ordinal))
            {
                JsonObject obj = JsonNode.Parse(source.ReadText(input.Key))?.AsObject() ?? throw new JsonException("Empty instrument.");
                string oldId = obj["id"]?.GetValue<string>() ?? throw new JsonException("Instrument id missing.");
                string id = Market(oldId).Value;
                obj["id"] = id;
                Rename(obj, "tsEvent", "eventTime");
                Rename(obj, "tsInit", "createdTime");
                string json = obj.ToJsonString();
                _ = InstrumentJson.Deserialize(json);
                await AtomicTextAsync(destination, "instruments/" + Bytex.Core.Serialization.ObjectKeyCodec.Encode(id) + ".json", json, ct).ConfigureAwait(false);
                converted++;
            }
            else if (parts.Length == 3 && parts[0] == "bars" && parts[2] == "empty-spans.json")
            {
                string key = Candle(DecodeLegacyName(parts[1])).ToString();
                string json = source.ReadText(input.Key);
                using JsonDocument checkedJson = JsonDocument.Parse(json);
                await AtomicTextAsync(destination, "bars/" + Bytex.Core.Serialization.ObjectKeyCodec.Encode(key) + "/empty-spans.json", json, ct).ConfigureAwait(false);
                converted++;
            }
            else if (parts.Length == 3 && input.Key.EndsWith(".parquet", StringComparison.Ordinal))
            {
                string identity = DecodeLegacyName(parts[1]);
                string key = parts[0] == "bars" ? Candle(identity).ToString() : Market(identity).Value;
                string prefix = parts[0] + "/" + Bytex.Core.Serialization.ObjectKeyCodec.Encode(key);
                string commitId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Key)))[..32].ToLowerInvariant();
                ArchiveJournal journal = new(destination);
                var prior = journal.Commits(prefix).FirstOrDefault(c => c.Id == commitId);
                if (prior.Commit is not null)
                {
                    if (prior.Commit.SourceHash != input.Hash) { throw new InvalidDataException("Checkpoint source hash differs."); }
                    foreach (ArchiveSegment segment in prior.Commit.Segments)
                    {
                        await using Stream verified = await journal.OpenVerifiedAsync(segment, ct).ConfigureAwait(false);
                    }
                    converted++;
                    continue;
                }
                await using Stream stream = await source.OpenReadAsync(input.Key, ct).ConfigureAwait(false);
                var decoded = await ParquetSerializer.DeserializeUntypedAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                List<JsonObject> rows = [];
                foreach (var row in decoded.Data)
                {
                    JsonObject obj = JsonSerializer.SerializeToNode(row)?.AsObject() ?? throw new JsonException("Empty row.");
                    Rename(obj, "TsEvent", "EventTime");
                    Rename(obj, "TsInit", "CreatedTime");
                    if (obj["EventTime"] is null || obj["CreatedTime"] is null) { throw new InvalidDataException("Both timestamp columns are required."); }
                    rows.Add(obj);
                }
                switch (parts[0])
                {
                    case "quotes": await ImportAsync<MarketArchive.QuoteRow>(journal, prefix, rows, r => r.CreatedTime, commitId, input.Hash, ct).ConfigureAwait(false); break;
                    case "trades": await ImportAsync<MarketArchive.TradeRow>(journal, prefix, rows, r => r.CreatedTime, commitId, input.Hash, ct).ConfigureAwait(false); break;
                    case "bars": await ImportAsync<MarketArchive.BarRow>(journal, prefix, rows, r => r.CreatedTime, commitId, input.Hash, ct).ConfigureAwait(false); break;
                    case "book_deltas": await ImportAsync<MarketArchive.DeltaRow>(journal, prefix, rows, r => r.CreatedTime, commitId, input.Hash, ct).ConfigureAwait(false); break;
                    case "book_depth": await ImportAsync<MarketArchive.DepthRow>(journal, prefix, rows, r => r.CreatedTime, commitId, input.Hash, ct).ConfigureAwait(false); break;
                    case "funding": await ImportAsync<MarketArchive.FundingRow>(journal, prefix, rows, r => r.CreatedTime, commitId, input.Hash, ct).ConfigureAwait(false); break;
                    default: throw new InvalidDataException($"Unsupported legacy data kind: {parts[0]}");
                }
                converted++;
            }
            else { throw new InvalidDataException($"Unrecognised source object must be reviewed, not silently discarded: {input.Key}"); }
            await using Stream current = await source.OpenReadAsync(input.Key, ct).ConfigureAwait(false);
            if (Convert.ToHexString(await SHA256.HashDataAsync(current, ct).ConfigureAwait(false)) != input.Hash)
            {
                throw new InvalidDataException("Source changed while being converted; destination must not be accepted.");
            }
        }
        await AtomicTextAsync(destination, "migration/completed.json", inventory, ct).ConfigureAwait(false);
        return converted;
    }

    private static async Task ImportAsync<TRow>(ArchiveJournal journal, string prefix, List<JsonObject> source,
        Func<TRow, long> timestamp, string id, string hash, CancellationToken ct) where TRow : class, new()
    {
        List<TRow> rows = source.Select(row => row.Deserialize<TRow>(ArchiveJournal.Json)
            ?? throw new JsonException("Null source row.")).OrderBy(timestamp).ToList();
        if (rows.Count == 0) { throw new InvalidDataException("Empty legacy segment requires explicit review."); }
        List<ArchiveSegment> segments = [];
        foreach (TRow[] chunk in rows.Chunk(MarketArchive.MaxRowsPerFile))
        {
            ArchiveSegment segment = await journal.StageAsync(prefix, timestamp(chunk[0]), timestamp(chunk[^1]), chunk.Length, segments.Count,
                stream => ParquetSerializer.SerializeAsync(chunk, stream, cancellationToken: ct), ct).ConfigureAwait(false);
            await using Stream verify = await journal.OpenVerifiedAsync(segment, ct).ConfigureAwait(false);
            var restored = await ParquetSerializer.DeserializeAsync<TRow>(verify, cancellationToken: ct).ConfigureAwait(false);
            if (!chunk.Select(r => JsonSerializer.Serialize(r)).SequenceEqual(restored.Data.Select(r => JsonSerializer.Serialize(r)), StringComparer.Ordinal))
            {
                throw new InvalidDataException("Converted rows differ from source rows; no manifest committed.");
            }
            segments.Add(segment);
        }
        await journal.PublishAsync(prefix, segments, [], ct, id, hash).ConfigureAwait(false);
    }

    private static MarketKey Market(string value) => value.StartsWith("bx-market:", StringComparison.Ordinal) ? MarketKey.Parse(value) : LegacyIdentityReader.ReadMarket(value);

    private static CandleSeries Candle(string value) => value.StartsWith("bx-candle:", StringComparison.Ordinal) ? CandleSeries.Parse(value) : LegacyIdentityReader.ReadCandle(value);

    private static string DecodeLegacyName(string value) => value.StartsWith("bx-", StringComparison.Ordinal)
        ? Bytex.Core.Serialization.ObjectKeyCodec.Decode(value) : value.Replace("%2F", "/", StringComparison.Ordinal).Replace("%5C", "\\", StringComparison.Ordinal).Replace("%3A", ":", StringComparison.Ordinal).Replace("%25", "%", StringComparison.Ordinal);

    private static void Rename(JsonObject obj, string oldName, string newName)
    {
        if (!obj.ContainsKey(oldName)) { return; }
        if (obj.ContainsKey(newName)) { throw new InvalidDataException($"Both timestamp names present: {oldName}, {newName}"); }
        JsonNode? value = obj[oldName];
        obj.Remove(oldName);
        obj[newName] = value;
    }

    internal static Task AtomicTextAsync(IObjectStore store, string key, string text, CancellationToken ct) =>
        store.WriteAsync(key, stream => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask(), ct);
}
