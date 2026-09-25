using System.Globalization;
using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;

namespace Bytex.Backtest;

public sealed record SimulatedVenueConfig
{
    public required Venue Venue { get; init; }

    public OmsType OmsType { get; init; } = OmsType.Netting;

    public AccountType AccountType { get; init; } = AccountType.Cash;

    /// <summary>Base currency for single-currency accounts; null for multi-currency.</summary>
    public Currency? BaseCurrency { get; init; }

    public IReadOnlyList<Money> StartingBalances { get; init; } = [];

    /// <summary>
    /// Whether this venue refuses to change an order once it is placed, as some real ones do - KuCoin's perpetual
    /// futures cannot amend at all, neither plain orders nor stops. Modelling it here is what lets a strategy be
    /// backtested against the venue it will actually run on: a document whose protective orders are resized behaves
    /// differently on a venue that will not resize them, and that difference is worth finding in a backtest rather
    /// than at the first scale-in with real money.
    /// </summary>
    public bool RefusesOrderAmends { get; init; }

    /// <summary>
    /// Venue behaviours this run adds, that the simulator does not know about (R8.19) - interest on a position held
    /// overnight, and whatever else one venue does and its competitors do not. Each sees the clock advance and may
    /// move money against the account, and nothing else.
    /// </summary>
    public IReadOnlyList<ISimulationModule> Modules { get; init; } = [];

    /// <summary>
    /// Leverage for instruments without their own setting. At least 1, as an account's own setter demands: a
    /// leverage below that is not a smaller position, it is a margin requirement larger than the notional, and zero
    /// is a division nobody meant to write.
    /// </summary>
    public decimal DefaultLeverage { get; init; } = 1m;

    public IReadOnlyDictionary<InstrumentId, decimal> Leverages { get; init; } = new Dictionary<InstrumentId, decimal>();

    public FillModel? FillModel { get; init; }

    public FeeModel? FeeModel { get; init; }

    public LatencyModel? LatencyModel { get; init; }

    public BarExecutionMode BarExecution { get; init; } = BarExecutionMode.OhlcPath;

    public BookType BookType { get; init; } = BookType.L1;

    /// <summary>What bounds a fill: the size on offer at the touch, or nothing at all.</summary>
    public FillSizing FillSizing { get; init; } = FillSizing.AvailableSize;

    /// <summary>
    /// The share of a bar's volume one participant may take, for runs driven by bars. A bar says what traded across
    /// its whole length rather than what was on offer at a price, so this is not a claim about a book: it is a claim
    /// about how much of a period's trade one participant could plausibly have been, which is the assumption a
    /// bar-driven backtest is making anyway - silently, and at a hundred percent - when it fills any size at all.
    /// <para>
    /// <see cref="DefaultBarVolumeShare"/> by default. Null lets a bar fill any size, which is what the simulator did
    /// before 0.5 and what a study that means to ignore liquidity wants. Ignored where the data says what was really
    /// on offer: a quote, a book or a print bounds the fill itself.
    /// </para>
    /// </summary>
    public decimal? BarVolumeShare { get; init; } = DefaultBarVolumeShare;

    /// <summary>
    /// A tenth of a bar's volume: what one participant may take unless a run says otherwise. Enough that a liquid
    /// instrument is untouched by it and a thin one is bounded most of the time, which is the whole point of having
    /// it - a scan that ranks instruments against each other is otherwise biased towards the ones that could not have
    /// absorbed the trade.
    /// </summary>
    public const decimal DefaultBarVolumeShare = 0.10m;

    /// <summary>
    /// Close a margin account's positions when its equity falls below the maintenance margin it owes, as a venue
    /// does. On by default: a margin backtest that rode a position past the point a venue would have taken it away is
    /// reporting a trade that could not have happened. A cash account cannot be liquidated and ignores this.
    /// </summary>
    public bool Liquidate { get; init; } = true;

    /// <summary>Reject stop orders whose trigger is already through the market on arrival.</summary>
    public bool RejectStopOrdersAtMarket { get; init; } = true;

    /// <summary>Venue supports contingent order lists natively (OCO/OUO cancel or update linked orders).</summary>
    public bool SupportContingentOrders { get; init; } = true;
}

/// <summary>
/// One position a venue closed itself because the account could no longer carry it: what it held, what it was worth
/// when the venue stepped in, what the account had against the maintenance margin it owed, and the price the closing
/// order got.
/// </summary>
public sealed record Liquidation(InstrumentId InstrumentId, decimal SignedQuantity, Price Price, Money Equity, Money MaintenanceMargin, UnixNanos TsEvent);

/// <summary>
/// One funding payment a perpetual position made or took: the rate that was published, the position it was applied
/// to, the price it was valued at, and what it came to. Negative is what the account paid.
/// </summary>
public sealed record FundingPayment(InstrumentId InstrumentId, decimal Rate, decimal SignedQuantity, Price Price, Money Amount, UnixNanos TsEvent);

/// <summary>
/// A simulated venue: matching engines per instrument, an account, and latency-delayed command processing.
/// </summary>
public sealed class SimulatedExchange : IMatchingEngineHost
{
    private readonly SimulatedVenueConfig _config;
    private readonly KernelServices _services;
    private readonly FillModel _fillModel;
    private readonly FeeModel _feeModel;
    private readonly LatencyModel _latency;
    private readonly Dictionary<InstrumentId, Instrument> _instruments = new();
    private readonly Dictionary<InstrumentId, OrderMatchingEngine> _engines = new();
    private readonly Dictionary<Currency, decimal> _balances = new();
    private readonly Dictionary<ClientOrderId, Dictionary<Currency, decimal>> _reserved = new();
    private readonly Dictionary<Currency, decimal> _locked = new();
    private readonly Dictionary<InstrumentId, VenuePosition> _positions = new();
    private readonly List<FundingPayment> _funding = new();
    private readonly List<Liquidation> _liquidations = new();

    /// <summary>What a closing order the venue sent itself is tagged with, so a report can tell it from the account's own.</summary>
    public const string LiquidationTag = "LIQUIDATION";

    /// <summary>
    /// How far apart the client order ids of the venue's own closing orders are kept. Far enough that a strategy's
    /// own next order cannot land on one of them, the way a host's flatten does it.
    /// </summary>
    private const int LiquidationIdBlock = 100_000;

    /// <summary>
    /// Who last sent an order for an instrument. A venue does not know about strategies, but the engine does: a
    /// position it closes itself has to be closed under the strategy whose position it is, or the fill would arrive
    /// for nobody.
    /// </summary>
    private readonly Dictionary<InstrumentId, (TraderId TraderId, StrategyId StrategyId)> _owners = new();
    private readonly Dictionary<ClientOrderId, Order> _orders = new();
    private readonly Dictionary<ClientOrderId, IReadOnlyList<ClientOrderId>> _restoredLinks = new();
    private readonly PriorityQueue<Action, (long Due, long Seq)> _inflight = new();
    private readonly ILogger _log;
    private BacktestExecutionClient? _client;
    private long _venueOrderCount;
    private long _tradeCount;
    private long _inflightSeq;
    private long _moduleClock;
    private ModuleState? _moduleState;
    private readonly List<ModuleCharge> _moduleCharges = new();

    public SimulatedExchange(SimulatedVenueConfig config, KernelServices services)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(services);

        // A leverage below 1 is a margin requirement larger than the notional, and zero divides the margin
        // arithmetic by nothing. An account's own setter refuses both; the venue that feeds it has to refuse them
        // too, at the point where someone wrote the number, rather than when the first position is opened.
        ArgumentOutOfRangeException.ThrowIfLessThan(config.DefaultLeverage, 1m, nameof(config.DefaultLeverage));
        foreach ((InstrumentId instrumentId, decimal leverage) in config.Leverages)
        {
            if (leverage < 1m)
            {
                throw new ArgumentOutOfRangeException(nameof(config), leverage, $"Leverage for {instrumentId} must be at least 1.");
            }
        }

        _config = config;
        _services = services;
        _fillModel = config.FillModel ?? new FillModel();
        _feeModel = config.FeeModel ?? new MakerTakerFeeModel();
        _latency = config.LatencyModel ?? LatencyModel.Zero;
        _log = services.Logging.CreateLogger<SimulatedExchange>();
        foreach (Money balance in config.StartingBalances)
        {
            _balances[balance.Currency] = balance.Amount;
        }
    }

    public Venue Venue => _config.Venue;

    public SimulatedVenueConfig Config => _config;

    public AccountId AccountId => new($"{_config.Venue}{AccountIdSuffix}");

    public IReadOnlyDictionary<InstrumentId, Instrument> Instruments => _instruments;

    public IReadOnlyDictionary<Currency, decimal> Balances => _balances;

    /// <summary>What a simulated venue calls its one account: the venue's name and the first account on it.</summary>
    private const string AccountIdSuffix = "-001";

    public int OpenOrderCount => _engines.Values.Sum(e => e.OpenOrderCount);

    internal void Register(BacktestExecutionClient client) => _client = client;

    private BacktestExecutionClient Client => _client ?? throw new InvalidOperationException($"Simulated exchange {Venue} has no execution client registered.");

    private UnixNanos Now => _services.Clock.Timestamp;

    public void AddInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (instrument.Venue != Venue)
        {
            throw new ArgumentException($"Instrument {instrument.Id} does not belong to venue {Venue}.", nameof(instrument));
        }

        if (_config.AccountType == AccountType.Cash && instrument.InstrumentClass != InstrumentClass.Spot)
        {
            // A cash account settles a fill by exchanging the two currencies, which books a derivative as if it were spot:
            // no margin, no leverage, and a short that sells a base currency the account never held.
            throw new ArgumentException($"Instrument {instrument.Id} is a {instrument.InstrumentClass}; venue {Venue} is configured with a cash account, which can only hold spot instruments. Set the venue's accountType to margin.", nameof(instrument));
        }

        _instruments[instrument.Id] = instrument;
        _engines[instrument.Id] = new OrderMatchingEngine(instrument, this, _fillModel, _config.BarExecution, _config.RejectStopOrdersAtMarket, _config.OmsType, _config.FillSizing, _config.BarVolumeShare);
    }

    /// <summary>
    /// Emits the initial account state so the portfolio knows the starting balances.
    /// </summary>
    public void InitializeAccount()
    {
        if (_config.AccountType == AccountType.Margin)
        {
            Client.RaiseAccountState(BuildBalances(), BuildMargins(), reported: true, Now);
        }
        else
        {
            Client.RaiseAccountState(BuildBalances(), [], reported: true, Now);
        }
    }

    public void Reset()
    {
        _fillModel.Reset();
        _engines.Clear();
        foreach (Instrument instrument in _instruments.Values)
        {
            _engines[instrument.Id] = new OrderMatchingEngine(instrument, this, _fillModel, _config.BarExecution, _config.RejectStopOrdersAtMarket, _config.OmsType, _config.FillSizing, _config.BarVolumeShare);
        }

        _balances.Clear();
        _funding.Clear();
        _liquidations.Clear();
        _owners.Clear();
        _reserved.Clear();
        _locked.Clear();
        _announcedChildren.Clear();
        foreach (Money balance in _config.StartingBalances)
        {
            _balances[balance.Currency] = balance.Amount;
        }

        _positions.Clear();
        _orders.Clear();
        _restoredLinks.Clear();
        _inflight.Clear();
        _venueOrderCount = 0;
        _tradeCount = 0;
    }

    // ----- State carried over a restart -----

    /// <summary>
    /// Gives the venue a position it held before a restart, so reduce-only checks, margin and the P&amp;L of the closing
    /// fill start from it. Balances are not touched: a margin position moves no money until it is closed, and what was
    /// realized before the restart belongs in <see cref="SimulatedVenueConfig.StartingBalances"/>.
    /// </summary>
    public void RestorePosition(InstrumentId instrumentId, decimal signedQuantity, decimal avgPx)
    {
        if (!_instruments.ContainsKey(instrumentId))
        {
            throw new InvalidOperationException($"Cannot restore a position on {instrumentId}: the instrument is not registered with simulated venue {Venue}.");
        }

        if (_config.AccountType == AccountType.Cash)
        {
            throw new InvalidOperationException($"Venue {Venue} has a cash account, which holds a position as a balance of the base currency. Restore it through the starting balances.");
        }

        if (signedQuantity == 0m || avgPx <= 0m)
        {
            throw new ArgumentException($"A restored position needs a quantity other than zero and an average price above zero (got {signedQuantity} at {avgPx}).");
        }

        if (_positions.TryGetValue(instrumentId, out VenuePosition? existing) && existing.SignedQuantity != 0m)
        {
            throw new InvalidOperationException($"Venue {Venue} already holds a position on {instrumentId}.");
        }

        _positions[instrumentId] = new VenuePosition(signedQuantity, avgPx);
    }

    /// <summary>
    /// Puts back an order that was resting at the venue before a restart. The order must be the object the owner's cache
    /// holds, because the matching engine reads its state from it. <paramref name="linkedOrderIds"/> are the orders to
    /// cancel when this one fills (the other leg of a stop and target pair); an order rebuilt from a status report no longer
    /// carries them itself.
    /// </summary>
    public void RestoreOrder(Order order, VenueOrderId venueOrderId, IReadOnlyList<ClientOrderId>? linkedOrderIds = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
        {
            throw new InvalidOperationException($"Cannot restore order {order.ClientOrderId}: instrument {order.InstrumentId} is not registered with simulated venue {Venue}.");
        }

        engine.RestoreOrder(order, venueOrderId);
        _orders[order.ClientOrderId] = order;
        Reserve(order);
        if (linkedOrderIds is { Count: > 0 })
        {
            _restoredLinks[order.ClientOrderId] = linkedOrderIds;
        }
    }

    // ----- Market data -----

    public void ProcessQuoteTick(QuoteTick quote)
    {
        ProcessDueCommands(quote.TsEvent);
        if (_engines.TryGetValue(quote.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessQuote(quote);
        }

        CheckMargin(quote.TsEvent);
    }

    public void ProcessTradeTick(TradeTick trade)
    {
        ProcessDueCommands(trade.TsEvent);
        if (_engines.TryGetValue(trade.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessTrade(trade);
        }

        CheckMargin(trade.TsEvent);
    }

    public void ProcessBar(Bar bar)
    {
        ProcessDueCommands(bar.TsEvent);
        if (_engines.TryGetValue(bar.BarType.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessBar(bar);
        }

        CheckMargin(bar.TsEvent);
    }

    /// <summary>
    /// Applies a published funding rate to an open perpetual position: the side that is long pays a positive rate and
    /// takes a negative one, on the notional the position is worth at the venue's last price, in the currency the
    /// instrument settles in. A venue publishes a rate whether anybody holds the contract or not, so nothing happens
    /// when the account is flat.
    /// <para>
    /// Only a perpetual swap has funding, and only a margin account can hold one. The price is the last the venue saw
    /// rather than a separate mark price, because a mark of its own is something this simulator does not keep.
    /// </para>
    /// </summary>
    public void ProcessFundingRate(FundingRateUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ProcessDueCommands(update.TsEvent);
        if (!_instruments.TryGetValue(update.InstrumentId, out Instrument? instrument)
            || instrument.InstrumentClass != InstrumentClass.Swap
            || _config.AccountType == AccountType.Cash
            || update.Rate == 0m)
        {
            return;
        }

        if (!_positions.TryGetValue(update.InstrumentId, out VenuePosition? position) || position.SignedQuantity == 0m)
        {
            return;
        }

        if (!_engines.TryGetValue(update.InstrumentId, out OrderMatchingEngine? engine)
            || (engine.LastPrice ?? engine.BestBid ?? engine.BestAsk) is not { } price
            || price <= 0m)
        {
            _log.LogWarning("Funding rate {Rate} for {InstrumentId} arrived before any price, so there is nothing to value the position at", update.Rate, update.InstrumentId);
            return;
        }

        decimal size = Math.Abs(position.SignedQuantity);
        decimal notional = instrument.IsInverse
            ? size * instrument.Multiplier.Value / price
            : size * price * instrument.Multiplier.Value;
        decimal amount = notional * update.Rate * (position.SignedQuantity > 0m ? -1m : 1m);
        Money payment = new(amount, instrument.SettlementCurrency);

        Adjust(payment.Currency, payment.Amount);
        _funding.Add(new FundingPayment(update.InstrumentId, update.Rate, position.SignedQuantity, instrument.MakePrice(price), payment, update.TsEvent));
        _log.LogInformation("Funding {Rate} on {Quantity} {InstrumentId} at {Price}: {Payment}", update.Rate, position.SignedQuantity, update.InstrumentId, price, payment);
        Client.RaiseAccountState(BuildBalances(), _config.AccountType == AccountType.Margin ? BuildMargins() : [], reported: true, update.TsEvent);
    }

    /// <summary>Every funding payment this venue has applied, oldest first.</summary>
    public IReadOnlyList<FundingPayment> FundingPayments => _funding;

    /// <summary>Every charge an added behaviour made, oldest first (R8.19).</summary>
    public IReadOnlyList<ModuleCharge> ModuleCharges => _moduleCharges;

    /// <summary>Every position this venue closed itself for want of margin, oldest first.</summary>
    public IReadOnlyList<Liquidation> Liquidations => _liquidations;

    /// <summary>How many fills this venue bounded by the size on offer, and how much went in under a bound.</summary>
    public (int Fills, decimal Quantity) Bounded =>
        (_engines.Values.Sum(e => e.BoundedFills), _engines.Values.Sum(e => e.BoundedQuantity));

    /// <summary>
    /// What this venue actually did in this run, as against what it was configured to be able to do. A backtest fed
    /// data that says nothing about size can be configured for partial fills and never bound one; a run with no
    /// funding rates in it charges no funding whatever the venue could have done. A product that tells its users what
    /// it can do has to read this one, not the configuration.
    /// </summary>
    public IReadOnlyList<string> Applied
    {
        get
        {
            List<string> applied = new();
            if (Bounded.Fills > 0)
            {
                applied.Add(SimulationCapabilities.PartialFills);
            }

            if (_funding.Count > 0)
            {
                applied.Add(SimulationCapabilities.Funding);
            }

            if (_liquidations.Count > 0)
            {
                applied.Add(SimulationCapabilities.Liquidation);
            }

            if (_engines.Values.Any(e => e.WalkedTheBook))
            {
                applied.Add(SimulationCapabilities.BookDepth);
            }

            // An added behaviour appears only once it has actually moved money, the same rule the built-ins follow.
            // A run configured with a rollover charge whose data never crossed one did not charge it, and saying it
            // did would be exactly the claim this property exists to stop.
            foreach (string module in _moduleCharges.Select(c => c.Module).Distinct(StringComparer.Ordinal))
            {
                applied.Add(module);
            }

            return applied;
        }
    }

    public void ProcessOrderBook(OrderBook book, UnixNanos ts)
    {
        ProcessDueCommands(ts);
        if (_engines.TryGetValue(book.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessBook(book, ts);
        }

        CheckMargin(ts);
    }

    /// <summary>
    /// What a margin account is worth against what it owes, per currency: the balance plus what its open positions
    /// are up or down at the price the venue last saw, against the maintenance margin those positions require at that
    /// same price. Below it, the venue closes them - R8.16.
    /// <para>
    /// One check per currency and per price the venue sees, because that is when an account gets into trouble. A cash
    /// account cannot be liquidated: it owns what it bought.
    /// </para>
    /// </summary>
    private void CheckMargin(UnixNanos ts)
    {
        if (_config.AccountType != AccountType.Margin || !_config.Liquidate || _positions.Count == 0)
        {
            return;
        }

        Dictionary<Currency, decimal> unrealized = new();
        Dictionary<Currency, decimal> maintenance = new();
        foreach ((InstrumentId instrumentId, VenuePosition position) in _positions)
        {
            if (position.SignedQuantity == 0m
                || !_instruments.TryGetValue(instrumentId, out Instrument? instrument)
                || MarkPrice(instrumentId) is not { } price)
            {
                continue;
            }

            Currency currency = instrument.SettlementCurrency;
            unrealized[currency] = unrealized.GetValueOrDefault(currency) + Unrealized(instrument, position, price);
            Quantity size = new(Math.Abs(position.SignedQuantity), instrument.SizePrecision);
            maintenance[currency] = maintenance.GetValueOrDefault(currency)
                + (instrument.NotionalValue(size, instrument.MakePrice(price)).Amount * instrument.MaintenanceMarginRate);
        }

        foreach ((Currency currency, decimal owed) in maintenance)
        {
            decimal equity = _balances.GetValueOrDefault(currency) + unrealized.GetValueOrDefault(currency);
            if (equity >= owed)
            {
                continue;
            }

            LiquidateIn(currency, equity, owed, ts);
        }
    }

    /// <summary>
    /// Closes every position that settles in one currency, because the account can no longer carry any of them: the
    /// working orders go first so nothing of the account's own is in the way, then each position is closed at the
    /// market under the strategy that opened it, reduce-only and tagged, so the engine's books follow it as they
    /// follow any other fill.
    /// </summary>
    private void LiquidateIn(Currency currency, decimal equity, decimal owed, UnixNanos ts)
    {
        foreach ((InstrumentId instrumentId, VenuePosition position) in _positions.ToList())
        {
            if (position.SignedQuantity == 0m
                || !_instruments.TryGetValue(instrumentId, out Instrument? instrument)
                || !instrument.SettlementCurrency.Equals(currency)
                || !_engines.TryGetValue(instrumentId, out OrderMatchingEngine? engine)
                || MarkPrice(instrumentId) is not { } price)
            {
                continue;
            }

            if (!_owners.TryGetValue(instrumentId, out (TraderId TraderId, StrategyId StrategyId) owner))
            {
                _log.LogError("{InstrumentId} is being liquidated and no order has ever been sent for it, so there is nobody to close it for", instrumentId);
                continue;
            }

            decimal signed = position.SignedQuantity;
            engine.ProcessCancelAll(null, null, ts);
            Quantity size = new(Math.Abs(signed), instrument.SizePrecision);
            _liquidations.Add(new Liquidation(
                instrumentId,
                signed,
                instrument.MakePrice(price),
                new Money(equity, currency),
                new Money(owed, currency),
                ts));
            _log.LogWarning(
                "Liquidating {Quantity} {InstrumentId} at {Price}: equity {Equity} is below the {Owed} maintenance margin it requires",
                signed, instrumentId, price, equity, owed);

            OrderFactory factory = new(owner.TraderId, owner.StrategyId, _services.Clock, _liquidations.Count * LiquidationIdBlock);
            MarketOrder closing = factory.Market(
                instrumentId,
                signed > 0m ? OrderSide.Sell : OrderSide.Buy,
                size,
                reduceOnly: true,
                tags: [LiquidationTag]);

            // The order is the venue's, but the engine has to know it to follow what happens to it: an event for an
            // order nobody has heard of is dropped, and the strategy would go on believing it still held the
            // position. Registered the way an execution algorithm registers the pieces it spawns.
            if (_services.Cache is Cache cache)
            {
                cache.AddOrder(closing, _services.Cache.PositionsOpen(instrumentId: instrumentId, strategyId: owner.StrategyId).FirstOrDefault()?.Id);
            }

            Submit(new SubmitOrder(owner.TraderId, owner.StrategyId, closing, null, null, null, Guid.NewGuid(), ts));
        }
    }

    /// <summary>The price the venue last saw for an instrument, which is what it values a position at.</summary>
    private decimal? MarkPrice(InstrumentId instrumentId) =>
        _engines.TryGetValue(instrumentId, out OrderMatchingEngine? engine)
            ? engine.LastPrice ?? engine.BestBid ?? engine.BestAsk
            : null;

    /// <summary>The leverage this venue grants, per instrument, for the account to be told.</summary>
    internal IReadOnlyDictionary<InstrumentId, decimal> Leverages => _config.Leverages;

    /// <summary>The leverage this venue grants an instrument it was given no setting for.</summary>
    internal decimal DefaultLeverage => _config.DefaultLeverage;

    private decimal Leverage(InstrumentId instrumentId) =>
        _config.Leverages.TryGetValue(instrumentId, out decimal leverage) ? leverage : _config.DefaultLeverage;

    /// <summary>What a position is up or down at a price, in the currency it settles in.</summary>
    private static decimal Unrealized(Instrument instrument, VenuePosition position, decimal price)
    {
        decimal size = Math.Abs(position.SignedQuantity);
        decimal raw = instrument.IsInverse
            ? ((1m / position.AvgPx) - (1m / price)) * size * instrument.Multiplier.Value
            : (price - position.AvgPx) * size * instrument.Multiplier.Value;
        return position.SignedQuantity > 0m ? raw : -raw;
    }

    /// <summary>When the next command in flight comes due, or null when none is waiting.</summary>
    public UnixNanos? NextDueTime => _inflight.TryPeek(out _, out (long Due, long Seq) priority) ? new UnixNanos(priority.Due) : null;

    /// <summary>
    /// Executes every queued command whose latency has elapsed at <paramref name="now"/>. The caller sets the clock to
    /// that time first, so each command is stamped with its own due time rather than the next data event's.
    /// </summary>
    public void ProcessDueCommands(UnixNanos now)
    {
        while (_inflight.TryPeek(out Action? action, out (long Due, long Seq) priority) && priority.Due <= now.Value)
        {
            _inflight.Dequeue();
            action();
        }

        RunModules(now);
    }

    /// <summary>
    /// Lets each added behaviour see the clock (R8.19). Every path into this venue passes through the due-command
    /// pump, so this is the one place that sees time move however a run is driven - by ticks, by bars, or by a book.
    /// <para>
    /// Never backwards: a run interleaving several instruments can offer a timestamp older than one already seen,
    /// and a behaviour charging for elapsed time would bill it twice.
    /// </para>
    /// </summary>
    private void RunModules(UnixNanos now)
    {
        if (_config.Modules.Count == 0 || now.Value <= _moduleClock)
        {
            return;
        }

        _moduleClock = now.Value;
        foreach (ISimulationModule module in _config.Modules)
        {
            module.OnTime(now, _moduleState ??= new ModuleState(this));
        }
    }

    private void Enqueue(TimeSpan latency, Action action)
    {
        if (_latency.IsZero || latency == TimeSpan.Zero)
        {
            action();
            return;
        }

        long due = Now.Add(latency).Value;
        _inflight.Enqueue(action, (due, _inflightSeq++));
    }

    // ----- Commands (called by the execution client) -----

    internal void Submit(SubmitOrder command)
    {
        Order order = command.Order;
        _orders[order.ClientOrderId] = order;
        _owners[order.InstrumentId] = (order.TraderId, order.StrategyId);
        Enqueue(_latency.InsertLatency, () =>
        {
            UnixNanos ts = Now;
            Client.RaiseSubmitted(order, ts);
            if (!_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
            {
                Client.RaiseRejected(order, $"instrument {order.InstrumentId} not registered with simulated venue {Venue}", ts);
                return;
            }

            if (Unaffordable(order) is { } why)
            {
                Client.RaiseRejected(order, why, ts);
                return;
            }

            engine.ProcessOrder(order, ts);
        });
    }

    internal void SubmitList(SubmitOrderList command)
    {
        foreach (Order order in command.OrderList.Orders)
        {
            _orders[order.ClientOrderId] = order;
        }

        Enqueue(_latency.InsertLatency, () =>
        {
            UnixNanos ts = Now;
            Order first = command.OrderList.First;
            foreach (Order order in command.OrderList.Orders)
            {
                Client.RaiseSubmitted(order, ts);
            }

            if (!_engines.TryGetValue(first.InstrumentId, out OrderMatchingEngine? engine))
            {
                foreach (Order order in command.OrderList.Orders)
                {
                    Client.RaiseRejected(order, $"instrument {first.InstrumentId} not registered with simulated venue {Venue}", ts);
                }

                return;
            }

            // OTO: the parent is worked now; children are accepted but held until the parent fills.
            if (first.Contingency == ContingencyType.Oto)
            {
                if (Unaffordable(first) is { } why)
                {
                    // Without the entry the children have nothing to exit, so the whole list is refused.
                    foreach (Order order in command.OrderList.Orders)
                    {
                        Client.RaiseRejected(order, why, ts);
                    }

                    return;
                }

                engine.ProcessOrder(first, ts);
                foreach (Order child in command.OrderList.Orders.Skip(1))
                {
                    if (first.IsClosed && first.Status == OrderStatus.Filled)
                    {
                        engine.ProcessOrder(child, ts);
                    }
                    else
                    {
                        _pendingChildren.Add(child);
                        bool held = Reserve(child);
                        VenueOrderId childId = NextVenueOrderId();
                        _announcedChildren[child.ClientOrderId] = childId;
                        Client.RaiseAccepted(child, childId, ts);
                        PublishHolds(held, ts);
                    }
                }

                return;
            }

            foreach (Order order in command.OrderList.Orders)
            {
                // Order by order: the first leg reserves its funds before the next one is judged, so a pair that only
                // one balance can cover does not both get worked.
                if (Unaffordable(order) is { } why)
                {
                    Client.RaiseRejected(order, why, ts);
                    continue;
                }

                engine.ProcessOrder(order, ts);
            }
        });
    }

    private readonly List<Order> _pendingChildren = new();
    private readonly Dictionary<ClientOrderId, VenueOrderId> _announcedChildren = new();

    internal void Modify(ModifyOrder command)
    {
        Enqueue(_latency.UpdateLatency, () =>
        {
            UnixNanos ts = Now;
            if (!_orders.TryGetValue(command.ClientOrderId, out Order? order) || !_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
            {
                Client.RaiseModifyRejectedById(command, "order not found at venue", ts);
                return;
            }

            if (_config.RefusesOrderAmends)
            {
                Client.RaiseModifyRejectedById(command, "this venue cannot change an order once it is placed; cancel it and submit a new one", ts);
                return;
            }

            engine.ProcessModify(order, command.Quantity, command.Price, command.TriggerPrice, ts);
        });
    }

    internal void Cancel(CancelOrder command)
    {
        Enqueue(_latency.CancelLatency, () =>
        {
            UnixNanos ts = Now;
            if (!_orders.TryGetValue(command.ClientOrderId, out Order? order) || !_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
            {
                Client.RaiseCancelRejectedById(command, "order not found at venue", ts);
                return;
            }

            if (_pendingChildren.Remove(order))
            {
                bool released = Release(order);
                Client.RaiseCanceled(order, ts);
                PublishHolds(released, ts);
                return;
            }

            engine.ProcessCancel(order, ts);
        });
    }

    internal void CancelAll(CancelAllOrders command)
    {
        Enqueue(_latency.CancelLatency, () =>
        {
            UnixNanos ts = Now;
            foreach (Order child in _pendingChildren.Where(c => c.InstrumentId == command.InstrumentId && c.StrategyId == command.StrategyId).ToList())
            {
                _pendingChildren.Remove(child);
                bool released = Release(child);
                Client.RaiseCanceled(child, ts);
                PublishHolds(released, ts);
            }

            if (_engines.TryGetValue(command.InstrumentId, out OrderMatchingEngine? engine))
            {
                engine.ProcessCancelAll(command.OrderSide, command.StrategyId, ts);
            }
        });
    }

    // ----- IMatchingEngineHost -----

    public VenueOrderId NextVenueOrderId() => new($"V-{++_venueOrderCount}");

    public TradeId NextTradeId() => new($"T-{++_tradeCount}");

    public decimal NetPosition(InstrumentId instrumentId) => _positions.TryGetValue(instrumentId, out VenuePosition? p) ? p.SignedQuantity : 0m;

    public void OnAccepted(Order order, VenueOrderId venueOrderId, UnixNanos ts)
    {
        bool changed = Reserve(order);
        Client.RaiseAccepted(order, venueOrderId, ts);
        PublishHolds(changed, ts);
    }

    public void OnRejected(Order order, string reason, UnixNanos ts)
    {
        bool released = Release(order);
        _orders.Remove(order.ClientOrderId);
        Client.RaiseRejected(order, reason, ts);
        PublishHolds(released, ts);
    }

    public void OnCanceled(Order order, UnixNanos ts)
    {
        bool released = Release(order);
        _orders.Remove(order.ClientOrderId);
        _restoredLinks.Remove(order.ClientOrderId);
        Client.RaiseCanceled(order, ts);
        PublishHolds(released, ts);
        HandleContingencyOnClose(order, ts);
    }

    public void OnExpired(Order order, UnixNanos ts)
    {
        bool released = Release(order);
        _orders.Remove(order.ClientOrderId);
        Client.RaiseExpired(order, ts);
        PublishHolds(released, ts);
        HandleContingencyOnClose(order, ts);
    }

    public void OnTriggered(Order order, UnixNanos ts) => Client.RaiseTriggered(order, ts);

    public void OnUpdated(Order order, Quantity quantity, Price? price, Price? triggerPrice, UnixNanos ts)
    {
        bool changed = Reserve(order, quantity, price);
        Client.RaiseUpdated(order, quantity, price, triggerPrice, ts);
        PublishHolds(changed, ts);
    }

    public void OnModifyRejected(Order order, string reason, UnixNanos ts) => Client.RaiseModifyRejected(order, reason, ts);

    public void OnCancelRejected(Order order, string reason, UnixNanos ts) => Client.RaiseCancelRejected(order, reason, ts);

    public void OnFilled(Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide, UnixNanos ts)
    {
        Instrument instrument = _instruments[order.InstrumentId];
        Money commission = _feeModel.Commission(instrument, order, lastQty, lastPx, liquiditySide);
        TradeId tradeId = NextTradeId();

        // The reservation gives way to the real movement the fill makes.
        Release(order);

        ApplyFillToAccount(instrument, order, lastQty, lastPx, commission);
        _orders.Remove(order.ClientOrderId);
        Client.RaiseFilled(order, tradeId, lastQty, lastPx, commission, liquiditySide, ts);
        Client.RaiseAccountState(BuildBalances(), _config.AccountType == AccountType.Margin ? BuildMargins() : [], reported: true, ts);

        HandleContingencyOnFill(order, ts);
    }

    // ----- Contingencies -----

    private void HandleContingencyOnFill(Order filled, UnixNanos ts)
    {
        if (!_config.SupportContingentOrders)
        {
            return;
        }

        // Release OTO children of a filled parent.
        if (filled.Contingency == ContingencyType.Oto)
        {
            foreach (Order child in _pendingChildren.Where(c => c.ParentOrderId == filled.ClientOrderId).ToList())
            {
                _pendingChildren.Remove(child);
                if (_engines.TryGetValue(child.InstrumentId, out OrderMatchingEngine? engine))
                {
                    // The child was accepted when the list arrived and has been waiting here since; it starts working
                    // under the venue order id its owner already knows, and is not announced as accepted a second time.
                    VenueOrderId? announced = _announcedChildren.Remove(child.ClientOrderId, out VenueOrderId known) ? known : null;
                    engine.ProcessOrder(child, ts, announced);
                }
            }
        }

        // OCO/OUO: cancel the linked orders.
        if (filled.Contingency is ContingencyType.Oco or ContingencyType.Ouo || _restoredLinks.ContainsKey(filled.ClientOrderId))
        {
            CancelLinked(filled, ts);
        }
    }

    private void HandleContingencyOnClose(Order closed, UnixNanos ts)
    {
        if (!_config.SupportContingentOrders)
        {
            return;
        }

        if (closed.Contingency == ContingencyType.Oto)
        {
            foreach (Order child in _pendingChildren.Where(c => c.ParentOrderId == closed.ClientOrderId).ToList())
            {
                _pendingChildren.Remove(child);
                bool released = Release(child);
                Client.RaiseCanceled(child, ts);
                PublishHolds(released, ts);
            }
        }
    }

    private void CancelLinked(Order order, UnixNanos ts)
    {
        IEnumerable<ClientOrderId> linkedIds = _restoredLinks.Remove(order.ClientOrderId, out IReadOnlyList<ClientOrderId>? restored) ? order.LinkedOrderIds.Union(restored) : order.LinkedOrderIds;
        foreach (ClientOrderId linkedId in linkedIds.ToList())
        {
            if (!_orders.TryGetValue(linkedId, out Order? linked) || linked.IsClosed)
            {
                continue;
            }

            if (_pendingChildren.Remove(linked))
            {
                _orders.Remove(linkedId);
                bool released = Release(linked);
                Client.RaiseCanceled(linked, ts);
                PublishHolds(released, ts);
                continue;
            }

            if (_engines.TryGetValue(linked.InstrumentId, out OrderMatchingEngine? engine) && engine.OpenOrders.Contains(linked))
            {
                engine.ProcessCancel(linked, ts);
            }
        }
    }

    /// <summary>
    /// How much size the venue still has resting ahead of that order at its price. Null for an order that is not
    /// standing in a queue - one that never rested, one that has left the book, or a stop still waiting for its
    /// trigger - and zero for one at the front of the queue, which is not the same thing.
    /// </summary>
    public decimal? SizeAhead(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return _engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine) ? engine.SizeAhead(order.ClientOrderId) : null;
    }

    /// <summary>
    /// Where that order stands among this account's own orders at the same price and side: 1 for the one nearest the
    /// front. The strangers ahead of it are anonymous, so what is known about them is their size, not their number.
    /// </summary>
    public int? QueuePosition(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return _engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine) ? engine.QueuePosition(order.ClientOrderId) : null;
    }

    // ----- Account -----

    /// <summary>
    /// What the account has to hold free before the venue will work the order, currency by currency. On a cash account
    /// that is the currency the order spends, plus the commission wherever the fill does not pay it out of what it
    /// receives; on a margin account it is the initial margin the order would have to post. Null when nothing is
    /// required - a reduce-only order, an order that would close rather than open, or a buy with no price to judge it
    /// by.
    /// </summary>
    private Dictionary<Currency, decimal>? Requirement(Order order, Quantity? quantity = null, Price? price = null)
    {
        if (order.IsReduceOnly)
        {
            return null;
        }

        if (!_instruments.TryGetValue(order.InstrumentId, out Instrument? instrument) || !_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
        {
            return null;
        }

        if (_config.AccountType != AccountType.Cash)
        {
            return MarginRequirement(order, instrument, engine, quantity, price);
        }

        Quantity qty = quantity ?? order.Quantity;
        Currency baseCurrency = instrument.BaseCurrency ?? instrument.QuoteCurrency;
        Currency received = order.IsBuy ? baseCurrency : instrument.QuoteCurrency;
        decimal? px = (price ?? order.Price)?.Value ?? (order.IsBuy ? engine.BestAsk : engine.BestBid) ?? engine.LastPrice;

        Dictionary<Currency, decimal> required = new();
        if (order.IsBuy)
        {
            if (px is null)
            {
                return null;
            }

            required[instrument.QuoteCurrency] = qty.Value * px.Value * instrument.Multiplier.Value;
        }
        else
        {
            required[baseCurrency] = qty.Value * instrument.Multiplier.Value;
        }

        if (px is { } commissionPx)
        {
            // The fee has to fit as well: a buy for the whole balance used to be accepted and then took the balance
            // below zero when the commission was charged.
            Money commission = _feeModel.Commission(instrument, order, qty, instrument.MakePrice(commissionPx), LiquiditySide.Taker);
            if (commission.Amount > 0m && !commission.Currency.Equals(received))
            {
                required[commission.Currency] = required.GetValueOrDefault(commission.Currency) + commission.Amount;
            }
        }

        return required;
    }

    /// <summary>
    /// The margin a working order holds. A venue does not let one balance back several orders: the margin an order
    /// would have to post is held the moment the order is worked, not when it fills, or an account could commit the
    /// same money as many times over as it can send orders. What closes a position asks for nothing, because closing
    /// gives margin back rather than wanting more.
    /// </summary>
    private Dictionary<Currency, decimal>? MarginRequirement(Order order, Instrument instrument, OrderMatchingEngine engine, Quantity? quantity, Price? price)
    {
        // On a netting venue an order against the position closes part of it and gives margin back. On a hedging
        // venue the same order opens a position of its own, and that one has to be paid for like any other.
        decimal net = NetPosition(order.InstrumentId);
        bool closes = net != 0m && ((net > 0m && order.IsSell) || (net < 0m && order.IsBuy));
        if (closes && _config.OmsType != OmsType.Hedging)
        {
            return null;
        }

        if (((price ?? order.Price)?.Value ?? (order.IsBuy ? engine.BestAsk : engine.BestBid) ?? engine.LastPrice) is not { } px)
        {
            return null;
        }

        Money notional = instrument.NotionalValue(quantity ?? order.Quantity, instrument.MakePrice(px));
        decimal margin = notional.Amount * instrument.InitialMarginRate(Leverage(order.InstrumentId));
        return margin <= 0m ? null : new Dictionary<Currency, decimal> { [notional.Currency] = margin };
    }

    /// <summary>Why the account cannot carry the order, or null when it can.</summary>
    private string? Unaffordable(Order order)
    {
        Dictionary<Currency, decimal>? required = Requirement(order);
        if (required is null)
        {
            return null;
        }

        foreach ((Currency currency, decimal amount) in required)
        {
            if (Free(currency) >= amount)
            {
                continue;
            }

            return _config.AccountType == AccountType.Cash
                ? "insufficient balance"
                : $"insufficient margin: {new Money(amount, currency)} needed at {Leverage(order.InstrumentId)}x leverage, {new Money(Free(currency), currency)} free";
        }

        return null;
    }

    /// <summary>Holds what the order needs until it fills or closes, so a second order cannot spend the same balance.</summary>
    private bool Reserve(Order order, Quantity? quantity = null, Price? price = null)
    {
        bool changed = Release(order);
        Dictionary<Currency, decimal>? required = Requirement(order, quantity, price);
        if (required is null || required.Count == 0)
        {
            return changed;
        }

        _reserved[order.ClientOrderId] = required;
        foreach ((Currency currency, decimal amount) in required)
        {
            _locked[currency] = _locked.GetValueOrDefault(currency) + amount;
        }

        return true;
    }

    private bool Release(Order order)
    {
        if (!_reserved.Remove(order.ClientOrderId, out Dictionary<Currency, decimal>? required))
        {
            return false;
        }

        foreach ((Currency currency, decimal amount) in required)
        {
            decimal left = _locked.GetValueOrDefault(currency) - amount;
            if (left <= 0m)
            {
                _locked.Remove(currency);
            }
            else
            {
                _locked[currency] = left;
            }
        }

        return true;
    }

    /// <summary>
    /// Publishes the account when a hold was taken or given back. Balances only move on a fill, but what is free changes
    /// the moment an order rests or stops resting, and a strategy reading its account has to see that.
    /// </summary>
    private void PublishHolds(bool changed, UnixNanos ts)
    {
        if (changed)
        {
            Client.RaiseAccountState(BuildBalances(), [], reported: true, ts);
        }
    }

    /// <summary>
    /// The balance nothing has claimed yet: what is there, less the holds taken for working orders, and on a margin
    /// account less the margin its open positions are already posting.
    /// </summary>
    private decimal Free(Currency currency)
    {
        decimal claimed = _locked.GetValueOrDefault(currency);
        if (_config.AccountType == AccountType.Margin)
        {
            claimed += TotalInitialMargin(currency);
        }

        return _balances.GetValueOrDefault(currency) - claimed;
    }

    private void ApplyFillToAccount(Instrument instrument, Order order, Quantity lastQty, Price lastPx, Money commission)
    {
        decimal qty = lastQty.Value;
        decimal px = lastPx.Value;
        VenuePosition position = GetPosition(instrument.Id);
        decimal realized = position.Apply(order.Side, qty, px, instrument);

        if (_config.AccountType == AccountType.Cash)
        {
            decimal notional = qty * px * instrument.Multiplier.Value;
            Currency baseCurrency = instrument.BaseCurrency ?? instrument.QuoteCurrency;
            if (order.IsBuy)
            {
                Adjust(instrument.QuoteCurrency, -notional);
                Adjust(baseCurrency, qty * instrument.Multiplier.Value);
            }
            else
            {
                Adjust(instrument.QuoteCurrency, notional);
                Adjust(baseCurrency, -qty * instrument.Multiplier.Value);
            }
        }
        else
        {
            Adjust(instrument.SettlementCurrency, realized);
        }

        Adjust(commission.Currency, -commission.Amount);
    }

    private VenuePosition GetPosition(InstrumentId instrumentId)
    {
        if (!_positions.TryGetValue(instrumentId, out VenuePosition? position))
        {
            position = new VenuePosition();
            _positions[instrumentId] = position;
        }

        return position;
    }

    private void Adjust(Currency currency, decimal amount) => _balances[currency] = _balances.GetValueOrDefault(currency) + amount;

    private List<AccountBalance> BuildBalances()
    {
        List<AccountBalance> balances = new();
        foreach ((Currency currency, decimal amount) in _balances)
        {
            Money total = new(amount, currency);
            Money locked = _config.AccountType == AccountType.Margin
                ? new Money(Math.Min(TotalInitialMargin(currency) + _locked.GetValueOrDefault(currency), amount), currency)
                : new Money(Math.Min(_locked.GetValueOrDefault(currency), amount), currency);

            balances.Add(AccountBalance.Of(total, locked));
        }

        return balances;
    }

    private List<MarginBalance> BuildMargins()
    {
        List<MarginBalance> margins = new();
        foreach ((InstrumentId instrumentId, VenuePosition position) in _positions)
        {
            if (position.SignedQuantity == 0m || !_instruments.TryGetValue(instrumentId, out Instrument? instrument))
            {
                continue;
            }

            Quantity quantity = new(Math.Abs(position.SignedQuantity), instrument.SizePrecision);
            Price price = instrument.MakePrice(position.AvgPx);
            Money notional = instrument.NotionalValue(quantity, price);
            margins.Add(new MarginBalance(
                new Money(notional.Amount * instrument.InitialMarginRate(Leverage(instrumentId)), notional.Currency),
                new Money(notional.Amount * instrument.MaintenanceMarginRate, notional.Currency),
                instrumentId));
        }

        return margins;
    }

    private decimal TotalInitialMargin(Currency currency) => BuildMargins().Where(m => m.Currency.Equals(currency)).Sum(m => m.Initial.Amount);

    // ----- Reports (for reconciliation tests) -----

    public ExecutionMassStatus GenerateMassStatus()
    {
        List<OrderStatusReport> orders = new();
        foreach (OrderMatchingEngine engine in _engines.Values)
        {
            foreach (Order order in engine.OpenOrders)
            {
                orders.Add(new OrderStatusReport(AccountId, order.InstrumentId, order.ClientOrderId, engine.VenueOrderIdFor(order.ClientOrderId) ?? new VenueOrderId("V-0"),
                    order.Side, order.Type, order.TimeInForce, order.Status, order.Quantity, order.FilledQuantity, order.TsAccepted ?? order.TsInit, order.TsLast, Now, Guid.NewGuid(),
                    order.Price, order.TriggerPrice, order.TriggerType, Tags: order.Tags));
            }
        }

        List<PositionStatusReport> positions = new();
        foreach ((InstrumentId id, VenuePosition position) in _positions)
        {
            if (position.SignedQuantity == 0m)
            {
                continue;
            }

            Instrument instrument = _instruments[id];
            positions.Add(new PositionStatusReport(AccountId, id, position.SignedQuantity > 0m ? PositionSide.Long : PositionSide.Short,
                new Quantity(Math.Abs(position.SignedQuantity), instrument.SizePrecision), Now, Now, Guid.NewGuid(), null, position.AvgPx));
        }

        return new ExecutionMassStatus(Client.ClientId, AccountId, Venue, orders, [], positions, Now, Guid.NewGuid());
    }

    public override string ToString() => $"SimulatedExchange({Venue}, {_config.AccountType}, balances=[{string.Join(", ", _balances.Select(kv => kv.Value.ToString(CultureInfo.InvariantCulture) + " " + kv.Key.Code))}])";

    /// <summary>
    /// Venue-side net position used for reduce-only checks and margin P&amp;L.
    /// </summary>
    private sealed class VenuePosition
    {
        public VenuePosition()
        {
        }

        public VenuePosition(decimal signedQuantity, decimal avgPx)
        {
            SignedQuantity = signedQuantity;
            AvgPx = avgPx;
        }

        public decimal SignedQuantity { get; private set; }

        public decimal AvgPx { get; private set; }

        /// <summary>Applies a fill and returns the realized P&amp;L in the settlement currency.</summary>
        public decimal Apply(OrderSide side, decimal qty, decimal px, Instrument instrument)
        {
            decimal signed = side == OrderSide.Buy ? qty : -qty;
            decimal realized = 0m;

            if (SignedQuantity == 0m || Math.Sign(SignedQuantity) == Math.Sign(signed))
            {
                decimal total = Math.Abs(SignedQuantity) + qty;
                AvgPx = total == 0m ? px : (AvgPx * Math.Abs(SignedQuantity) + px * qty) / total;
                SignedQuantity += signed;
                return 0m;
            }

            decimal closeQty = Math.Min(qty, Math.Abs(SignedQuantity));
            bool wasLong = SignedQuantity > 0m;
            decimal raw = instrument.IsInverse
                ? ((1m / AvgPx) - (1m / px)) * closeQty * instrument.Multiplier.Value
                : (px - AvgPx) * closeQty * instrument.Multiplier.Value;
            realized = wasLong ? raw : -raw;

            decimal remainder = qty - closeQty;
            SignedQuantity += signed;
            if (remainder > 0m)
            {
                AvgPx = px;
            }
            else if (SignedQuantity == 0m)
            {
                AvgPx = 0m;
            }

            return realized;
        }
    }
    /// <summary>
    /// What an added behaviour is allowed to see and do (R8.19). Its own type rather than the exchange implementing
    /// the interface, so a module is handed a surface it cannot reach past: it can read positions, prices and
    /// notionals, and it can move money. There is no way from here to fill an order, cancel one or change a
    /// position, because a behaviour that needed to do those is the matching engine and belongs in it.
    /// </summary>
    private sealed class ModuleState(SimulatedExchange exchange) : ISimulatedVenueState
    {
        public Venue Venue => exchange.Venue;

        public ILogger Log => exchange._log;

        public IReadOnlyList<(InstrumentId InstrumentId, decimal SignedQuantity)> OpenPositions =>
            [.. exchange._positions
                .Where(p => p.Value.SignedQuantity != 0m)
                .OrderBy(p => p.Key.Value, StringComparer.Ordinal)
                .Select(p => (p.Key, p.Value.SignedQuantity))];

        public Instrument? Instrument(InstrumentId instrumentId) =>
            exchange._instruments.GetValueOrDefault(instrumentId);

        public decimal? Price(InstrumentId instrumentId) =>
            exchange._engines.TryGetValue(instrumentId, out OrderMatchingEngine? engine)
                ? engine.LastPrice ?? engine.BestBid ?? engine.BestAsk
                : null;

        public decimal? Notional(InstrumentId instrumentId)
        {
            if (Instrument(instrumentId) is not { } instrument
                || !exchange._positions.TryGetValue(instrumentId, out VenuePosition? position)
                || position.SignedQuantity == 0m
                || Price(instrumentId) is not { } price
                || price <= 0m)
            {
                return null;
            }

            // Valued the way this venue values it, which is the same arithmetic funding uses: an inverse instrument
            // is quoted the other way up, and a contract is a multiple of the instrument's own unit.
            decimal size = Math.Abs(position.SignedQuantity);
            return instrument.IsInverse
                ? size * instrument.Multiplier.Value / price
                : size * price * instrument.Multiplier.Value;
        }

        public void Charge(string module, Money amount, string reason, UnixNanos ts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(module);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (amount.Amount == 0m)
            {
                // Nothing moved, so nothing happened - and recording it would make the run claim a behaviour
                // applied when it did not.
                return;
            }

            exchange.Adjust(amount.Currency, amount.Amount);
            exchange._moduleCharges.Add(new ModuleCharge(module, amount, reason, ts));
            exchange._log.LogInformation("{Module} charged {Amount}: {Reason}", module, amount, reason);
            exchange.Client.RaiseAccountState(
                exchange.BuildBalances(),
                exchange._config.AccountType == AccountType.Margin ? exchange.BuildMargins() : [],
                reported: true,
                ts);
        }
    }
}
