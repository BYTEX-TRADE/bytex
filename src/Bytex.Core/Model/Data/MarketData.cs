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
    MarketKey? MarketKey { get; }

    /// <summary>When the event occurred at its source.</summary>
    UnixNanos EventTime { get; }

    /// <summary>When the object was created in this process.</summary>
    UnixNanos CreatedTime { get; }
}

public readonly record struct QuoteTick(
    MarketKey MarketKey,
    Price Bid,
    Price Ask,
    Quantity BidSize,
    Quantity AskSize,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;

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

    public override string ToString() => $"{MarketKey},{Bid},{Ask},{BidSize},{AskSize},{EventTime}";
}

public readonly record struct TradeTick(
    MarketKey MarketKey,
    Price Price,
    Quantity Size,
    AggressorSide Aggressor,
    TradeId TradeId,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;

    public override string ToString() => $"{MarketKey},{Price},{Size},{Aggressor},{TradeId},{EventTime}";
}

/// <summary>
/// Describes how bars are built: step, aggregation method, and price source.
/// </summary>
public readonly record struct SamplingRule(int Step, SamplingMethod Aggregation, PriceType PriceType)
{
    public bool IsTimeAggregated => Aggregation.IsTimeBased();

    /// <summary>
    /// Whether this bar closes on how one-sided the flow has been rather than on how much of it there was: the imbalance
    /// and runs kinds (R2.12). The step is then a threshold of flow rather than a count, an amount or a length of time.
    /// </summary>
    public bool IsInformationBased() => Aggregation.IsInformationBased();

    /// <summary>Length of one bar in nanoseconds for time-based aggregations.</summary>
    public long IntervalNanos => Aggregation switch
    {
        SamplingMethod.Millisecond => Step * UnixNanos.NanosPerMillisecond,
        SamplingMethod.Second => Step * UnixNanos.NanosPerSecond,
        SamplingMethod.Minute => Step * UnixNanos.NanosPerMinute,
        SamplingMethod.Hour => Step * UnixNanos.NanosPerHour,
        SamplingMethod.Day => Step * UnixNanos.NanosPerDay,
        SamplingMethod.Week => Step * 7L * UnixNanos.NanosPerDay,
        SamplingMethod.Month => Step * UnixNanos.DaysPerMonth * UnixNanos.NanosPerDay,
        _ => throw new InvalidOperationException($"{Aggregation} is not time based."),
    };

    public TimeSpan Interval => TimeSpan.FromTicks(IntervalNanos / UnixNanos.NanosPerTick);

    public static SamplingRule Parse(string text)
    {
        string[] parts = text.Split('/');
        if (parts.Length != 4 || parts[0] != "bx-sampling:v2")
        {
            throw new FormatException("Sampling rules use bx-sampling:v2/<method>/<step>/<price>.");
        }

        int step = int.Parse(parts[2], CultureInfo.InvariantCulture);
        SamplingMethod method = ParseAggregation(parts[1]);
        PriceType price = Enum.Parse<PriceType>(parts[3], ignoreCase: true);
        if (step <= 0 || !Enum.IsDefined(method) || !Enum.IsDefined(price))
        {
            throw new FormatException("Sampling rules require a positive step and known method and price basis.");
        }

        return new SamplingRule(step, method, price);
    }

    /// <summary>
    /// An aggregation from its written form. The canonical one is what <see cref="ToString"/> writes - the name in
    /// lowercase, tickimbalance - and an underscore between the words is accepted as well, because that is how anybody
    /// writing one by hand writes it and the alternative is a stranger's first attempt failing on punctuation.
    /// </summary>
    internal static SamplingMethod ParseAggregation(string text) =>
        Enum.Parse<SamplingMethod>(text.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);

    public override string ToString() => $"bx-sampling:v2/{Aggregation.ToString().ToLowerInvariant()}/{Step.ToString(CultureInfo.InvariantCulture)}/{PriceType.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Identifies a stream of bars: instrument, specification, and whether bars come from the venue or are built locally.
/// </summary>
public readonly record struct CandleSeries(MarketKey MarketKey, SamplingRule Spec, CandleOrigin Source = CandleOrigin.Provider)
{
    public bool IsProvider => Source == CandleOrigin.Provider;

    public bool IsComputed => Source == CandleOrigin.Computed;

    /// <summary>
    /// Parses a versioned candle identity with escaped venue and symbol segments.
    /// </summary>
    public static CandleSeries Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string[] parts = text.Split('/');
        if (parts.Length != 7 || parts[0] != "bx-candle:v2")
        {
            throw new FormatException("Candle series use bx-candle:v2/<venue>/<symbol>/<method>/<step>/<price>/<origin>.");
        }

        MarketKey market = MarketKey.Parse($"bx-market:v2/{parts[1]}/{parts[2]}");
        SamplingRule rule = SamplingRule.Parse($"bx-sampling:v2/{parts[3]}/{parts[4]}/{parts[5]}");
        CandleOrigin origin = Enum.Parse<CandleOrigin>(parts[6], ignoreCase: true);
        if (!Enum.IsDefined(origin))
        {
            throw new FormatException("The candle origin is unknown.");
        }

        return new CandleSeries(market, rule, origin);
    }

    public static bool TryParse(string? text, out CandleSeries candleSeries)
    {
        candleSeries = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            candleSeries = Parse(text);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            // A step of more digits than an int holds is malformed text like any other.
            candleSeries = default;
            return false;
        }
    }

    public override string ToString() => $"bx-candle:v2/{Uri.EscapeDataString(MarketKey.Venue.Value)}/{Uri.EscapeDataString(MarketKey.Symbol.Value)}/{Spec.Aggregation.ToString().ToLowerInvariant()}/{Spec.Step.ToString(CultureInfo.InvariantCulture)}/{Spec.PriceType.ToString().ToLowerInvariant()}/{Source.ToString().ToLowerInvariant()}";
}

public readonly record struct Bar(
    CandleSeries CandleSeries,
    Price Open,
    Price High,
    Price Low,
    Price Close,
    Quantity Volume,
    UnixNanos EventTime,
    UnixNanos CreatedTime,
    bool IsRevision = false) : IData
{
    public MarketKey? MarketKey => CandleSeries.MarketKey;

    public bool IsSinglePrice => Open == High && High == Low && Low == Close;

    public override string ToString() => $"{CandleSeries},{Open},{High},{Low},{Close},{Volume},{EventTime}";
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
    MarketKey MarketKey,
    BookAction Action,
    BookOrder Order,
    RecordFlags Flags,
    ulong Sequence,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;

    public static OrderBookDelta Clear(MarketKey marketKey, ulong sequence, UnixNanos eventTime, UnixNanos createdTime) =>
        new(marketKey, BookAction.Clear, default, RecordFlags.None, sequence, eventTime, createdTime);
}

public sealed record OrderBookDeltas(
    MarketKey MarketKey,
    IReadOnlyList<OrderBookDelta> Deltas,
    RecordFlags Flags,
    ulong Sequence,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;

    public bool IsSnapshot => (Flags & RecordFlags.Snapshot) != 0;

    /// <inheritdoc cref="OrderBookDepth.Equals(OrderBookDepth)"/>
    public bool Equals(OrderBookDeltas? other) =>
        other is not null
        && MarketKey == other.MarketKey
        && Flags == other.Flags
        && Sequence == other.Sequence
        && EventTime == other.EventTime
        && CreatedTime == other.CreatedTime
        && Deltas.SequenceEqual(other.Deltas);

    /// <inheritdoc cref="OrderBookDepth.GetHashCode"/>
    public override int GetHashCode() => HashCode.Combine(MarketKey, Flags, Sequence, EventTime, CreatedTime, Deltas.Count);
}

public readonly record struct BookLevel(Price Price, Quantity Size, int Count = 1);

public sealed record OrderBookDepth(
    MarketKey MarketKey,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    RecordFlags Flags,
    ulong Sequence,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;

    /// <summary>
    /// Two books with the same levels are the same book.
    ///
    /// <para>
    /// This has to be written out because the two sides are lists. Every other market-data record is made of value
    /// types, so the equality the compiler generates for a record is right for them; here it would compare the two
    /// list REFERENCES, and two snapshots holding identical levels would never come out equal. Nothing threw and
    /// nothing reported a difference - the answer was always "different", so a caller that deduplicated a repeated
    /// snapshot quietly did nothing, and the obvious test failed with both sides printing the same.
    /// </para>
    /// </summary>
    public bool Equals(OrderBookDepth? other) =>
        other is not null
        && MarketKey == other.MarketKey
        && Flags == other.Flags
        && Sequence == other.Sequence
        && EventTime == other.EventTime
        && CreatedTime == other.CreatedTime
        && Bids.SequenceEqual(other.Bids)
        && Asks.SequenceEqual(other.Asks);

    /// <summary>
    /// Hashes the scalars and the two ladder LENGTHS, and deliberately not the levels themselves.
    ///
    /// <para>
    /// The contract a hash owes is that equal values hash alike, not that unequal ones differ, and everything hashed
    /// here is identical whenever <see cref="Equals(OrderBookDepth)"/> says yes. Walking fifty levels on every
    /// dictionary lookup to sharpen a hash would cost more than the collisions it avoids - and two books that agree
    /// on instrument, sequence and both timestamps are in practice the same book.
    /// </para>
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(MarketKey, Flags, Sequence, EventTime, CreatedTime, Bids.Count, Asks.Count);
}

public sealed record InstrumentStatus(
    MarketKey MarketKey,
    MarketStatus Status,
    string? Reason,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;
}

public sealed record MarkPriceUpdate(MarketKey MarketKey, Price Value, UnixNanos EventTime, UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;
}

public sealed record IndexPriceUpdate(MarketKey MarketKey, Price Value, UnixNanos EventTime, UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;
}

public sealed record FundingRateUpdate(MarketKey MarketKey, decimal Rate, UnixNanos? NextFundingTime, UnixNanos EventTime, UnixNanos CreatedTime) : IData
{
    MarketKey? IData.MarketKey => MarketKey;
}

/// <summary>
/// A named numeric signal published by an runtimeModule for other runtimeModules.
/// </summary>
public sealed record Signal(string Name, decimal Value, UnixNanos EventTime, UnixNanos CreatedTime) : IData
{
    public MarketKey? MarketKey => null;
}

/// <summary>
/// Base class for user-defined data types.
/// </summary>
public abstract record CustomData(UnixNanos EventTime, UnixNanos CreatedTime) : IData
{
    public virtual MarketKey? MarketKey => null;
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
