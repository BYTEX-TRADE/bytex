using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bytex.Data;

/// <summary>A committed immutable segment; timestamps are metadata, never filenames.</summary>
public sealed record ArchiveSegment(string ObjectKey, long Start, long End, int Rows, long Bytes, string Sha256, int Ordinal);

internal sealed record ArchiveCommit(int Format, long Sequence, string[] Replaces, ArchiveSegment[] Segments, string? SourceHash = null);

internal sealed class ArchiveJournal(IObjectStore store)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal IReadOnlyList<(string Id, ArchiveCommit Commit)> Commits(string prefix)
    {
        List<(string Id, ArchiveCommit Commit)> result = [];
        foreach (StoredObject file in store.List(prefix + "/commits"))
        {
            if (!file.Key.EndsWith(".json", StringComparison.Ordinal)) { continue; }
            string id = Path.GetFileNameWithoutExtension(file.Key);
            if (!Guid.TryParseExact(id, "N", out _)) { throw new InvalidDataException($"Invalid commit key: {file.Key}"); }
            ArchiveCommit commit = JsonSerializer.Deserialize<ArchiveCommit>(store.ReadText(file.Key), Json)
                ?? throw new InvalidDataException($"Empty manifest: {file.Key}");
            if (commit.Format != 2 || commit.Sequence < 0 || commit.Segments is null || commit.Replaces is null
                || commit.Segments.Length == 0 || commit.Replaces.Distinct(StringComparer.Ordinal).Count() != commit.Replaces.Length)
            {
                throw new InvalidDataException($"Invalid manifest: {file.Key}");
            }
            for (int i = 0; i < commit.Segments.Length; i++)
            {
                ArchiveSegment segment = commit.Segments[i]
                    ?? throw new InvalidDataException($"Null segment metadata: {file.Key}");
                ValidateObjectKey(prefix, segment.ObjectKey);
                if (segment.Start > segment.End || segment.Rows is < 1 or > MarketArchive.MaxRowsPerFile
                    || segment.Bytes < 1 || segment.Ordinal != i || segment.Sha256 is null
                    || segment.Sha256.Length != 64 || !segment.Sha256.All(Uri.IsHexDigit))
                {
                    throw new InvalidDataException($"Invalid segment metadata: {file.Key}");
                }
            }
            foreach (string replaced in commit.Replaces) { ValidateObjectKey(prefix, replaced); }
            result.Add((id, commit));
        }
        return result;
    }

    private static void ValidateObjectKey(string prefix, string? key)
    {
        string expected = prefix + "/segments/";
        if (key is null || key.Length != expected.Length + 40 || !key.StartsWith(expected, StringComparison.Ordinal) || !key.EndsWith(".parquet", StringComparison.Ordinal)
            || !Guid.TryParseExact(key[expected.Length..^8], "N", out _))
        {
            throw new InvalidDataException($"Segment escapes its stream or has an invalid key: {key}");
        }
    }

    internal IReadOnlyList<ArchiveSegment> Active(string prefix)
    {
        RefuseLegacy(prefix);
        var commits = Commits(prefix);
        HashSet<string> replaced = new(StringComparer.Ordinal);
        Dictionary<string, (string Id, ArchiveCommit Commit)> owners = new(StringComparer.Ordinal);
        foreach (var (id, commit) in commits)
        {
            foreach (ArchiveSegment segment in commit.Segments)
            {
                if (!owners.TryAdd(segment.ObjectKey, (id, commit)))
                {
                    throw new InvalidDataException($"Segment appears in multiple commits: {segment.ObjectKey}");
                }
            }
            foreach (string key in commit.Replaces)
            {
                if (!replaced.Add(key))
                {
                    throw new InvalidDataException("Concurrent maintenance commits replace the same segment; explicit recovery is required.");
                }
            }
        }
        foreach (var (_, commit) in commits)
        {
            foreach (string key in commit.Replaces)
            {
                if (!owners.TryGetValue(key, out var owner) || owner.Commit.Sequence >= commit.Sequence)
                {
                    throw new InvalidDataException($"Manifest replaces an unknown or non-ancestor segment: {key}");
                }
            }
        }
        return [.. commits.OrderBy(c => c.Commit.Sequence).ThenBy(c => c.Id, StringComparer.Ordinal)
            .SelectMany(c => c.Commit.Segments).Where(s => !replaced.Contains(s.ObjectKey))
            .OrderBy(s => s.Start)];
    }

    internal void RefuseLegacy(string prefix)
    {
        if (store.List(prefix).Any(o => o.Key.EndsWith(".parquet", StringComparison.Ordinal)
            && !o.Key.StartsWith(prefix + "/segments/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Legacy archive files require explicit migration to a separate destination.");
        }
    }

    internal async Task<ArchiveSegment> StageAsync(string prefix, long start, long end, int rows, int ordinal,
        Func<Stream, Task> write, CancellationToken ct)
    {
        RefuseLegacy(prefix);
        string key = $"{prefix}/segments/{Guid.NewGuid():N}.parquet";
        await store.WriteAsync(key, write, ct).ConfigureAwait(false);
        await using Stream stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return new ArchiveSegment(key, start, end, rows, stream.Length, hash, ordinal);
    }

    internal async Task PublishAsync(string prefix, IReadOnlyList<ArchiveSegment> segments, string[] replaces,
        CancellationToken ct, string? id = null, string? sourceHash = null)
    {
        long sequence = checked(Commits(prefix).Select(c => c.Commit.Sequence).DefaultIfEmpty(-1).Max() + 1);
        ArchiveCommit commit = new(2, sequence, replaces, [.. segments], sourceHash);
        string key = $"{prefix}/commits/{id ?? Guid.NewGuid().ToString("N")}.json";
        // WriteAsync publishes only after the complete callback; WriteTextAsync is not atomic on every backend.
        await store.WriteAsync(key, stream => JsonSerializer.SerializeAsync(stream, commit, Json, ct), ct).ConfigureAwait(false);
    }

    internal async Task<Stream> OpenVerifiedAsync(ArchiveSegment segment, CancellationToken ct)
    {
        Stream stream = await store.OpenReadAsync(segment.ObjectKey, ct).ConfigureAwait(false);
        try
        {
            if (stream.Length != segment.Bytes
                || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)), segment.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Segment integrity mismatch: {segment.ObjectKey}");
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
