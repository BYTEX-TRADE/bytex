using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Bytex.Backtest;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Tests;

internal static class Fixtures
{
    public static readonly Venue Sim = new("SIM");

    public static CurrencyPair BtcUsdt() => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT"), Sim),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 6,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.000001m, 6),
        MinQuantity = new Quantity(0.00001m, 6),
        MakerFee = 0.001m,
        TakerFee = 0.001m,
    });

    public static BarType MinuteBars(Instrument instrument) => new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

    /// <summary>Deterministic random-walk minute bars, the same generator the engine examples use.</summary>
    public static IReadOnlyList<Bar> RandomWalkBars(Instrument instrument, BarType barType, int count, decimal startPrice = 50_000m, int seed = 7)
    {
        Random random = new(seed);
        List<Bar> bars = new(count);
        decimal price = startPrice;
        UnixNanos ts = UnixNanos.FromDateTimeOffset(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        long interval = barType.Spec.IntervalNanos;
        for (int i = 0; i < count; i++)
        {
            decimal drift = (decimal)(random.NextDouble() - 0.48) * price * 0.002m;
            decimal open = price;
            decimal close = Math.Max(1m, open + drift);
            decimal high = Math.Max(open, close) + (decimal)random.NextDouble() * price * 0.001m;
            decimal low = Math.Min(open, close) - (decimal)random.NextDouble() * price * 0.001m;
            decimal volume = 1m + (decimal)random.NextDouble() * 10m;
            ts = ts.AddNanos(interval);
            bars.Add(new Bar(barType, instrument.MakePrice(open), instrument.MakePrice(high), instrument.MakePrice(low), instrument.MakePrice(close), instrument.MakeQuantity(volume), ts, ts));
            price = close;
        }

        return bars;
    }

    public static StrategyDocument Example(string name)
    {
        Assembly assembly = typeof(DocumentStrategy).Assembly;
        using Stream stream = assembly.GetManifestResourceStream($"Bytex.Documents.Examples.{name}.json") ?? throw new InvalidOperationException($"Example {name} not embedded.");
        using StreamReader reader = new(stream);
        return DocumentJson.Deserialize(reader.ReadToEnd());
    }

    public static (BacktestResult Result, DocumentStrategy Strategy) RunBacktest(StrategyDocument document, int bars = 3000, IReadOnlyDictionary<string, decimal>? overrides = null)
    {
        Instrument instrument = BtcUsdt();
        BarType barType = MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "test" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(RandomWalkBars(instrument, barType, bars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001"), ParameterOverrides = overrides });
        engine.AddStrategy(strategy);
        engine.Run();
        return (engine.GetResult(), strategy);
    }

    /// <summary>
    /// The same run with a quote and a trade at each bar's close, for the nodes that read top of book or the last
    /// print. Kept apart from <see cref="RunBacktest"/> because quotes drive the matching engine, and a test that
    /// asserts a fill price wants to know exactly what the venue saw.
    /// </summary>
    public static (BacktestResult Result, DocumentStrategy Strategy) RunWithTicks(StrategyDocument document, int bars = 3000)
    {
        Instrument instrument = BtcUsdt();
        BarType barType = MinuteBars(instrument);
        IReadOnlyList<Bar> series = RandomWalkBars(instrument, barType, bars);
        List<IData> data = new();
        foreach (Bar bar in series)
        {
            data.Add(bar);
            data.Add(new QuoteTick(instrument.Id, instrument.MakePrice(bar.Close.Value - instrument.PriceIncrement.Value), bar.Close,
                instrument.MakeQuantity(1m), instrument.MakeQuantity(1m), bar.TsEvent, bar.TsInit));
            data.Add(new TradeTick(instrument.Id, bar.Close, instrument.MakeQuantity(1m), AggressorSide.Buyer, new TradeId($"T-{bar.TsEvent.Value}"), bar.TsEvent, bar.TsInit));
        }

        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "test-ticks" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(data);
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();
        return (engine.GetResult(), strategy);
    }

    /// <summary>A stable fingerprint of what a run did: every order, fill, and position row.</summary>
    /// <summary>
    /// Runs a document on an engine whose cache already holds the state a previous run saved, the way a live node
    /// starts again over its own store: the kernel loads actor state before the strategies start.
    /// </summary>
    public static DocumentStrategy RunWithSavedState(StrategyDocument document, IDictionary<string, byte[]> state, int bars = 50)
    {
        Instrument instrument = BtcUsdt();
        BarType barType = MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig
        {
            RunId = "restart",
            Kernel = new KernelConfig { Environment = TradingEnvironment.Backtest, LoadState = true, SaveState = false },
        });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(RandomWalkBars(instrument, barType, bars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Kernel.Cache.SaveActorState(strategy.ActorId, state);
        engine.Run();
        return strategy;
    }

    public static string Fingerprint(BacktestResult result)
    {
        StringBuilder sb = new();
        sb.Append(BytexJson.Serialize(result.Orders));
        sb.Append(BytexJson.Serialize(result.Fills));
        sb.Append(BytexJson.Serialize(result.Positions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
