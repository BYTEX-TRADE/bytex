using System.Globalization;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
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

    public decimal DefaultLeverage { get; init; } = 1m;

    public IReadOnlyDictionary<InstrumentId, decimal> Leverages { get; init; } = new Dictionary<InstrumentId, decimal>();

    public FillModel? FillModel { get; init; }

    public FeeModel? FeeModel { get; init; }

    public LatencyModel? LatencyModel { get; init; }

    public BarExecutionMode BarExecution { get; init; } = BarExecutionMode.OhlcPath;

    public BookType BookType { get; init; } = BookType.L1;

    /// <summary>Reject stop orders whose trigger is already through the market on arrival.</summary>
    public bool RejectStopOrdersAtMarket { get; init; } = true;

    /// <summary>Venue supports contingent order lists natively (OCO/OUO cancel or update linked orders).</summary>
    public bool SupportContingentOrders { get; init; } = true;
}

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
    private readonly Dictionary<InstrumentId, VenuePosition> _positions = new();
    private readonly Dictionary<ClientOrderId, Order> _orders = new();
    private readonly PriorityQueue<Action, (long Due, long Seq)> _inflight = new();
    private readonly ILogger _log;
    private BacktestExecutionClient? _client;
    private long _venueOrderCount;
    private long _tradeCount;
    private long _inflightSeq;

    public SimulatedExchange(SimulatedVenueConfig config, KernelServices services)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(services);
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

    public AccountId AccountId => new($"{_config.Venue}-001");

    public IReadOnlyDictionary<InstrumentId, Instrument> Instruments => _instruments;

    public IReadOnlyDictionary<Currency, decimal> Balances => _balances;

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

        _instruments[instrument.Id] = instrument;
        _engines[instrument.Id] = new OrderMatchingEngine(instrument, this, _fillModel, _config.BarExecution, _config.RejectStopOrdersAtMarket);
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
        _engines.Clear();
        foreach (Instrument instrument in _instruments.Values)
        {
            _engines[instrument.Id] = new OrderMatchingEngine(instrument, this, _fillModel, _config.BarExecution, _config.RejectStopOrdersAtMarket);
        }

        _balances.Clear();
        foreach (Money balance in _config.StartingBalances)
        {
            _balances[balance.Currency] = balance.Amount;
        }

        _positions.Clear();
        _orders.Clear();
        _inflight.Clear();
        _venueOrderCount = 0;
        _tradeCount = 0;
    }

    // ----- Market data -----

    public void ProcessQuoteTick(QuoteTick quote)
    {
        ProcessDueCommands(quote.TsEvent);
        if (_engines.TryGetValue(quote.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessQuote(quote);
        }
    }

    public void ProcessTradeTick(TradeTick trade)
    {
        ProcessDueCommands(trade.TsEvent);
        if (_engines.TryGetValue(trade.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessTrade(trade);
        }
    }

    public void ProcessBar(Bar bar)
    {
        ProcessDueCommands(bar.TsEvent);
        if (_engines.TryGetValue(bar.BarType.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessBar(bar);
        }
    }

    public void ProcessOrderBook(OrderBook book, UnixNanos ts)
    {
        ProcessDueCommands(ts);
        if (_engines.TryGetValue(book.InstrumentId, out OrderMatchingEngine? engine))
        {
            engine.ProcessBook(book, ts);
        }
    }

    /// <summary>
    /// Executes every queued command whose latency has elapsed at <paramref name="now"/>.
    /// </summary>
    public void ProcessDueCommands(UnixNanos now)
    {
        while (_inflight.TryPeek(out Action? action, out (long Due, long Seq) priority) && priority.Due <= now.Value)
        {
            _inflight.Dequeue();
            action();
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
        Enqueue(_latency.InsertLatency, () =>
        {
            UnixNanos ts = Now;
            Client.RaiseSubmitted(order, ts);
            if (!_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
            {
                Client.RaiseRejected(order, $"instrument {order.InstrumentId} not registered with simulated venue {Venue}", ts);
                return;
            }

            if (!HasBalanceFor(order))
            {
                Client.RaiseRejected(order, "insufficient balance", ts);
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
                        Client.RaiseAccepted(child, NextVenueOrderId(), ts);
                    }
                }

                return;
            }

            foreach (Order order in command.OrderList.Orders)
            {
                engine.ProcessOrder(order, ts);
            }
        });
    }

    private readonly List<Order> _pendingChildren = new();

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
                Client.RaiseCanceled(order, ts);
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
                Client.RaiseCanceled(child, ts);
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

    public void OnAccepted(Order order, VenueOrderId venueOrderId, UnixNanos ts) => Client.RaiseAccepted(order, venueOrderId, ts);

    public void OnRejected(Order order, string reason, UnixNanos ts)
    {
        _orders.Remove(order.ClientOrderId);
        Client.RaiseRejected(order, reason, ts);
    }

    public void OnCanceled(Order order, UnixNanos ts)
    {
        _orders.Remove(order.ClientOrderId);
        Client.RaiseCanceled(order, ts);
        HandleContingencyOnClose(order, ts);
    }

    public void OnExpired(Order order, UnixNanos ts)
    {
        _orders.Remove(order.ClientOrderId);
        Client.RaiseExpired(order, ts);
        HandleContingencyOnClose(order, ts);
    }

    public void OnTriggered(Order order, UnixNanos ts) => Client.RaiseTriggered(order, ts);

    public void OnUpdated(Order order, Quantity quantity, Price? price, Price? triggerPrice, UnixNanos ts) => Client.RaiseUpdated(order, quantity, price, triggerPrice, ts);

    public void OnModifyRejected(Order order, string reason, UnixNanos ts) => Client.RaiseModifyRejected(order, reason, ts);

    public void OnCancelRejected(Order order, string reason, UnixNanos ts) => Client.RaiseCancelRejected(order, reason, ts);

    public void OnFilled(Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide, UnixNanos ts)
    {
        Instrument instrument = _instruments[order.InstrumentId];
        Money commission = _feeModel.Commission(instrument, order, lastQty, lastPx, liquiditySide);
        TradeId tradeId = NextTradeId();

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
                    Client.RaiseSubmitted(child, ts);
                    engine.ProcessOrder(child, ts);
                }
            }
        }

        // OCO/OUO: cancel the linked orders.
        if (filled.Contingency is ContingencyType.Oco or ContingencyType.Ouo)
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
                Client.RaiseCanceled(child, ts);
            }
        }
    }

    private void CancelLinked(Order order, UnixNanos ts)
    {
        foreach (ClientOrderId linkedId in order.LinkedOrderIds)
        {
            if (!_orders.TryGetValue(linkedId, out Order? linked) || linked.IsClosed)
            {
                continue;
            }

            if (_pendingChildren.Remove(linked))
            {
                _orders.Remove(linkedId);
                Client.RaiseCanceled(linked, ts);
                continue;
            }

            if (_engines.TryGetValue(linked.InstrumentId, out OrderMatchingEngine? engine) && engine.OpenOrders.Contains(linked))
            {
                engine.ProcessCancel(linked, ts);
            }
        }
    }

    // ----- Account -----

    private bool HasBalanceFor(Order order)
    {
        if (_config.AccountType != AccountType.Cash || order.IsReduceOnly)
        {
            return true;
        }

        Instrument instrument = _instruments[order.InstrumentId];
        if (!_engines.TryGetValue(order.InstrumentId, out OrderMatchingEngine? engine))
        {
            return true;
        }

        if (order.IsBuy)
        {
            decimal? px = order.Price?.Value ?? engine.BestAsk ?? engine.LastPrice;
            if (px is null)
            {
                return true;
            }

            decimal required = order.Quantity.Value * px.Value * instrument.Multiplier.Value;
            return _balances.GetValueOrDefault(instrument.QuoteCurrency) >= required;
        }

        Currency baseCurrency = instrument.BaseCurrency ?? instrument.QuoteCurrency;
        return _balances.GetValueOrDefault(baseCurrency) >= order.Quantity.Value * instrument.Multiplier.Value;
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
            Money locked = Money.Zero(currency);
            if (_config.AccountType == AccountType.Margin)
            {
                locked = new Money(TotalInitialMargin(currency), currency);
            }

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

            decimal leverage = _config.Leverages.TryGetValue(instrumentId, out decimal l) ? l : _config.DefaultLeverage;
            Quantity quantity = new(Math.Abs(position.SignedQuantity), instrument.SizePrecision);
            Price price = instrument.MakePrice(position.AvgPx);
            Money notional = instrument.NotionalValue(quantity, price);
            decimal adjusted = notional.Amount / leverage;
            margins.Add(new MarginBalance(new Money(adjusted * Math.Max(instrument.MarginInit, 1m / leverage), notional.Currency), new Money(adjusted * instrument.MarginMaint, notional.Currency), instrumentId));
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
                    order.Price, order.TriggerPrice, order.TriggerType));
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
}
