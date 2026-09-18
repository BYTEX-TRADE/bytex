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

    public bool RejectStopOrdersAtMarket { get; init; } = true;

    public bool SupportContingentOrders { get; init; } = true;

    public decimal DefaultLeverage { get; init; } = 1m;

    public IReadOnlyDictionary<InstrumentId, decimal>? Leverages { get; init; }

    /// <summary>Bypass the pre-trade risk engine so that venue-side rejections can be observed.</summary>
    public bool BypassRisk { get; init; }

    public bool FlattenOnStop { get; init; }

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
                RiskEngine = new RiskEngineConfig { Bypass = options.BypassRisk },
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
            RejectStopOrdersAtMarket = options.RejectStopOrdersAtMarket,
            SupportContingentOrders = options.SupportContingentOrders,
            DefaultLeverage = options.DefaultLeverage,
            Leverages = options.Leverages ?? new Dictionary<InstrumentId, decimal>(),
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

    public static SimHarness Perp(SimOptions? options = null) =>
        new(TestInstruments.Perp(), (options ?? new SimOptions()) with { AccountType = AccountType.Margin, StartingBalances = options?.StartingBalances ?? [new Money(100_000m, Currencies.USDT)] });

    public static SimHarness For(Instrument instrument, SimOptions options) => new(instrument, options);

    public Price Px(decimal value) => Instrument.MakePrice(value);

    public Quantity Qty(decimal value) => Instrument.MakeQuantity(value);

    public SimHarness Quote(long atMs, decimal bid, decimal ask)
    {
        Engine.AddData([Scripted.Quote(Instrument, atMs, bid, ask)]);
        return this;
    }

    public SimHarness Trade(long atMs, decimal price)
    {
        Engine.AddData([Scripted.Trade(Instrument, atMs, price)]);
        return this;
    }

    public SimHarness Bar(long atMs, decimal open, decimal high, decimal low, decimal close)
    {
        Engine.AddData([Scripted.Bar(Instrument, atMs, open, high, low, close)]);
        return this;
    }

    /// <summary>Adds one resting order to each side of the book (a better price becomes the new best).</summary>
    public SimHarness Book(long atMs, decimal bid, decimal ask)
    {
        Engine.AddData([Scripted.BookLevels(Instrument, atMs, bid, ask)]);
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
