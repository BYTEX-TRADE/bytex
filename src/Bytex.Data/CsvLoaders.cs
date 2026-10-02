using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Data;

/// <summary>
/// Column mapping for CSV files. Names are matched case-insensitively against the header row.
/// </summary>
public sealed record CsvColumns
{
    public string Timestamp { get; init; } = "timestamp";

    public string Open { get; init; } = "open";

    public string High { get; init; } = "high";

    public string Low { get; init; } = "low";

    public string Close { get; init; } = "close";

    public string Volume { get; init; } = "volume";

    public string Bid { get; init; } = "bid";

    public string Ask { get; init; } = "ask";

    public string BidSize { get; init; } = "bid_size";

    public string AskSize { get; init; } = "ask_size";

    public string Price { get; init; } = "price";

    public string Size { get; init; } = "size";

    public string Side { get; init; } = "side";

    public string TradeId { get; init; } = "trade_id";

    public char Separator { get; init; } = ',';

    /// <summary>Timestamp format: "iso", "unix_s", "unix_ms", "unix_us", "unix_ns", or a .NET format string.</summary>
    public string TimestampFormat { get; init; } = "iso";

    /// <summary>
    /// False for a file whose first line is already data, which is most of what an exchange hands out of its own
    /// archive. Columns are then addressed by position - <c>Timestamp = "0"</c>, <c>Open = "1"</c> - because a file
    /// with no header has no names to match, and inventing some would only move the guess.
    /// </summary>
    public bool HasHeader { get; init; } = true;
}

/// <summary>
/// Reads bars, quotes, and trades from CSV files using an instrument for precision.
/// </summary>
public static class CsvLoader
{
    public static IReadOnlyList<Bar> LoadBars(string path, Instrument instrument, CandleSeries candleSeries, CsvColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        columns ??= new CsvColumns();
        List<Bar> bars = new();
        foreach (Dictionary<string, string> row in ReadRows(path, columns.Separator, columns.HasHeader))
        {
            UnixNanos ts = ParseTimestamp(Get(row, columns.Timestamp), columns.TimestampFormat);
            Price open = instrument.MakePrice(Dec(Get(row, columns.Open)));
            Price high = instrument.MakePrice(Dec(Get(row, columns.High)));
            Price low = instrument.MakePrice(Dec(Get(row, columns.Low)));
            Price close = instrument.MakePrice(Dec(Get(row, columns.Close)));
            Quantity volume = new(Dec(GetOptional(row, columns.Volume) ?? "0"), instrument.SizePrecision);
            bars.Add(new Bar(candleSeries, open, high, low, close, volume, ts, ts));
        }

        // Stable, so rows sharing a timestamp leave the loader in the order the file had them.
        return bars.OrderBy(b => b.CreatedTime).ToList();
    }

    public static IReadOnlyList<QuoteTick> LoadQuoteTicks(string path, Instrument instrument, CsvColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        columns ??= new CsvColumns();
        List<QuoteTick> ticks = new();
        foreach (Dictionary<string, string> row in ReadRows(path, columns.Separator, columns.HasHeader))
        {
            UnixNanos ts = ParseTimestamp(Get(row, columns.Timestamp), columns.TimestampFormat);
            Price bid = instrument.MakePrice(Dec(Get(row, columns.Bid)));
            Price ask = instrument.MakePrice(Dec(Get(row, columns.Ask)));
            Quantity bidSize = new(Dec(GetOptional(row, columns.BidSize) ?? "1"), instrument.SizePrecision);
            Quantity askSize = new(Dec(GetOptional(row, columns.AskSize) ?? "1"), instrument.SizePrecision);
            ticks.Add(new QuoteTick(instrument.Id, bid, ask, bidSize, askSize, ts, ts));
        }

        return ticks.OrderBy(t => t.CreatedTime).ToList();
    }

    public static IReadOnlyList<TradeTick> LoadTradeTicks(string path, Instrument instrument, CsvColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        columns ??= new CsvColumns();
        List<TradeTick> ticks = new();
        int counter = 0;
        foreach (Dictionary<string, string> row in ReadRows(path, columns.Separator, columns.HasHeader))
        {
            UnixNanos ts = ParseTimestamp(Get(row, columns.Timestamp), columns.TimestampFormat);
            Price price = instrument.MakePrice(Dec(Get(row, columns.Price)));
            Quantity size = new(Dec(Get(row, columns.Size)), instrument.SizePrecision);
            AggressorSide aggressor = ParseSide(GetOptional(row, columns.Side));
            string tradeId = GetOptional(row, columns.TradeId) ?? (++counter).ToString(CultureInfo.InvariantCulture);
            ticks.Add(new TradeTick(instrument.Id, price, size, aggressor, new TradeId(tradeId), ts, ts));
        }

        return ticks.OrderBy(t => t.CreatedTime).ToList();
    }

    private static IEnumerable<Dictionary<string, string>> ReadRows(string path, char separator, bool hasHeader = true)
    {
        using StreamReader reader = new(path);
        string[]? names = null;

        if (hasHeader)
        {
            if (reader.ReadLine() is not { } header)
            {
                yield break;
            }

            names = [.. Split(header, separator).Select(h => h.ToLowerInvariant())];
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] values = Split(line, separator);

            // With no header the columns are their own positions, so a caller names them "0", "1" and so on. The
            // names are built per row rather than once, because a headerless file may have ragged rows and a column
            // that is not there must be absent rather than holding a previous row's value.
            string[] keys = names ?? [.. Enumerable.Range(0, values.Length).Select(i => i.ToString(CultureInfo.InvariantCulture))];

            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Length && i < values.Length; i++)
            {
                row[keys[i]] = values[i];
            }

            yield return row;
        }
    }

    /// <summary>
    /// One CSV line as its fields, honouring quotes.
    ///
    /// <para>
    /// Splitting on the separator was wrong for any file that quotes its fields, which is what quoting is for: a
    /// value containing the separator shifted every column after it, and the row still parsed - into a price taken
    /// from something that was never a price. A doubled quote inside a quoted field is one quote, as every writer of
    /// these files means it.
    /// </para>
    /// </summary>
    internal static string[] Split(string line, char separator)
    {
        List<string> fields = new();
        System.Text.StringBuilder current = new();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    current.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return [.. fields];
    }

    private static string Get(Dictionary<string, string> row, string column) =>
        row.TryGetValue(column, out string? value) ? value : throw new FormatException($"CSV is missing column '{column}'.");

    private static string? GetOptional(Dictionary<string, string> row, string column) => row.TryGetValue(column, out string? value) ? value : null;

    private static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static AggressorSide ParseSide(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "BUY" or "BUYER" or "B" or "1" => AggressorSide.Buyer,
        "SELL" or "SELLER" or "S" or "2" => AggressorSide.Seller,
        _ => AggressorSide.None,
    };

    public static UnixNanos ParseTimestamp(string text, string format)
    {
        switch (format)
        {
            case "iso":
                return UnixNanos.Parse(text);
            case "unix_s":
                return UnixNanos.FromSeconds(long.Parse(text, CultureInfo.InvariantCulture));
            case "unix_ms":
                return UnixNanos.FromMilliseconds(long.Parse(text, CultureInfo.InvariantCulture));
            case "unix_us":
                return UnixNanos.FromMicroseconds(long.Parse(text, CultureInfo.InvariantCulture));
            case "unix_ns":
                return new UnixNanos(long.Parse(text, CultureInfo.InvariantCulture));
            default:
                DateTime parsed = DateTime.ParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                return UnixNanos.FromDateTime(parsed);
        }
    }
}
