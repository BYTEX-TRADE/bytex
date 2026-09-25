using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Positions;
using Bytex.Core.Portfolios;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Engines;

public sealed record RiskEngineConfig
{
    /// <summary>Skip all checks (for trusted environments and some tests).</summary>
    public bool Bypass { get; init; }

    /// <summary>Maximum number of orders per <see cref="OrderRateInterval"/>.</summary>
    public int MaxOrderSubmitRate { get; init; } = 100;

    public TimeSpan OrderRateInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of modify commands per <see cref="OrderRateInterval"/>.</summary>
    public int MaxOrderModifyRate { get; init; } = 100;

    /// <summary>Maximum notional per order by instrument, in the instrument's cost currency.</summary>
    public IReadOnlyDictionary<InstrumentId, decimal> MaxNotionalPerOrder { get; init; } = new Dictionary<InstrumentId, decimal>();

    /// <summary>Reject buy orders on cash accounts when free balance cannot cover them.</summary>
    public bool CheckCashBalance { get; init; } = true;

    /// <summary>
    /// Reject orders on margin accounts when the free balance cannot post the initial margin they need. On for the
    /// same reason the cash check is on: an engine that lets a node submit what its account cannot hold leaves the
    /// refusal to the venue, or worse, to a position nobody can carry. Turn it off for a venue that judges margin
    /// itself and is the only one that knows its own rules.
    /// </summary>
    public bool CheckMargin { get; init; } = true;

    /// <summary>
    /// What the account may lose, carry and have going at once. Nothing here is on until it is set, and a host may
    /// replace the whole of it while the node runs through <see cref="RiskEngine.SetLimits"/>.
    /// </summary>
    public RiskLimits Limits { get; init; } = new();

    /// <summary>
    /// How often the open-loss watch may run. It is driven by the prices the engine sees, and a busy venue sends
    /// thousands of them a second, so the interval bounds the work: a second of the engine's own clock, which is
    /// simulated time in a backtest and therefore the same every run. Zero watches on every price.
    /// </summary>
    public TimeSpan LossWatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    public bool LogDenials { get; init; } = true;
}

/// <summary>What the engine does to itself when a loss limit is reached.</summary>
public enum LossLimitBreach
{
    /// <summary>
    /// Deny the orders that would add while the loss stands at the limit, and judge the next order on its own merits.
    /// A loss that recovers, or a period that rolls, lets the next order through with nothing else happening.
    /// </summary>
    DenyAdds,

    /// <summary>
    /// Stop the engine trading, and leave it stopped: nothing that would add gets through until a host resumes it,
    /// whatever the loss does afterwards, and a new period does not release it either - the engine never starts
    /// trading again by itself. Orders that get a position out are still allowed, because a switch that trapped
    /// someone in a losing position would be worse than no switch at all.
    /// </summary>
    StopTrading,

    /// <summary>
    /// Stop trading as <see cref="StopTrading"/> does, and close what is open: every working order of every strategy
    /// is cancelled and every open position is sent a reduce-only market order. The account stops losing on the
    /// position that breached the limit, which stopping alone does not do - a stopped node holds what it held and goes
    /// on holding it until a person closes it.
    /// <para>
    /// Not the default, and never will be: closing someone's position without being asked is the one thing a limit
    /// must not do on its own. A host says so, in its configuration, over the control channel or on the command line.
    /// </para>
    /// </summary>
    Flatten,
}

/// <summary>
/// A limit the risk engine measures in money. It is either a fixed amount, which is only ever compared with a figure
/// in its own currency, or a percentage of the equity the account held when the period opened, which keeps its meaning
/// as the account grows. One or the other: a limit that says both, or neither, is not a limit.
/// </summary>
[JsonConverter(typeof(RiskLimit.Converter))]
public sealed record RiskLimit
{
    private RiskLimit()
    {
    }

    /// <summary>What the account may lose, or carry, in one currency.</summary>
    public static RiskLimit Of(Money amount) => amount.Amount > 0m
        ? new RiskLimit { Amount = amount }
        : throw new ArgumentOutOfRangeException(nameof(amount), amount, "A limit is above zero.");

    /// <summary>The same, as a percentage of the equity the account held when the current period opened.</summary>
    public static RiskLimit PercentOfEquity(decimal percent) => percent > 0m
        ? new RiskLimit { Percent = percent }
        : throw new ArgumentOutOfRangeException(nameof(percent), percent, "A limit is above zero.");

    public Money? Amount { get; private init; }

    public decimal? Percent { get; private init; }

    /// <summary>
    /// The limit as an amount of <paramref name="currency"/>: itself when it is set in that currency, the percentage
    /// applied to <paramref name="equity"/> when it is a percentage, and null when neither can be answered - a limit
    /// set in another currency, or a percentage of an equity the account does not report.
    /// </summary>
    public Money? In(Currency currency, Money? equity)
    {
        if (Amount is { } amount)
        {
            return amount.Currency.Equals(currency) ? amount : null;
        }

        return equity is { } e && e.Currency.Equals(currency) && Percent is { } percent
            ? new Money(e.Amount * percent / PercentDivisor, currency)
            : null;
    }

    /// <summary>
    /// The limit as text: "1000.00 USDT" for an amount, "5%" for a share of equity. A limit a host can write in a
    /// configuration file, send over the control channel and pass on a command line is the same limit everywhere.
    /// </summary>
    public override string ToString() => Amount is { } amount
        ? amount.ToString()
        : Percent!.Value.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Reads what <see cref="ToString"/> writes: "&lt;amount&gt; &lt;currency&gt;", or a percentage.</summary>
    public static RiskLimit Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string trimmed = text.Trim();
        return trimmed.EndsWith('%')
            ? PercentOfEquity(decimal.Parse(trimmed[..^1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture))
            : Of(Money.Parse(trimmed));
    }

    public static bool TryParse(string? text, out RiskLimit? limit)
    {
        try
        {
            limit = text is null ? null : Parse(text);
            return limit is not null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            limit = null;
            return false;
        }
    }

    private sealed class Converter : JsonConverter<RiskLimit>
    {
        public override RiskLimit? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, RiskLimit value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private const decimal PercentDivisor = 100m;
}

/// <summary>
/// What an account may lose, what it may carry, and how much it may have going at once. Every one of them is off
/// until it is set, so a node that says nothing about risk behaves as it always did. They travel together because a
/// host sets them together: once in the node's configuration, or over the control channel while it runs.
/// </summary>
public sealed record RiskLimits
{
    /// <summary>
    /// The most that may be lost inside one <see cref="LossPeriod"/> before orders that would add are denied. What
    /// has been realised and what the open positions are down both count, because a position held through a fall
    /// costs the account exactly as much as one that was closed in it, and the engine watches that figure between
    /// fills rather than only when an order arrives. Orders that reduce are always allowed: a limit that stopped
    /// someone getting out would be a trap.
    /// </summary>
    public RiskLimit? MaxLossPerPeriod { get; init; }

    /// <summary>
    /// What reaching <see cref="MaxLossPerPeriod"/> does. Stopping is the default: a limit that only denied the order
    /// in front of it would report the loss rather than protect the account, because the next order, and the one
    /// after, would each be judged as if nothing had happened. <see cref="LossLimitBreach.Flatten"/> also closes what
    /// is open, which has to be asked for.
    /// </summary>
    public LossLimitBreach OnLossLimit { get; init; } = LossLimitBreach.StopTrading;

    /// <summary>
    /// The window <see cref="MaxLossPerPeriod"/> is measured over. Periods are aligned to the epoch rather than to
    /// when the engine happened to start, so a day is a UTC day and a restart lands in the period it belongs to.
    /// </summary>
    public TimeSpan LossPeriod { get; init; } = TimeSpan.FromDays(1);

    /// <summary>The most exposure the account may carry at once, the order being judged included.</summary>
    public RiskLimit? MaxExposure { get; init; }

    /// <summary>The most open positions the account may hold on one instrument at once.</summary>
    public int? MaxOpenPositionsPerInstrument { get; init; }

    /// <summary>The most open positions the account may hold across every instrument at once.</summary>
    public int? MaxOpenPositions { get; init; }

    /// <summary>The most orders the account may have working on one instrument at once.</summary>
    public int? MaxWorkingOrdersPerInstrument { get; init; }

    /// <summary>The most orders the account may have working across every instrument at once.</summary>
    public int? MaxWorkingOrders { get; init; }

    /// <summary>Whether anything is set at all: an engine with nothing to enforce does not look at the portfolio.</summary>
    public bool IsEmpty =>
        MaxLossPerPeriod is null && MaxExposure is null && MaxOpenPositionsPerInstrument is null
        && MaxOpenPositions is null && MaxWorkingOrdersPerInstrument is null && MaxWorkingOrders is null;
}


/// <summary>
/// Validates trading commands before they reach the execution engine.
/// </summary>
public sealed class RiskEngine : Component
{
    private readonly IMessageBus _bus;
    private readonly Cache _cache;
    private readonly IPortfolio _portfolio;
    private readonly RiskEngineConfig _config;
    private RiskLimits _limits;
    private readonly Queue<UnixNanos> _submitTimes = new();
    private readonly Queue<UnixNanos> _modifyTimes = new();

    /// <summary>What each venue's realised PnL and equity were when the current loss period opened.</summary>
    private readonly Dictionary<Venue, PeriodMark> _marks = new();

    /// <summary>The period the engine began its life in; what happened before that belongs to no period it measures.</summary>
    private long? _startedPeriod;

    /// <summary>When the open-loss watch last ran, so that a stream of prices cannot turn it into the hot path.</summary>
    private UnixNanos? _lastWatch;

    /// <summary>The period in which the engine last stopped its own trading: it does that once per period, not once per price.</summary>
    private long? _stoppedPeriod;

    public RiskEngine(IMessageBus bus, Cache cache, IPortfolio portfolio, RiskEngineConfig? config = null)
        : base(new ComponentId("RiskEngine"))
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(portfolio);
        _bus = bus;
        _cache = cache;
        _portfolio = portfolio;
        _config = config ?? new RiskEngineConfig();
        _limits = _config.Limits;
        _bus.Register(Endpoints.RiskEngineExecute, Execute);

        // The open-loss watch is driven by whatever moves what an open position is worth. One throttled handler, and
        // it leaves at once when there is no loss limit to watch, so a node with no limits pays a field read per price.
        foreach (string topic in WatchedTopics)
        {
            _bus.Subscribe(topic, OnPriceSeen);
        }
    }

    public RiskEngineConfig Config => _config;

    /// <summary>What the engine is enforcing now, which is what the node was configured with until a host replaces it.</summary>
    public RiskLimits Limits => _limits;

    /// <summary>
    /// Replaces the limits while the engine runs: how a host applies a limit to a node it did not start, without
    /// restarting anything. What the period has already lost is not forgotten - a limit set at noon is measured
    /// against the same day - and nothing else about the configuration changes.
    /// </summary>
    public void SetLimits(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        Log.LogWarning("Risk limits set: loss {Loss} per {Period}, exposure {Exposure}, positions {PositionsPerInstrument}/{Positions}, orders {OrdersPerInstrument}/{Orders}",
            limits.MaxLossPerPeriod, limits.LossPeriod, limits.MaxExposure, limits.MaxOpenPositionsPerInstrument, limits.MaxOpenPositions,
            limits.MaxWorkingOrdersPerInstrument, limits.MaxWorkingOrders);
    }

    public TradingState TradingState { get; private set; } = TradingState.Active;

    public bool IsBypassed => _config.Bypass;

    public long CommandCount { get; private set; }

    public long DeniedCount { get; private set; }

    /// <summary>Orders denied because the realised loss of the period had reached <see cref="RiskLimits.MaxLossPerPeriod"/>.</summary>
    public long LossLimitDeniedCount { get; private set; }

    /// <summary>Orders denied because they would have carried the account past <see cref="RiskLimits.MaxExposure"/>.</summary>
    public long ExposureLimitDeniedCount { get; private set; }

    /// <summary>Orders denied because they would have taken the account past a position or working-order cap.</summary>
    public long CapDeniedCount { get; private set; }

    /// <summary>Orders denied because the account's free balance could not post the margin they need.</summary>
    public long MarginDeniedCount { get; private set; }

    /// <summary>How many times the engine has stopped its own trading because a loss limit was reached.</summary>
    public long LossLimitStoppedCount { get; private set; }

    /// <summary>How many positions the engine has sent a closing order for because a loss limit was reached.</summary>
    public long LossLimitFlattenedCount { get; private set; }

    /// <summary>How many times the open-loss watch has run: what the engine looked at between fills.</summary>
    public long LossWatchCount { get; private set; }

    public void SetTradingState(TradingState state)
    {
        TradingState = state;
        Log.LogWarning("Trading state set to {State}", state);
    }

    protected override void OnStart() => _startedPeriod = PeriodOf(Clock.Timestamp);

    protected override void OnReset()
    {
        _submitTimes.Clear();
        _modifyTimes.Clear();
        CommandCount = 0;
        DeniedCount = 0;
        _limits = _config.Limits;
        LossLimitDeniedCount = 0;
        ExposureLimitDeniedCount = 0;
        CapDeniedCount = 0;
        MarginDeniedCount = 0;
        _marks.Clear();
        _startedPeriod = null;
        _lastWatch = null;
        _stoppedPeriod = null;
        LossLimitStoppedCount = 0;
        LossLimitFlattenedCount = 0;
        LossWatchCount = 0;
        TradingState = TradingState.Active;
    }

    public void Execute(object message)
    {
        CommandCount++;
        switch (message)
        {
            case SubmitOrder submit:
                HandleSubmitOrder(submit);
                break;
            case SubmitOrderList list:
                HandleSubmitOrderList(list);
                break;
            case ModifyOrder modify:
                HandleModifyOrder(modify);
                break;
            case TradingCommand other:
                Forward(other);
                break;
            default:
                Log.LogError("RiskEngine cannot handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    private void HandleSubmitOrder(SubmitOrder command)
    {
        Order order = command.Order;
        if (_config.Bypass)
        {
            Forward(command);
            return;
        }

        if (TradingState == TradingState.Halted)
        {
            Deny(order, "TRADING_HALTED");
            return;
        }

        Instrument? instrument = _cache.Instrument(order.InstrumentId);
        if (instrument is null)
        {
            Deny(order, $"Instrument {order.InstrumentId} not found in cache");
            return;
        }

        if (TradingState == TradingState.Reducing && !WouldReduce(order))
        {
            Deny(order, "TRADING_REDUCING: order would not reduce a position");
            return;
        }

        string? reason = CheckOrder(instrument, order) ?? CheckLimits(instrument, order) ?? CheckCaps(instrument, order);
        if (reason is not null)
        {
            Deny(order, reason);
            return;
        }

        if (!CheckRate(_submitTimes, _config.MaxOrderSubmitRate))
        {
            Deny(order, $"Exceeded max order submit rate of {_config.MaxOrderSubmitRate} per {_config.OrderRateInterval}");
            return;
        }

        Forward(command);
    }

    private void HandleSubmitOrderList(SubmitOrderList command)
    {
        if (_config.Bypass)
        {
            Forward(command);
            return;
        }

        if (TradingState == TradingState.Halted)
        {
            foreach (Order order in command.OrderList.Orders)
            {
                Deny(order, "TRADING_HALTED");
            }

            return;
        }

        // A list is judged order by order against what the ones before it in the same list would already have added,
        // so a list cannot walk past a limit or a cap one small order at a time.
        Pending pending = new();
        foreach (Order order in command.OrderList.Orders)
        {
            Instrument? instrument = _cache.Instrument(order.InstrumentId);
            string? reason = instrument is null ? $"Instrument {order.InstrumentId} not found in cache"
                : TradingState == TradingState.Reducing && !WouldReduce(order) ? "TRADING_REDUCING: order would not reduce a position"
                : CheckOrder(instrument, order) ?? CheckLimits(instrument, order, pending) ?? CheckCaps(instrument, order, pending);
            if (reason is null && instrument is not null && !order.IsReduceOnly && !WouldReduce(order))
            {
                pending.Add(instrument.Id, Notional(instrument, order));
            }

            if (reason is not null)
            {
                foreach (Order o in command.OrderList.Orders)
                {
                    Deny(o, reason);
                }

                return;
            }
        }

        if (!CheckRate(_submitTimes, _config.MaxOrderSubmitRate))
        {
            foreach (Order o in command.OrderList.Orders)
            {
                Deny(o, $"Exceeded max order submit rate of {_config.MaxOrderSubmitRate} per {_config.OrderRateInterval}");
            }

            return;
        }

        Forward(command);
    }

    private void HandleModifyOrder(ModifyOrder command)
    {
        if (_config.Bypass)
        {
            Forward(command);
            return;
        }

        Order? order = _cache.Order(command.ClientOrderId);
        if (order is null)
        {
            Log.LogError("Cannot modify {ClientOrderId}: order not found", command.ClientOrderId);
            return;
        }

        if (order.IsClosed)
        {
            RejectModify(order, "order already closed");
            return;
        }

        if (order.IsPending)
        {
            RejectModify(order, $"order already {order.Status}");
            return;
        }

        Instrument? instrument = _cache.Instrument(order.InstrumentId);
        if (instrument is not null)
        {
            if (command.Quantity is { } quantity)
            {
                string? qtyReason = CheckQuantity(instrument, quantity);
                if (qtyReason is not null)
                {
                    RejectModify(order, qtyReason);
                    return;
                }
            }

            if (command.Price is { } price)
            {
                string? pxReason = CheckPrice(instrument, price);
                if (pxReason is not null)
                {
                    RejectModify(order, pxReason);
                    return;
                }
            }

            if (command.TriggerPrice is { } trigger)
            {
                string? trReason = CheckPrice(instrument, trigger);
                if (trReason is not null)
                {
                    RejectModify(order, trReason);
                    return;
                }
            }
        }

        if (!CheckRate(_modifyTimes, _config.MaxOrderModifyRate))
        {
            RejectModify(order, $"Exceeded max order modify rate of {_config.MaxOrderModifyRate} per {_config.OrderRateInterval}");
            return;
        }

        Forward(command);
    }

    private string? CheckOrder(Instrument instrument, Order order)
    {
        // A quote-quantity order states its size in the quote currency: 1,000 means 1,000 USDT, not 1,000 BTC. Judging
        // it against the instrument's size rules, or costing it as a size times a price, said a 1,000 USDT buy needed
        // fifty million USDT.
        string? qty = order.IsQuoteQuantity ? CheckQuoteQuantity(instrument, order.Quantity) : CheckQuantity(instrument, order.Quantity);
        if (qty is not null)
        {
            return qty;
        }

        if (order.Price is { } price)
        {
            string? px = CheckPrice(instrument, price);
            if (px is not null)
            {
                return px;
            }
        }

        if (order.TriggerPrice is { } trigger)
        {
            string? tp = CheckPrice(instrument, trigger);
            if (tp is not null)
            {
                return tp;
            }
        }

        Price? reference = ReferencePrice(order);
        if (reference is { } refPx)
        {
            // The quote quantity already IS the notional; converting it again multiplied money by a price.
            Money notional = Notional(instrument, order)!.Value;
            if (instrument.MaxNotional is { } maxNotional && notional.Currency.Equals(maxNotional.Currency) && notional.Amount > maxNotional.Amount)
            {
                return $"NOTIONAL_EXCEEDS_MAX: {notional} > {maxNotional}";
            }

            if (instrument.MinNotional is { } minNotional && notional.Currency.Equals(minNotional.Currency) && notional.Amount < minNotional.Amount)
            {
                return $"NOTIONAL_LESS_THAN_MIN: {notional} < {minNotional}";
            }

            if (_config.MaxNotionalPerOrder.TryGetValue(order.InstrumentId, out decimal maxPerOrder) && notional.Amount > maxPerOrder)
            {
                return $"NOTIONAL_EXCEEDS_MAX_PER_ORDER: {notional} > {maxPerOrder}";
            }

            if (_config.CheckCashBalance && _cache.AccountForVenue(order.InstrumentId.Venue) is CashAccount cash && !order.IsReduceOnly)
            {
                // A quote-quantity buy costs exactly the amount it names; a sell of that much quote currency has to
                // deliver the base it converts to at the reference price.
                Money required = order.IsQuoteQuantity
                    ? order.IsBuy
                        ? new Money(order.Quantity.Value, instrument.QuoteCurrency)
                        : cash.CalculateBalanceLocked(instrument, order.Side, instrument.MakeQuantity(order.Quantity.Value / refPx.Value), refPx)
                    : cash.CalculateBalanceLocked(instrument, order.Side, order.Quantity, refPx);
                Money free = cash.BalanceFree(required.Currency) ?? Money.Zero(required.Currency);
                if (free.Amount < required.Amount)
                {
                    return $"INSUFFICIENT_BALANCE: {required} required, {free} free";
                }
            }

            // The same question on a margin account: not what the order costs, but what has to be posted to hold it.
            // The venue reports the margin already committed as the locked part of the balance, so what is free is
            // free of it, and an order that needs more than that is one the account cannot carry. Anything that
            // reduces is let through, because closing a position gives margin back rather than asking for more.
            if (_config.CheckMargin && _cache.AccountForVenue(order.InstrumentId.Venue) is MarginAccount marginAccount
                && !order.IsReduceOnly && !WouldReduce(order))
            {
                Quantity size = order.IsQuoteQuantity ? instrument.MakeQuantity(order.Quantity.Value / refPx.Value) : order.Quantity;
                Money required = marginAccount.CalculateInitialMargin(instrument, size, refPx);
                Money free = marginAccount.BalanceFree(required.Currency) ?? Money.Zero(required.Currency);
                if (free.Amount < required.Amount)
                {
                    MarginDeniedCount++;
                    return $"INSUFFICIENT_MARGIN: {required} needed at {marginAccount.Leverage(instrument.Id)}x leverage, {free} free";
                }
            }
        }

        return null;
    }

    /// <summary>What can be checked about an amount of quote currency: that it is there, and that it is not absurd.</summary>
    private static string? CheckQuoteQuantity(Instrument instrument, Quantity quantity)
    {
        if (quantity.IsZero)
        {
            return "QUANTITY_ZERO";
        }

        if (instrument.MaxNotional is { } max && max.Currency.Equals(instrument.QuoteCurrency) && quantity.Value > max.Amount)
        {
            return $"NOTIONAL_EXCEEDS_MAX: {quantity} > {max}";
        }

        if (instrument.MinNotional is { } min && min.Currency.Equals(instrument.QuoteCurrency) && quantity.Value < min.Amount)
        {
            return $"NOTIONAL_LESS_THAN_MIN: {quantity} < {min}";
        }

        return null;
    }

    private static string? CheckQuantity(Instrument instrument, Quantity quantity)
    {
        if (quantity.Precision > instrument.SizePrecision)
        {
            return $"QUANTITY_PRECISION: {quantity.Precision} > instrument size precision {instrument.SizePrecision}";
        }

        if (instrument.SizeIncrement.Value > 0m && quantity.Value % instrument.SizeIncrement.Value != 0m)
        {
            return $"QUANTITY_INCREMENT: {quantity} is not a multiple of the size step {instrument.SizeIncrement}";
        }

        if (quantity.IsZero)
        {
            return "QUANTITY_ZERO";
        }

        if (instrument.MaxQuantity is { } max && quantity > max)
        {
            return $"QUANTITY_EXCEEDS_MAX: {quantity} > {max}";
        }

        if (instrument.MinQuantity is { } min && quantity < min)
        {
            return $"QUANTITY_LESS_THAN_MIN: {quantity} < {min}";
        }

        return null;
    }

    private static string? CheckPrice(Instrument instrument, Price price)
    {
        if (price.Precision > instrument.PricePrecision)
        {
            return $"PRICE_PRECISION: {price.Precision} > instrument price precision {instrument.PricePrecision}";
        }

        if (instrument.PriceIncrement.Value > 0m && price.Value % instrument.PriceIncrement.Value != 0m)
        {
            return $"PRICE_INCREMENT: {price} is not a multiple of the tick {instrument.PriceIncrement}";
        }

        if (!price.IsPositive)
        {
            return "PRICE_NOT_POSITIVE";
        }

        if (instrument.MaxPrice is { } max && price > max)
        {
            return $"PRICE_EXCEEDS_MAX: {price} > {max}";
        }

        if (instrument.MinPrice is { } min && price < min)
        {
            return $"PRICE_LESS_THAN_MIN: {price} < {min}";
        }

        return null;
    }

    /// <summary>
    /// The caps on how much the account may have going at once: open positions and working orders, per instrument and
    /// in total, counted from the cache. Like the limits, they only ever stop something being added - an order that
    /// reduces is not what runs a venue's book up - and they name the cap that stopped it. Returns the reason the
    /// order cannot be sent, or null.
    /// </summary>
    private string? CheckCaps(Instrument instrument, Order order, Pending? pending = null)
    {
        if (_limits.MaxOpenPositionsPerInstrument is null && _limits.MaxOpenPositions is null
            && _limits.MaxWorkingOrdersPerInstrument is null && _limits.MaxWorkingOrders is null)
        {
            return null;
        }

        if (order.IsReduceOnly || WouldReduce(order))
        {
            return null;
        }

        int here = _cache.PositionsOpenCount(instrumentId: instrument.Id);
        if (_limits.MaxOpenPositionsPerInstrument is { } perInstrument && here >= perInstrument)
        {
            CapDeniedCount++;
            return $"POSITION_CAP: {here} open on {instrument.Id} reaches the cap of {perInstrument}";
        }

        // Adding to a position the account already holds opens nothing new, so only an instrument it is flat on can
        // take the total past its cap.
        if (_limits.MaxOpenPositions is { } total && here == 0 && _cache.PositionsOpenCount() >= total)
        {
            CapDeniedCount++;
            return $"POSITION_CAP: {_cache.PositionsOpenCount()} open across the account reaches the cap of {total}";
        }

        int working = _cache.OrdersOpenCount(instrumentId: instrument.Id) + (pending?.Orders(instrument.Id) ?? 0);
        if (_limits.MaxWorkingOrdersPerInstrument is { } ordersHere && working >= ordersHere)
        {
            CapDeniedCount++;
            return $"ORDER_CAP: {working} working on {instrument.Id} reaches the cap of {ordersHere}";
        }

        int workingAll = _cache.OrdersOpenCount() + (pending?.Count ?? 0);
        if (_limits.MaxWorkingOrders is { } ordersAll && workingAll >= ordersAll)
        {
            CapDeniedCount++;
            return $"ORDER_CAP: {workingAll} working across the account reaches the cap of {ordersAll}";
        }

        return null;
    }

    /// <summary>
    /// What the orders already judged in one submitted list would add if they all went: an order list is judged order
    /// by order, and a list nobody counted could walk past a limit or a cap one small order at a time.
    /// </summary>
    private sealed class Pending
    {
        private readonly Dictionary<Currency, decimal> _exposure = new();
        private readonly Dictionary<InstrumentId, int> _orders = new();

        public int Count { get; private set; }

        public decimal Exposure(Currency currency) => _exposure.GetValueOrDefault(currency);

        public int Orders(InstrumentId instrumentId) => _orders.GetValueOrDefault(instrumentId);

        public void Add(InstrumentId instrumentId, Money? notional)
        {
            _orders[instrumentId] = _orders.GetValueOrDefault(instrumentId) + 1;
            Count++;
            if (notional is { } money)
            {
                _exposure[money.Currency] = _exposure.GetValueOrDefault(money.Currency) + money.Amount;
            }
        }
    }

    /// <summary>The price an order's value is judged against: its own, its trigger, or what the market last said.</summary>
    private Price? ReferencePrice(Order order) =>
        order.Price ?? order.TriggerPrice ?? _cache.Price(order.InstrumentId, order.IsBuy ? PriceType.Ask : PriceType.Bid) ?? _cache.Price(order.InstrumentId, PriceType.Last);

    /// <summary>
    /// What an order is worth, or null when nothing prices it. A quote-quantity order already names its notional; any
    /// other has to be costed at a reference price.
    /// </summary>
    private Money? Notional(Instrument instrument, Order order) =>
        order.IsQuoteQuantity ? new Money(order.Quantity.Value, instrument.QuoteCurrency)
        : ReferencePrice(order) is { } reference ? instrument.NotionalValue(order.Quantity, reference)
        : null;

    /// <summary>What the open-loss watch listens to: everything that changes what an open position is worth.</summary>
    private static readonly string[] WatchedTopics = [Topics.AllQuotes, Topics.AllTrades, Topics.AllBars, Topics.AllMarkPrices];

    /// <summary>
    /// A price arrived. The watch runs at most once per <see cref="RiskEngineConfig.LossWatchInterval"/> of the
    /// engine's own clock, which is simulated time in a backtest, so a run watches the same moments every time.
    /// </summary>
    private void OnPriceSeen(object message)
    {
        if (_config.Bypass || _limits.MaxLossPerPeriod is null || TradingState != TradingState.Active)
        {
            return;
        }

        UnixNanos now = Clock.Timestamp;
        if (_lastWatch is { } last && now - last < _config.LossWatchInterval)
        {
            return;
        }

        _lastWatch = now;
        WatchOpenLoss();
    }

    /// <summary>
    /// Reviews what the account is down at this moment - what the period has realised and what its open positions are
    /// worth - and stops the engine trading when a loss limit has been reached. Judging orders alone could not see a
    /// position moving against a node between one fill and the next, which is when the damage is done: a strategy that
    /// holds through a fall and sends nothing is invisible to a check that only runs on what it sends. A host may call
    /// this itself; nothing goes wrong if it is called more often than the interval, which only paces the prices.
    /// </summary>
    public void WatchOpenLoss()
    {
        LossWatchCount++;
        if (_config.Bypass || _limits.MaxLossPerPeriod is not { } limit || TradingState != TradingState.Active)
        {
            return;
        }

        foreach (Account account in _cache.Accounts())
        {
            Venue venue = account.Id.Venue;
            PeriodMark mark = MarkFor(venue);
            foreach (Currency currency in LossCurrencies(venue))
            {
                if (limit.In(currency, mark.Equity(currency)) is not { } allowed)
                {
                    continue;
                }

                decimal lost = LostThisPeriod(venue, currency, mark);
                if (lost >= allowed.Amount)
                {
                    StopTrading($"LOSS_LIMIT: {new Money(lost, currency)} lost this period, realised and open, reaches the limit of {allowed}");
                    return;
                }
            }
        }
    }

    /// <summary>The currencies a venue has anything to lose in: what it has realised, and what its open positions are in.</summary>
    private IEnumerable<Currency> LossCurrencies(Venue venue) =>
        _portfolio.RealizedPnls(venue).Keys.Union(_portfolio.UnrealizedPnls(venue).Keys);

    /// <summary>
    /// What the period has lost in one currency at this moment: how far realised and open profit together have fallen
    /// since the period opened. A loss is positive, which is how the limits read.
    /// </summary>
    private decimal LostThisPeriod(Venue venue, Currency currency, PeriodMark mark) =>
        mark.Realized(currency) + mark.Unrealized(currency)
        - Amount(_portfolio.RealizedPnls(venue), currency)
        - Amount(_portfolio.UnrealizedPnls(venue), currency);

    /// <summary>
    /// What the engine does to itself when a loss limit is reached, if it was told to do anything: it stops trading
    /// and stays stopped. Reducing, not halted - nothing that would add gets through, and everything that gets a
    /// position out still does. A host resumes it, and a new period does not.
    /// <para>
    /// Once per period, and no more: a host that resumes a node it stopped is not overruled a second later by the
    /// same loss. What it gets back is the rest of the period judged order by order - the limit still denies what
    /// would add while the loss stands - so a host that means to trade past the limit raises or clears the limit.
    /// </para>
    /// </summary>
    private void StopTrading(string reason)
    {
        long period = PeriodOf(Clock.Timestamp);
        if (_limits.OnLossLimit == LossLimitBreach.DenyAdds || TradingState != TradingState.Active || _stoppedPeriod == period)
        {
            return;
        }

        _stoppedPeriod = period;
        LossLimitStoppedCount++;
        Log.LogWarning("Trading stopped: {Reason}. Nothing that adds will be sent until a host resumes the node", reason);
        SetTradingState(TradingState.Reducing);

        if (_limits.OnLossLimit == LossLimitBreach.Flatten)
        {
            Flatten();
        }
    }

    /// <summary>
    /// Takes the book off the venue: every working order cancelled, every open position sent a reduce-only market
    /// order. Sent through this engine's own endpoint, the way a host's own flatten does, so the orders are judged
    /// like any other - they reduce, so the state this engine has just put itself into lets them through.
    /// </summary>
    private void Flatten()
    {
        TraderId traderId = _bus.TraderId;
        UnixNanos now = Clock.Timestamp;

        foreach (IGrouping<StrategyId, Order> byStrategy in _cache.OrdersOpen().GroupBy(o => o.StrategyId))
        {
            foreach (InstrumentId instrumentId in byStrategy.Select(o => o.InstrumentId).Distinct().ToList())
            {
                _bus.Send(Endpoints.RiskEngineExecute, new CancelAllOrders(traderId, byStrategy.Key, instrumentId, null, null, Guid.NewGuid(), now));
            }
        }

        foreach (IGrouping<StrategyId, Position> byStrategy in _cache.PositionsOpen().GroupBy(p => p.StrategyId))
        {
            // The same id seeding a host's own flatten uses: far enough past the strategy's own count that a closing
            // order cannot collide with one the strategy is about to write.
            OrderFactory factory = new(traderId, byStrategy.Key, Clock, _cache.Orders(strategyId: byStrategy.Key).Count + 1000);
            foreach (Position position in byStrategy.ToList())
            {
                MarketOrder order = factory.Market(position.InstrumentId, position.Side.ClosingSide(), position.Quantity, reduceOnly: true, tags: ["LOSS_LIMIT_FLATTEN"]);
                _cache.AddOrder(order, position.Id);
                LossLimitFlattenedCount++;
                Log.LogWarning("Closing {Position} on {InstrumentId}: {Side} {Quantity} reduce-only, because the loss limit was reached",
                    position.Id, position.InstrumentId, order.Side, order.Quantity);
                _bus.Send(Endpoints.RiskEngineExecute, new SubmitOrder(traderId, byStrategy.Key, order, position.Id, null, null, Guid.NewGuid(), now));
            }
        }
    }

    /// <summary>
    /// The account-wide limits: what the period may lose and what the account may carry. Both are measured from the
    /// portfolio, in the currency the figure is denominated in, and both let through anything that reduces - a limit
    /// that stopped someone closing a losing position would turn a bad day into a worse one. Returns the reason the
    /// order cannot be sent, or null.
    /// </summary>
    private string? CheckLimits(Instrument instrument, Order order, Pending? pending = null)
    {
        if (_limits.MaxLossPerPeriod is null && _limits.MaxExposure is null)
        {
            return null;
        }

        if (order.IsReduceOnly || WouldReduce(order))
        {
            return null;
        }

        Venue venue = order.InstrumentId.Venue;
        PeriodMark mark = MarkFor(venue);

        if (_limits.MaxLossPerPeriod is { } lossLimit)
        {
            Currency currency = instrument.SettlementCurrency;
            if (lossLimit.In(currency, mark.Equity(currency)) is { } allowed)
            {
                decimal lost = LostThisPeriod(venue, currency, mark);
                if (lost >= allowed.Amount)
                {
                    LossLimitDeniedCount++;
                    string reason = $"LOSS_LIMIT: {new Money(lost, currency)} lost this period, realised and open, reaches the limit of {allowed}";
                    StopTrading(reason);
                    return reason;
                }
            }
        }

        if (_limits.MaxExposure is { } exposureLimit)
        {
            Currency currency = instrument.CostCurrency;
            if (exposureLimit.In(currency, mark.Equity(currency)) is { } allowed)
            {
                decimal carried = Amount(_portfolio.NetExposures(venue), currency)
                    + (pending?.Exposure(currency) ?? 0m)
                    + (Notional(instrument, order) is { } notional && notional.Currency.Equals(currency) ? notional.Amount : 0m);
                if (carried > allowed.Amount)
                {
                    ExposureLimitDeniedCount++;
                    return $"EXPOSURE_LIMIT: {new Money(carried, currency)} carried with this order exceeds the limit of {allowed}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Which period a moment falls in. Periods are counted off the epoch rather than from when the engine happened to
    /// start, so a day is a UTC day for every engine that measures one.
    /// </summary>
    private long PeriodOf(UnixNanos ts) =>
        _limits.LossPeriod.Ticks <= 0 ? 0 : ts.Value / (_limits.LossPeriod.Ticks * UnixNanos.NanosPerTick);

    /// <summary>
    /// Where the venue stood when the current period opened. A period that rolled while the engine was running is
    /// marked at where the account stands now. A venue the engine meets for the first time inside the period it began
    /// its own life in was not trading when it began, so that period opens at nothing realised - otherwise a loss
    /// taken before the first order of the run would be marked as the opening balance and never counted.
    /// </summary>
    private PeriodMark MarkFor(Venue venue)
    {
        long period = PeriodOf(Clock.Timestamp);
        _startedPeriod ??= period;
        if (_marks.TryGetValue(venue, out PeriodMark? mark) && mark.Period == period)
        {
            return mark;
        }

        bool openedFlat = mark is null && period == _startedPeriod;
        mark = new PeriodMark(
            period,
            openedFlat ? EmptyMoney : _portfolio.RealizedPnls(venue),
            openedFlat ? EmptyMoney : _portfolio.UnrealizedPnls(venue),
            _cache.AccountForVenue(venue)?.Balances.ToDictionary(b => b.Key, b => b.Value.Total));
        _marks[venue] = mark;
        return mark;
    }

    private static readonly IReadOnlyDictionary<Currency, Money> EmptyMoney = new Dictionary<Currency, Money>();

    private static decimal Amount(IReadOnlyDictionary<Currency, Money> figures, Currency currency) =>
        figures.TryGetValue(currency, out Money money) ? money.Amount : 0m;

    /// <summary>
    /// Where a venue stood when the current loss period opened: what it had realised, what its open positions were
    /// worth, and what the account was worth. A period that opens where the engine's own life began opens at nothing
    /// on both counts, so a loss taken inside the run is counted whether it was closed or is still open.
    /// </summary>
    private sealed class PeriodMark
    {
        private readonly IReadOnlyDictionary<Currency, Money> _realized;
        private readonly IReadOnlyDictionary<Currency, Money> _unrealized;
        private readonly IReadOnlyDictionary<Currency, Money>? _equity;

        public PeriodMark(
            long period,
            IReadOnlyDictionary<Currency, Money> realized,
            IReadOnlyDictionary<Currency, Money> unrealized,
            IReadOnlyDictionary<Currency, Money>? equity)
        {
            Period = period;
            _realized = realized;
            _unrealized = unrealized;
            _equity = equity;
        }

        public long Period { get; }

        public decimal Realized(Currency currency) => _realized.TryGetValue(currency, out Money money) ? money.Amount : 0m;

        public decimal Unrealized(Currency currency) => _unrealized.TryGetValue(currency, out Money money) ? money.Amount : 0m;

        public Money? Equity(Currency currency) => _equity is not null && _equity.TryGetValue(currency, out Money money) ? money : null;
    }

    private bool WouldReduce(Order order)
    {
        decimal net = _portfolio.NetPosition(order.InstrumentId);
        if (net == 0m)
        {
            return false;
        }

        bool opposite = (net > 0m && order.IsSell) || (net < 0m && order.IsBuy);
        return opposite && order.Quantity.Value <= Math.Abs(net);
    }

    private bool CheckRate(Queue<UnixNanos> times, int max)
    {
        UnixNanos now = Clock.Timestamp;
        UnixNanos cutoff = now - _config.OrderRateInterval;
        while (times.Count > 0 && times.Peek() < cutoff)
        {
            times.Dequeue();
        }

        if (times.Count >= max)
        {
            return false;
        }

        times.Enqueue(now);
        return true;
    }

    private void Deny(Order order, string reason)
    {
        DeniedCount++;
        if (_config.LogDenials)
        {
            Log.LogWarning("Denied {ClientOrderId}: {Reason}", order.ClientOrderId, reason);
        }

        OrderDenied denied = new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, reason, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp);
        _bus.Send(Endpoints.ExecutionEngineProcess, denied);
    }

    private void RejectModify(Order order, string reason)
    {
        DeniedCount++;
        if (_config.LogDenials)
        {
            Log.LogWarning("Modify rejected for {ClientOrderId}: {Reason}", order.ClientOrderId, reason);
        }

        OrderModifyRejected rejected = new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, order.AccountId, reason, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp);
        _bus.Send(Endpoints.ExecutionEngineProcess, rejected);
    }

    private void Forward(TradingCommand command) => _bus.Send(Endpoints.ExecutionEngineExecute, command);
}
