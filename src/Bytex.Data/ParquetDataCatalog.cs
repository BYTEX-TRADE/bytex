using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Parquet.Serialization;

namespace Bytex.Data;

/// <summary>
/// File-based catalog of market data stored as Parquet, organised as
/// <c>{root}/{kind}/{key}/{start}-{end}.parquet</c> where the key is an instrument id or bar type.
/// </summary>
public sealed class ParquetDataCatalog
{
    private const string InstrumentsDir = "instruments";
    private const string QuotesDir = "quotes";
    private const string TradesDir = "trades";
    private const string BarsDir = "bars";
    private const string DeltasDir = "book_deltas";

    public ParquetDataCatalog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    // ----- Instruments -----

    public async Task WriteInstrumentsAsync(IEnumerable<Instrument> instruments, CancellationToken ct = default)
    {
        string dir = Path.Combine(RootPath, InstrumentsDir);
        Directory.CreateDirectory(dir);
        foreach (Instrument instrument in instruments)
        {
            string path = Path.Combine(dir, SafeName(instrument.Id.Value) + ".json");
            await File.WriteAllTextAsync(path, InstrumentJson.Serialize(instrument), ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<Instrument> Instruments(Venue? venue = null)
    {
        string dir = Path.Combine(RootPath, InstrumentsDir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        List<Instrument> result = new();
        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            Instrument instrument = InstrumentJson.Deserialize(File.ReadAllText(file));
            if (venue is null || instrument.Venue == venue.Value)
            {
                result.Add(instrument);
            }
        }

        return result;
    }

    public Instrument? Instrument(InstrumentId id)
    {
        string path = Path.Combine(RootPath, InstrumentsDir, SafeName(id.Value) + ".json");
        return File.Exists(path) ? InstrumentJson.Deserialize(File.ReadAllText(path)) : null;
    }

    // ----- Writing -----

    public Task WriteQuoteTicksAsync(IEnumerable<QuoteTick> ticks, CancellationToken ct = default) =>
        WriteGroupedAsync(ticks, t => t.InstrumentId.Value, t => t.TsInit, QuotesDir, QuoteRow.From, ct);

    public Task WriteTradeTicksAsync(IEnumerable<TradeTick> ticks, CancellationToken ct = default) =>
        WriteGroupedAsync(ticks, t => t.InstrumentId.Value, t => t.TsInit, TradesDir, TradeRow.From, ct);

    public Task WriteBarsAsync(IEnumerable<Bar> bars, CancellationToken ct = default) =>
        WriteGroupedAsync(bars, b => b.BarType.ToString(), b => b.TsInit, BarsDir, BarRow.From, ct);

    public Task WriteOrderBookDeltasAsync(IEnumerable<OrderBookDelta> deltas, CancellationToken ct = default) =>
        WriteGroupedAsync(deltas, d => d.InstrumentId.Value, d => d.TsInit, DeltasDir, DeltaRow.From, ct);

    public async Task WriteAsync(IEnumerable<IData> data, CancellationToken ct = default)
    {
        List<IData> list = data.ToList();
        await WriteQuoteTicksAsync(list.OfType<QuoteTick>(), ct).ConfigureAwait(false);
        await WriteTradeTicksAsync(list.OfType<TradeTick>(), ct).ConfigureAwait(false);
        await WriteBarsAsync(list.OfType<Bar>(), ct).ConfigureAwait(false);
        await WriteOrderBookDeltasAsync(list.OfType<OrderBookDelta>(), ct).ConfigureAwait(false);
    }

    private async Task WriteGroupedAsync<T, TRow>(IEnumerable<T> items, Func<T, string> key, Func<T, UnixNanos> ts, string kind, Func<T, TRow> map, CancellationToken ct) where TRow : class, new()
    {
        foreach (IGrouping<string, T> group in items.GroupBy(key))
        {
            List<T> sorted = group.OrderBy(ts).ToList();
            if (sorted.Count == 0)
            {
                continue;
            }

            string dir = Path.Combine(RootPath, kind, SafeName(group.Key));
            Directory.CreateDirectory(dir);
            long start = ts(sorted[0]).Value;
            long end = ts(sorted[^1]).Value;
            string path = Path.Combine(dir, $"{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}.parquet");
            int suffix = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, $"{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}-{suffix++}.parquet");
            }

            List<TRow> rows = sorted.Select(map).ToList();
            await using FileStream stream = File.Create(path);
            await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    // ----- Reading -----

    public Task<IReadOnlyList<QuoteTick>> QuoteTicksAsync(InstrumentId instrumentId, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<QuoteRow, QuoteTick>(QuotesDir, instrumentId.Value, start, end, r => r.ToTick(instrumentId), t => t.TsInit, ct);

    public Task<IReadOnlyList<TradeTick>> TradeTicksAsync(InstrumentId instrumentId, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<TradeRow, TradeTick>(TradesDir, instrumentId.Value, start, end, r => r.ToTick(instrumentId), t => t.TsInit, ct);

    public Task<IReadOnlyList<Bar>> BarsAsync(BarType barType, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<BarRow, Bar>(BarsDir, barType.ToString(), start, end, r => r.ToBar(barType), b => b.TsInit, ct);

    public Task<IReadOnlyList<OrderBookDelta>> OrderBookDeltasAsync(InstrumentId instrumentId, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<DeltaRow, OrderBookDelta>(DeltasDir, instrumentId.Value, start, end, r => r.ToDelta(instrumentId), d => d.TsInit, ct);

    private async Task<IReadOnlyList<TData>> ReadAsync<TRow, TData>(string kind, string key, UnixNanos? start, UnixNanos? end, Func<TRow, TData> map, Func<TData, UnixNanos> ts, CancellationToken ct) where TRow : class, new()
    {
        string dir = Path.Combine(RootPath, kind, SafeName(key));
        if (!Directory.Exists(dir))
        {
            return [];
        }

        List<TData> result = new();
        foreach (string file in Directory.GetFiles(dir, "*.parquet").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!Overlaps(file, start, end))
            {
                continue;
            }

            await using FileStream stream = File.OpenRead(file);
            DeserializationResult<TRow> rows = await ParquetSerializer.DeserializeAsync<TRow>(stream, cancellationToken: ct).ConfigureAwait(false);
            foreach (TRow row in rows.Data)
            {
                TData item = map(row);
                UnixNanos t = ts(item);
                if (start is { } s && t < s)
                {
                    continue;
                }

                if (end is { } e && t > e)
                {
                    continue;
                }

                result.Add(item);
            }
        }

        result.Sort((a, b) => ts(a).CompareTo(ts(b)));
        return result;
    }

    private static bool Overlaps(string file, UnixNanos? start, UnixNanos? end)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string[] parts = name.Split('-');
        if (parts.Length < 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileStart) || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileEnd))
        {
            return true;
        }

        if (start is { } s && fileEnd < s.Value)
        {
            return false;
        }

        if (end is { } e && fileStart > e.Value)
        {
            return false;
        }

        return true;
    }

    // ----- Listing -----

    public IReadOnlyList<InstrumentId> QuoteTickInstruments() => ListKeys(QuotesDir).Select(InstrumentId.Parse).ToList();

    public IReadOnlyList<InstrumentId> TradeTickInstruments() => ListKeys(TradesDir).Select(InstrumentId.Parse).ToList();

    public IReadOnlyList<BarType> BarTypes() => ListKeys(BarsDir).Select(BarType.Parse).ToList();

    public IReadOnlyList<InstrumentId> OrderBookDeltaInstruments() => ListKeys(DeltasDir).Select(InstrumentId.Parse).ToList();

    public IReadOnlyList<CatalogEntry> Entries()
    {
        List<CatalogEntry> entries = new();
        foreach ((string kind, string label) in new[] { (QuotesDir, "quotes"), (TradesDir, "trades"), (BarsDir, "bars"), (DeltasDir, "book_deltas") })
        {
            string kindDir = Path.Combine(RootPath, kind);
            if (!Directory.Exists(kindDir))
            {
                continue;
            }

            foreach (string keyDir in Directory.GetDirectories(kindDir))
            {
                string[] files = Directory.GetFiles(keyDir, "*.parquet");
                long min = long.MaxValue;
                long max = long.MinValue;
                long size = 0;
                foreach (string file in files)
                {
                    string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
                    if (parts.Length >= 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long e))
                    {
                        min = Math.Min(min, s);
                        max = Math.Max(max, e);
                    }

                    size += new FileInfo(file).Length;
                }

                entries.Add(new CatalogEntry(label, UnsafeName(Path.GetFileName(keyDir)), files.Length, files.Length == 0 ? null : new UnixNanos(min), files.Length == 0 ? null : new UnixNanos(max), size));
            }
        }

        return entries;
    }

    private IEnumerable<string> ListKeys(string kind)
    {
        string dir = Path.Combine(RootPath, kind);
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(d => UnsafeName(Path.GetFileName(d))) : [];
    }

    private static string SafeName(string key) => key.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    private static string UnsafeName(string name) => name;

    // ----- Row types -----

    public sealed class QuoteRow
    {
        public long TsEvent { get; set; }

        public long TsInit { get; set; }

        public decimal Bid { get; set; }

        public decimal Ask { get; set; }

        public decimal BidSize { get; set; }

        public decimal AskSize { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static QuoteRow From(QuoteTick t) => new()
        {
            TsEvent = t.TsEvent.Value,
            TsInit = t.TsInit.Value,
            Bid = t.Bid.Value,
            Ask = t.Ask.Value,
            BidSize = t.BidSize.Value,
            AskSize = t.AskSize.Value,
            PricePrecision = t.Bid.Precision,
            SizePrecision = t.BidSize.Precision,
        };

        public QuoteTick ToTick(InstrumentId id) => new(id, new Price(Bid, PricePrecision), new Price(Ask, PricePrecision), new Quantity(BidSize, SizePrecision), new Quantity(AskSize, SizePrecision), new UnixNanos(TsEvent), new UnixNanos(TsInit));
    }

    public sealed class TradeRow
    {
        public long TsEvent { get; set; }

        public long TsInit { get; set; }

        public decimal Price { get; set; }

        public decimal Size { get; set; }

        public int Aggressor { get; set; }

        public string TradeId { get; set; } = string.Empty;

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static TradeRow From(TradeTick t) => new()
        {
            TsEvent = t.TsEvent.Value,
            TsInit = t.TsInit.Value,
            Price = t.Price.Value,
            Size = t.Size.Value,
            Aggressor = (int)t.Aggressor,
            TradeId = t.TradeId.Value,
            PricePrecision = t.Price.Precision,
            SizePrecision = t.Size.Precision,
        };

        public TradeTick ToTick(InstrumentId id) => new(id, new Price(Price, PricePrecision), new Quantity(Size, SizePrecision), (AggressorSide)Aggressor, new TradeId(TradeId), new UnixNanos(TsEvent), new UnixNanos(TsInit));
    }

    public sealed class BarRow
    {
        public long TsEvent { get; set; }

        public long TsInit { get; set; }

        public decimal Open { get; set; }

        public decimal High { get; set; }

        public decimal Low { get; set; }

        public decimal Close { get; set; }

        public decimal Volume { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static BarRow From(Bar b) => new()
        {
            TsEvent = b.TsEvent.Value,
            TsInit = b.TsInit.Value,
            Open = b.Open.Value,
            High = b.High.Value,
            Low = b.Low.Value,
            Close = b.Close.Value,
            Volume = b.Volume.Value,
            PricePrecision = b.Close.Precision,
            SizePrecision = b.Volume.Precision,
        };

        public Bar ToBar(BarType barType) => new(barType, new Price(Open, PricePrecision), new Price(High, PricePrecision), new Price(Low, PricePrecision), new Price(Close, PricePrecision), new Quantity(Volume, SizePrecision), new UnixNanos(TsEvent), new UnixNanos(TsInit));
    }

    public sealed class DeltaRow
    {
        public long TsEvent { get; set; }

        public long TsInit { get; set; }

        public int Action { get; set; }

        public int Side { get; set; }

        public decimal Price { get; set; }

        public decimal Size { get; set; }

        public long OrderId { get; set; }

        public byte Flags { get; set; }

        public long Sequence { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static DeltaRow From(OrderBookDelta d) => new()
        {
            TsEvent = d.TsEvent.Value,
            TsInit = d.TsInit.Value,
            Action = (int)d.Action,
            Side = (int)d.Order.Side,
            Price = d.Order.Price.Value,
            Size = d.Order.Size.Value,
            OrderId = (long)d.Order.OrderId,
            Flags = (byte)d.Flags,
            Sequence = (long)d.Sequence,
            PricePrecision = d.Order.Price.Precision,
            SizePrecision = d.Order.Size.Precision,
        };

        public OrderBookDelta ToDelta(InstrumentId id) => new(id, (BookAction)Action, new BookOrder((OrderSide)Side, new Price(Price, PricePrecision), new Quantity(Size, SizePrecision), (ulong)OrderId), (RecordFlags)Flags, (ulong)Sequence, new UnixNanos(TsEvent), new UnixNanos(TsInit));
    }
}

public sealed record CatalogEntry(string Kind, string Key, int FileCount, UnixNanos? Start, UnixNanos? End, long SizeBytes);
