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
}

/// <summary>
/// Reads bars, quotes, and trades from CSV files using an instrument for precision.
/// </summary>
public static class CsvLoader
{
    public static IReadOnlyList<Bar> LoadBars(string path, Instrument instrument, BarType barType, CsvColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        columns ??= new CsvColumns();
        List<Bar> bars = new();
        foreach (Dictionary<string, string> row in ReadRows(path, columns.Separator))
        {
            UnixNanos ts = ParseTimestamp(Get(row, columns.Timestamp), columns.TimestampFormat);
            Price open = instrument.MakePrice(Dec(Get(row, columns.Open)));
            Price high = instrument.MakePrice(Dec(Get(row, columns.High)));
            Price low = instrument.MakePrice(Dec(Get(row, columns.Low)));
            Price close = instrument.MakePrice(Dec(Get(row, columns.Close)));
            Quantity volume = new(Dec(GetOptional(row, columns.Volume) ?? "0"), instrument.SizePrecision);
            bars.Add(new Bar(barType, open, high, low, close, volume, ts, ts));
        }

        // Stable, so rows sharing a timestamp leave the loader in the order the file had them.
        return bars.OrderBy(b => b.TsInit).ToList();
    }

    public static IReadOnlyList<QuoteTick> LoadQuoteTicks(string path, Instrument instrument, CsvColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        columns ??= new CsvColumns();
        List<QuoteTick> ticks = new();
        foreach (Dictionary<string, string> row in ReadRows(path, columns.Separator))
        {
            UnixNanos ts = ParseTimestamp(Get(row, columns.Timestamp), columns.TimestampFormat);
            Price bid = instrument.MakePrice(Dec(Get(row, columns.Bid)));
            Price ask = instrument.MakePrice(Dec(Get(row, columns.Ask)));
            Quantity bidSize = new(Dec(GetOptional(row, columns.BidSize) ?? "1"), instrument.SizePrecision);
            Quantity askSize = new(Dec(GetOptional(row, columns.AskSize) ?? "1"), instrument.SizePrecision);
            ticks.Add(new QuoteTick(instrument.Id, bid, ask, bidSize, askSize, ts, ts));
        }

        return ticks.OrderBy(t => t.TsInit).ToList();
    }

    public static IReadOnlyList<TradeTick> LoadTradeTicks(string path, Instrument instrument, CsvColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        columns ??= new CsvColumns();
        List<TradeTick> ticks = new();
        int counter = 0;
        foreach (Dictionary<string, string> row in ReadRows(path, columns.Separator))
        {
            UnixNanos ts = ParseTimestamp(Get(row, columns.Timestamp), columns.TimestampFormat);
            Price price = instrument.MakePrice(Dec(Get(row, columns.Price)));
            Quantity size = new(Dec(Get(row, columns.Size)), instrument.SizePrecision);
            AggressorSide aggressor = ParseSide(GetOptional(row, columns.Side));
            string tradeId = GetOptional(row, columns.TradeId) ?? (++counter).ToString(CultureInfo.InvariantCulture);
            ticks.Add(new TradeTick(instrument.Id, price, size, aggressor, new TradeId(tradeId), ts, ts));
        }

        return ticks.OrderBy(t => t.TsInit).ToList();
    }

    private static IEnumerable<Dictionary<string, string>> ReadRows(string path, char separator)
    {
        using StreamReader reader = new(path);
        string? header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }

        string[] names = header.Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToArray();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] values = line.Split(separator);
            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length && i < values.Length; i++)
            {
                row[names[i]] = values[i].Trim().Trim('"');
            }

            yield return row;
        }
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
