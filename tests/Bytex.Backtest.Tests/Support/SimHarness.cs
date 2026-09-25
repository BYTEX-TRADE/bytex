using Bytex.Core.Engines;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests.Support;

public sealed record SimOptions
{
    public AccountType AccountType { get; init; } = AccountType.Cash;

    public OmsType OmsType { get; init; } = OmsType.Netting;

    public IReadOnlyList<Money>? StartingBalances { get; init; }

    public FillModel? FillModel { get; init; }

    public FeeModel? FeeModel { get; init; }

    public LatencyModel? LatencyModel { get; init; }

    public BarExecutionMode BarExecution { get; init; } = BarExecutionMode.OhlcPath;

    public FillSizing FillSizing { get; init; } = FillSizing.AvailableSize;

    public decimal? BarVolumeShare { get; init; } = SimulatedVenueConfig.DefaultBarVolumeShare;

    public bool Liquidate { get; init; } = true;

    public bool RejectStopOrdersAtMarket { get; init; } = true;

    public bool SupportContingentOrders { get; init; } = true;

    /// <summary>
    /// The leverage the venue grants. Null takes the harness's own: 1 on a cash venue, <see cref="SimHarness.PerpLeverage"/>
    /// on a perp, because every perp test here was written when the venue charged a twentieth of the notional at what
    /// it called 1x. A test that means no leverage says 1 and gets it.
    /// </summary>
    public decimal? DefaultLeverage { get; init; }

    public IReadOnlyDictionary<InstrumentId, decimal>? Leverages { get; init; }

    /// <summary>Bypass the pre-trade risk engine so that venue-side rejections can be observed.</summary>
    public bool BypassRisk { get; init; }

    /// <summary>What the account may lose, carry and have going at once. Nothing is enforced unless this is set.</summary>
    public RiskLimits? Limits { get; init; }

    /// <summary>How often the open-loss watch may run. Zero watches on every price, which is what a test wants.</summary>
    public TimeSpan LossWatchInterval { get; init; } = TimeSpan.Zero;

    public bool FlattenOnStop { get; init; }

    /// <summary>Venue behaviours the run adds (R8.19), which the simulator knows nothing about beyond their names.</summary>
    public IReadOnlyList<ISimulationModule> Modules { get; init; } = [];

    public string RunId { get; init; } = "test-run";
}

/// <summary>
/// One simulated venue, one instrument, one scripted strategy and a recorder of every published event.
/// </summary>
public sealed class SimHarness : IDisposable
{
    public static readonly StrategyId StrategyId = new("Scripted-001");

    private readonly List<Event> _events = new();

    private SimHarness(Instrument instrument, SimOptions options)
    {
        Instrument = instrument;
        Engine = new BacktestEngine(new BacktestEngineConfig
        {
            RunId = options.RunId,
            Kernel = new KernelConfig
            {
                Environment = TradingEnvironment.Backtest,
                LoadState = false,
                SaveState = false,
                RiskEngine = new RiskEngineConfig
                {
                    Bypass = options.BypassRisk,
                    Limits = options.Limits ?? new RiskLimits(),
                    LossWatchInterval = options.LossWatchInterval,
                },
            },
        });
        Engine.AddInstrument(instrument);
        Exchange = Engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = instrument.Venue,
            AccountType = options.AccountType,
            OmsType = options.OmsType,
            StartingBalances = options.StartingBalances ?? [new Money(1_000_000m, Currencies.USDT), new Money(100m, Currencies.BTC)],
            FillModel = options.FillModel,
            FeeModel = options.FeeModel,
            LatencyModel = options.LatencyModel,
            BarExecution = options.BarExecution,
            FillSizing = options.FillSizing,
            BarVolumeShare = options.BarVolumeShare,
            Liquidate = options.Liquidate,
            RejectStopOrdersAtMarket = options.RejectStopOrdersAtMarket,
            SupportContingentOrders = options.SupportContingentOrders,
            DefaultLeverage = options.DefaultLeverage ?? 1m,
            Leverages = options.Leverages ?? new Dictionary<InstrumentId, decimal>(),
            Modules = options.Modules,
        });
        Strategy = new ScriptedStrategy(new ScriptedStrategyConfig
        {
            StrategyId = StrategyId,
            QuoteSubscriptions = [instrument.Id],
            TradeSubscriptions = [instrument.Id],
            BarSubscriptions = [Scripted.MinuteBars(instrument)],
            BookSubscriptions = [instrument.Id],
            FlattenOnStop = options.FlattenOnStop,
        });
        Engine.AddStrategy(Strategy);
        Engine.Kernel.MessageBus.Subscribe("events.*", message =>
        {
            if (message is Event e)
            {
                _events.Add(e);
            }
        });
    }

    public Instrument Instrument { get; }

    public BacktestEngine Engine { get; }

    public SimulatedExchange Exchange { get; }

    public ScriptedStrategy Strategy { get; }

    /// <summary>Every order, position and account event published on the bus, in publication order.</summary>
    public IReadOnlyList<Event> Events => _events;

    public InstrumentId Id => Instrument.Id;

    public static SimHarness Spot(SimOptions? options = null) => new(TestInstruments.Spot(), options ?? new SimOptions());

    /// <summary>
    /// A margin account on the linear perp. Leverage is <see cref="PerpLeverage"/> unless a test says otherwise: the
    /// instrument asks 5% of notional at least, so 20x is the most it grants and the least margin a position can
    /// cost here. A test that wants to measure margin itself sets its own.
    /// </summary>
    public static SimHarness Perp(SimOptions? options = null) =>
        new(TestInstruments.Perp(), (options ?? new SimOptions()) with
        {
            AccountType = AccountType.Margin,
            StartingBalances = options?.StartingBalances ?? [new Money(100_000m, Currencies.USDT)],
            DefaultLeverage = options?.DefaultLeverage ?? PerpLeverage,
        });

    /// <summary>The leverage a perp harness runs at: the most the test instrument's 5% initial margin floor allows.</summary>
    public const decimal PerpLeverage = 20m;

    public static SimHarness For(Instrument instrument, SimOptions options) => new(instrument, options);

    public Price Px(decimal value) => Instrument.MakePrice(value);

    public Quantity Qty(decimal value) => Instrument.MakeQuantity(value);

    public SimHarness Quote(long atMs, decimal bid, decimal ask, decimal size = Scripted.DefaultQuoteSize)
    {
        Engine.AddData([Scripted.Quote(Instrument, atMs, bid, ask, size)]);
        return this;
    }

    public SimHarness Trade(long atMs, decimal price, decimal size = Scripted.DefaultTradeSize, AggressorSide aggressor = AggressorSide.Buyer)
    {
        Engine.AddData([Scripted.Trade(Instrument, atMs, price, aggressor, size)]);
        return this;
    }

    public SimHarness Bar(long atMs, decimal open, decimal high, decimal low, decimal close, decimal volume = Scripted.DefaultBarVolume)
    {
        Engine.AddData([Scripted.Bar(Instrument, atMs, open, high, low, close, volume)]);
        return this;
    }

    /// <summary>A book with depth: several levels a side, one tick apart, each holding the same size.</summary>
    public SimHarness BookDepth(long atMs, decimal bid, decimal ask, decimal size, int levels)
    {
        Engine.AddData([Scripted.BookDepth(Instrument, atMs, bid, ask, size, levels)]);
        return this;
    }

    /// <summary>The whole book, replacing whatever was quoted before it - the way a venue's snapshot arrives.</summary>
    public SimHarness BookSnapshot(long atMs, decimal bid, decimal ask, decimal size, int levels)
    {
        Engine.AddData([Scripted.BookDepth(Instrument, atMs, bid, ask, size, levels, replacing: true)]);
        return this;
    }

    /// <summary>Adds one resting order to each side of the book (a better price becomes the new best).</summary>
    public SimHarness Book(long atMs, decimal bid, decimal ask, decimal size = Scripted.DefaultLevelSize)
    {
        Engine.AddData([Scripted.BookLevels(Instrument, atMs, bid, ask, size)]);
        return this;
    }

    /// <summary>A funding rate published at that moment, as a venue publishes one.</summary>
    public SimHarness Funding(long atMs, decimal rate)
    {
        Engine.AddData([Scripted.Funding(Instrument, atMs, rate)]);
        return this;
    }

    public SimHarness At(long atMs, Action<ScriptedStrategy> action)
    {
        Strategy.At(Scripted.Ms(atMs), action);
        return this;
    }

    public SimHarness Run()
    {
        Engine.Run();
        return this;
    }

    public void ClearRecordedEvents() => _events.Clear();

    public IReadOnlyList<OrderFilled> Fills(Order order) => order.Events.OfType<OrderFilled>().ToList();

    public OrderFilled SingleFill(Order order) => Assert.Single(order.Events.OfType<OrderFilled>());

    public static string EventNames(Order order) => string.Join(",", order.Events.Select(e => e.GetType().Name.Replace("Order", string.Empty, StringComparison.Ordinal)));

    public decimal Balance(Currency currency) => Exchange.Balances.TryGetValue(currency, out decimal amount) ? amount : 0m;

    public Account Account => Engine.Cache.AccountForVenue(Instrument.Venue) ?? throw new InvalidOperationException("No account in cache.");

    public IReadOnlyList<Position> Positions => Engine.Cache.Positions();

    public void Dispose() => Engine.Dispose();
}
