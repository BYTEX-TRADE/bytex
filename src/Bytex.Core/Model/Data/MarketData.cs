using System.Globalization;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Data;

/// <summary>
/// Anything that flows through the data engine.
/// </summary>
public interface IData
{
    /// <summary>The instrument this element relates to, if any.</summary>
    InstrumentId? InstrumentId { get; }

    /// <summary>When the event occurred at its source.</summary>
    UnixNanos TsEvent { get; }

    /// <summary>When the object was created in this process.</summary>
    UnixNanos TsInit { get; }
}

public readonly record struct QuoteTick(
    InstrumentId InstrumentId,
    Price Bid,
    Price Ask,
    Quantity BidSize,
    Quantity AskSize,
    UnixNanos TsEvent,
    UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;

    public Price Mid => new((Bid.Value + Ask.Value) / 2m, (byte)Math.Min(Bid.Precision + 1, Price.MaxPrecision));

    public decimal Spread => Ask.Value - Bid.Value;

    public Price ExtractPrice(PriceType type) => type switch
    {
        PriceType.Bid => Bid,
        PriceType.Ask => Ask,
        PriceType.Mid => Mid,
        PriceType.Last => Mid,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public Quantity ExtractSize(PriceType type) => type switch
    {
        PriceType.Bid => BidSize,
        PriceType.Ask => AskSize,
        _ => new Quantity((BidSize.Value + AskSize.Value) / 2m, BidSize.Precision),
    };

    public override string ToString() => $"{InstrumentId},{Bid},{Ask},{BidSize},{AskSize},{TsEvent}";
}

public readonly record struct TradeTick(
    InstrumentId InstrumentId,
    Price Price,
    Quantity Size,
    AggressorSide Aggressor,
    TradeId TradeId,
    UnixNanos TsEvent,
    UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;

    public override string ToString() => $"{InstrumentId},{Price},{Size},{Aggressor},{TradeId},{TsEvent}";
}

/// <summary>
/// Describes how bars are built: step, aggregation method, and price source.
/// </summary>
public readonly record struct BarSpecification(int Step, BarAggregation Aggregation, PriceType PriceType)
{
    public bool IsTimeAggregated => Aggregation.IsTimeBased();

    /// <summary>Length of one bar in nanoseconds for time-based aggregations.</summary>
    public long IntervalNanos => Aggregation switch
    {
        BarAggregation.Millisecond => Step * UnixNanos.NanosPerMillisecond,
        BarAggregation.Second => Step * UnixNanos.NanosPerSecond,
        BarAggregation.Minute => Step * UnixNanos.NanosPerMinute,
        BarAggregation.Hour => Step * UnixNanos.NanosPerHour,
        BarAggregation.Day => Step * UnixNanos.NanosPerDay,
        BarAggregation.Week => Step * 7L * UnixNanos.NanosPerDay,
        BarAggregation.Month => Step * 30L * UnixNanos.NanosPerDay,
        _ => throw new InvalidOperationException($"{Aggregation} is not time based."),
    };

    public TimeSpan Interval => TimeSpan.FromTicks(IntervalNanos / 100);

    public static BarSpecification Parse(string text)
    {
        string[] parts = text.Split('-');
        if (parts.Length != 3)
        {
            throw new FormatException($"BarSpecification '{text}' must be '<step>-<aggregation>-<price_type>'.");
        }

        return new BarSpecification(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            Enum.Parse<BarAggregation>(parts[1], ignoreCase: true),
            Enum.Parse<PriceType>(parts[2], ignoreCase: true));
    }

    public override string ToString() => $"{Step}-{Aggregation.ToString().ToUpperInvariant()}-{PriceType.ToString().ToUpperInvariant()}";
}

/// <summary>
/// Identifies a stream of bars: instrument, specification, and whether bars come from the venue or are built locally.
/// </summary>
public readonly record struct BarType(InstrumentId InstrumentId, BarSpecification Spec, AggregationSource Source = AggregationSource.External)
{
    public bool IsExternal => Source == AggregationSource.External;

    public bool IsInternal => Source == AggregationSource.Internal;

    /// <summary>
    /// Parses "BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL".
    /// </summary>
    public static BarType Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string[] parts = text.Split('-');
        if (parts.Length < 5)
        {
            throw new FormatException($"BarType '{text}' must be '<instrument_id>-<step>-<aggregation>-<price_type>-<source>'.");
        }

        string instrumentText = string.Join('-', parts.Take(parts.Length - 4));
        int n = parts.Length;
        BarSpecification spec = new(
            int.Parse(parts[n - 4], CultureInfo.InvariantCulture),
            Enum.Parse<BarAggregation>(parts[n - 3], ignoreCase: true),
            Enum.Parse<PriceType>(parts[n - 2], ignoreCase: true));
        AggregationSource source = Enum.Parse<AggregationSource>(parts[n - 1], ignoreCase: true);
        return new BarType(InstrumentId.Parse(instrumentText), spec, source);
    }

    public static bool TryParse(string? text, out BarType barType)
    {
        barType = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            barType = Parse(text);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }

    public override string ToString() => $"{InstrumentId}-{Spec}-{Source.ToString().ToUpperInvariant()}";
}

public readonly record struct Bar(
    BarType BarType,
    Price Open,
    Price High,
    Price Low,
    Price Close,
    Quantity Volume,
    UnixNanos TsEvent,
    UnixNanos TsInit,
    bool IsRevision = false) : IData
{
    public InstrumentId? InstrumentId => BarType.InstrumentId;

    public bool IsSinglePrice => Open == High && High == Low && Low == Close;

    public override string ToString() => $"{BarType},{Open},{High},{Low},{Close},{Volume},{TsEvent}";
}

public readonly record struct BookOrder(OrderSide Side, Price Price, Quantity Size, ulong OrderId)
{
    public decimal Exposure => Price.Value * Size.Value;

    public override string ToString() => $"{Side} {Size} @ {Price} (#{OrderId})";
}

[Flags]
public enum RecordFlags : byte
{
    None = 0,
    Last = 1 << 7,
    TopOfBook = 1 << 6,
    Snapshot = 1 << 5,
    MarketByOrder = 1 << 4,
}

public readonly record struct OrderBookDelta(
    InstrumentId InstrumentId,
    BookAction Action,
    BookOrder Order,
    RecordFlags Flags,
    ulong Sequence,
    UnixNanos TsEvent,
    UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;

    public static OrderBookDelta Clear(InstrumentId instrumentId, ulong sequence, UnixNanos tsEvent, UnixNanos tsInit) =>
        new(instrumentId, BookAction.Clear, default, RecordFlags.None, sequence, tsEvent, tsInit);
}

public sealed record OrderBookDeltas(
    InstrumentId InstrumentId,
    IReadOnlyList<OrderBookDelta> Deltas,
    RecordFlags Flags,
    ulong Sequence,
    UnixNanos TsEvent,
    UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;

    public bool IsSnapshot => (Flags & RecordFlags.Snapshot) != 0;
}

public readonly record struct BookLevel(Price Price, Quantity Size, int Count = 1);

public sealed record OrderBookDepth(
    InstrumentId InstrumentId,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    RecordFlags Flags,
    ulong Sequence,
    UnixNanos TsEvent,
    UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;
}

public sealed record InstrumentStatus(
    InstrumentId InstrumentId,
    MarketStatus Status,
    string? Reason,
    UnixNanos TsEvent,
    UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;
}

public sealed record MarkPriceUpdate(InstrumentId InstrumentId, Price Value, UnixNanos TsEvent, UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;
}

public sealed record IndexPriceUpdate(InstrumentId InstrumentId, Price Value, UnixNanos TsEvent, UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;
}

public sealed record FundingRateUpdate(InstrumentId InstrumentId, decimal Rate, UnixNanos? NextFundingTime, UnixNanos TsEvent, UnixNanos TsInit) : IData
{
    InstrumentId? IData.InstrumentId => InstrumentId;
}

/// <summary>
/// A named numeric signal published by an actor for other actors.
/// </summary>
public sealed record Signal(string Name, decimal Value, UnixNanos TsEvent, UnixNanos TsInit) : IData
{
    public InstrumentId? InstrumentId => null;
}

/// <summary>
/// Base class for user-defined data types.
/// </summary>
public abstract record CustomData(UnixNanos TsEvent, UnixNanos TsInit) : IData
{
    public virtual InstrumentId? InstrumentId => null;
}

/// <summary>
/// Key for subscribing to data by CLR type plus optional metadata (e.g. an instrument or a parameter).
/// </summary>
public sealed class DataType : IEquatable<DataType>
{
    public DataType(Type type, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Topic = BuildTopic(type, Metadata);
    }

    public Type Type { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>Stable string used as a message-bus topic segment.</summary>
    public string Topic { get; }

    public static DataType Of<T>(IReadOnlyDictionary<string, string>? metadata = null) => new(typeof(T), metadata);

    private static string BuildTopic(Type type, IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.Count == 0)
        {
            return type.Name;
        }

        IEnumerable<string> pairs = metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value);
        return type.Name + "." + string.Join('.', pairs);
    }

    public bool Equals(DataType? other) => other is not null && string.Equals(Topic, other.Topic, StringComparison.Ordinal) && Type == other.Type;

    public override bool Equals(object? obj) => Equals(obj as DataType);

    public override int GetHashCode() => HashCode.Combine(Type, Topic);

    public override string ToString() => Topic;
}
