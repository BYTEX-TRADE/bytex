using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Bytex.Core.Engines;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Indicators;

namespace Bytex.Benchmarks;

/// <summary>
/// The numbers that are published (R8/1.0).
///
/// <para>
/// What is measured is deliberately the small things the engine does millions of times, not a whole backtest: a
/// backtest's time is dominated by whatever data it was given, so a number for one says more about the data than about
/// the engine. These say how much the engine itself costs per event, which is what somebody sizing a run needs.
/// </para>
/// </summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

public abstract class BenchmarkBase
{
    protected static readonly MarketKey Instrument = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");

    protected static readonly UnixNanos T0 = UnixNanos.FromSeconds(1_700_000_000);

    protected static CurrencyPair Spot() => new(new InstrumentSpec
    {
        Id = Instrument,
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        BaseCurrency = Currencies.BTC,
        QuoteCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
    });

    protected static QuoteTick Quote(decimal bid, int i) => new(
        Instrument,
        new Price(bid, 2),
        new Price(bid + 0.01m, 2),
        new Quantity(1m, 3),
        new Quantity(1m, 3),
        T0.AddNanos(i * UnixNanos.NanosPerMillisecond),
        T0.AddNanos(i * UnixNanos.NanosPerMillisecond));

    protected static TradeTick Trade(decimal price, int i) => new(
        Instrument,
        new Price(price, 2),
        new Quantity(0.01m, 3),
        i % 2 == 0 ? AggressorSide.Buyer : AggressorSide.Seller,
        new TradeId("T" + i),
        T0.AddNanos(i * UnixNanos.NanosPerMillisecond),
        T0.AddNanos(i * UnixNanos.NanosPerMillisecond));
}

/// <summary>What one message on the bus costs, which is the floor under every event the engine handles.</summary>
[MemoryDiagnoser]
public class MessageBusBenchmarks : BenchmarkBase
{
    private MessageBus _bus = null!;
    private QuoteTick _tick;

    [Params(1, 4)]
    public int Subscribers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bus = new MessageBus(new ModuleHostId("BENCH-001"));
        _tick = Quote(50_000m, 0);
        for (int i = 0; i < Subscribers; i++)
        {
            _bus.Subscribe("data.quotes.BINANCE.BTCUSDT", _ => { });
        }
    }

    [Benchmark(Description = "Publish one quote to an exact topic")]
    public void PublishExact() => _bus.Publish("data.quotes.BINANCE.BTCUSDT", _tick);
}

/// <summary>What a wildcard subscription costs against an exact one, since most subscriptions are patterns.</summary>
[MemoryDiagnoser]
public class MessageBusPatternBenchmarks : BenchmarkBase
{
    private MessageBus _bus = null!;
    private QuoteTick _tick;

    [GlobalSetup]
    public void Setup()
    {
        _bus = new MessageBus(new ModuleHostId("BENCH-001"));
        _tick = Quote(50_000m, 0);
        _bus.Subscribe("data.quotes.*", _ => { });
    }

    [Benchmark(Description = "Publish one quote to a wildcard subscriber")]
    public void PublishWildcard() => _bus.Publish("data.quotes.BINANCE.BTCUSDT", _tick);
}

/// <summary>Bar aggregation: the work behind every internally built bar.</summary>
[MemoryDiagnoser]
public class SamplingMethodBenchmarks : BenchmarkBase
{
    private const int Ticks = 10_000;

    private TradeTick[] _trades = null!;
    private CurrencyPair _instrument = null!;

    [GlobalSetup]
    public void Setup()
    {
        _instrument = Spot();
        _trades = new TradeTick[Ticks];
        for (int i = 0; i < Ticks; i++)
        {
            _trades[i] = Trade(50_000m + (i % 100 * 0.01m), i);
        }
    }

    [Benchmark(Description = "Ten thousand trades into tick bars of a hundred")]
    public int TickBars()
    {
        int bars = 0;
        TickBarAggregator aggregator = new(
            _instrument,
            new CandleSeries(Instrument, new SamplingRule(100, SamplingMethod.Tick, PriceType.Last), CandleOrigin.Computed),
            _ => bars++,
            new TestClock(T0));

        foreach (TradeTick trade in _trades)
        {
            aggregator.HandleTradeTick(trade);
        }

        return bars;
    }

    [Benchmark(Description = "Ten thousand trades into volume-imbalance bars")]
    public int ImbalanceBars()
    {
        int bars = 0;
        ImbalanceBarAggregator aggregator = new(
            _instrument,
            new CandleSeries(Instrument, new SamplingRule(1, SamplingMethod.VolumeImbalance, PriceType.Last), CandleOrigin.Computed),
            _ => bars++,
            new TestClock(T0));

        foreach (TradeTick trade in _trades)
        {
            aggregator.HandleTradeTick(trade);
        }

        return bars;
    }
}

/// <summary>An order book under a stream of deltas, which is the most expensive data a live node takes.</summary>
[MemoryDiagnoser]
public class OrderBookBenchmarks : BenchmarkBase
{
    private const int Updates = 10_000;

    private OrderBookDelta[] _deltas = null!;

    [GlobalSetup]
    public void Setup()
    {
        _deltas = new OrderBookDelta[Updates];
        for (int i = 0; i < Updates; i++)
        {
            bool bid = i % 2 == 0;
            decimal price = bid ? 50_000m - (i % 20 * 0.01m) : 50_001m + (i % 20 * 0.01m);
            _deltas[i] = new OrderBookDelta(
                Instrument,
                BookAction.Update,
                new BookOrder(bid ? OrderSide.Buy : OrderSide.Sell, new Price(price, 2), new Quantity(1m + (i % 5), 3), (ulong)i),
                RecordFlags.None,
                (ulong)i,
                T0.AddNanos(i * UnixNanos.NanosPerMillisecond),
                T0.AddNanos(i * UnixNanos.NanosPerMillisecond));
        }
    }

    [Benchmark(Description = "Ten thousand deltas applied to a book")]
    public decimal? ApplyDeltas()
    {
        OrderBook book = new(Instrument, BookType.L2);
        foreach (OrderBookDelta delta in _deltas)
        {
            book.Apply(delta);
        }

        return book.MidPrice;
    }
}

/// <summary>Indicators, which a strategy registers by the dozen and which see every bar.</summary>
[MemoryDiagnoser]
public class IndicatorBenchmarks : BenchmarkBase
{
    private const int Bars = 10_000;

    private Bar[] _bars = null!;

    [GlobalSetup]
    public void Setup()
    {
        CandleSeries candleSeries = new(Instrument, new SamplingRule(1, SamplingMethod.Minute, PriceType.Last), CandleOrigin.Provider);
        _bars = new Bar[Bars];
        for (int i = 0; i < Bars; i++)
        {
            decimal close = 50_000m + (i % 500 * 0.5m);
            _bars[i] = new Bar(
                candleSeries,
                new Price(close, 2),
                new Price(close + 5m, 2),
                new Price(close - 5m, 2),
                new Price(close + 1m, 2),
                new Quantity(10m, 3),
                T0.AddNanos(i * UnixNanos.NanosPerMinute),
                T0.AddNanos(i * UnixNanos.NanosPerMinute));
        }
    }

    [Benchmark(Description = "Ten thousand bars through a 20-period SMA")]
    public decimal Sma()
    {
        SimpleMovingAverage sma = new(20);
        foreach (Bar bar in _bars)
        {
            sma.Update(bar);
        }

        return sma.Value;
    }

    [Benchmark(Description = "Ten thousand bars through Ichimoku")]
    public decimal Ichimoku()
    {
        Ichimoku ichimoku = new();
        foreach (Bar bar in _bars)
        {
            ichimoku.Update(bar);
        }

        return ichimoku.CloudTop;
    }

    [Benchmark(Description = "Ten thousand bars through a 20-period linear regression")]
    public decimal LinearRegression()
    {
        LinearRegression regression = new(20);
        foreach (Bar bar in _bars)
        {
            regression.Update(bar);
        }

        return regression.Slope;
    }
}

/// <summary>The money arithmetic that happens on every fill and every mark.</summary>
[MemoryDiagnoser]
public class PriceArithmeticBenchmarks : BenchmarkBase
{
    private CurrencyPair _instrument = null!;
    private Price _price;
    private Quantity _quantity;

    [GlobalSetup]
    public void Setup()
    {
        _instrument = Spot();
        _price = new Price(50_123.45m, 2);
        _quantity = new Quantity(1.234m, 3);
    }

    [Benchmark(Description = "Round a price to the instrument's increment")]
    public Price MakePrice() => _instrument.MakePrice(50_123.456789m);

    [Benchmark(Description = "Notional value of a position")]
    public Money NotionalValue() => _instrument.NotionalValue(_quantity, _price);
}
