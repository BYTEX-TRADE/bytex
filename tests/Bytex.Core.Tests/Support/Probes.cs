using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Support;

/// <summary>
/// Indicator that only counts its updates and writes to a shared log, so the order of "indicator then handler" is observable.
/// </summary>
internal sealed class CountingIndicator : IIndicator
{
    private readonly List<string>? _log;
    private readonly int _initializedAfter;

    public CountingIndicator(string name = "COUNT", List<string>? log = null, int initializedAfter = 1)
    {
        Name = name;
        _log = log;
        _initializedAfter = initializedAfter;
    }

    public string Name { get; }

    public int Count { get; private set; }

    public bool HasInputs => Count > 0;

    public bool IsInitialized => Count >= _initializedAfter;

    public void Update(Bar bar) => Record("bar");

    public void Update(QuoteTick tick) => Record("quote");

    public void Update(TradeTick tick) => Record("trade");

    public void Reset() => Count = 0;

    private void Record(string kind)
    {
        Count++;
        _log?.Add($"indicator:{Name}:{kind}");
    }
}

/// <summary>
/// Actor that logs every hook and exposes the protected actor API to tests.
/// </summary>
internal class ProbeActor : Actor
{
    public ProbeActor(ActorConfig? config = null)
        : base(config)
    {
    }

    public List<string> Calls { get; } = new();

    public List<object> Received { get; } = new();

    /// <summary>Name of the hook that should throw, to test fault isolation.</summary>
    public string? ThrowIn { get; set; }

    public Action? StartAction { get; set; }

    public IDictionary<string, byte[]> StateToSave { get; set; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public IDictionary<string, byte[]>? LoadedState { get; private set; }

    public bool AllIndicatorsInitialized => IndicatorsInitialized;

    public int IndicatorCount => RegisteredIndicators.Count;

    public bool AnyPendingRequests => HasPendingRequests;

    private void Hook(string name, object? payload = null)
    {
        Calls.Add(name);
        if (payload is not null)
        {
            Received.Add(payload);
        }

        if (ThrowIn == name)
        {
            throw new InvalidOperationException(name + " failed");
        }
    }

    protected override void OnRegistered() => Hook("OnRegistered");

    protected override void OnStart()
    {
        Hook("OnStart");
        StartAction?.Invoke();
    }

    protected override void OnStop() => Hook("OnStop");

    protected override void OnResume() => Hook("OnResume");

    protected override void OnReset() => Hook("OnReset");

    protected override void OnDegrade() => Hook("OnDegrade");

    protected override void OnFault() => Hook("OnFault");

    protected override void OnDispose()
    {
        Hook("OnDispose");
        base.OnDispose();
    }

    protected override IDictionary<string, byte[]> OnSave()
    {
        Hook("OnSave");
        return StateToSave;
    }

    protected override void OnLoad(IDictionary<string, byte[]> state)
    {
        Hook("OnLoad");
        LoadedState = state;
    }

    protected override void OnInstrument(Instrument instrument) => Hook("OnInstrument", instrument);

    protected override void OnQuoteTick(QuoteTick tick) => Hook("OnQuoteTick", tick);

    protected override void OnTradeTick(TradeTick tick) => Hook("OnTradeTick", tick);

    protected override void OnBar(Bar bar) => Hook("OnBar", bar);

    protected override void OnOrderBookDeltas(OrderBookDeltas deltas) => Hook("OnOrderBookDeltas", deltas);

    protected override void OnOrderBook(OrderBook book) => Hook("OnOrderBook", book);

    protected override void OnInstrumentStatus(InstrumentStatus status) => Hook("OnInstrumentStatus", status);

    protected override void OnMarkPrice(MarkPriceUpdate update) => Hook("OnMarkPrice", update);

    protected override void OnIndexPrice(IndexPriceUpdate update) => Hook("OnIndexPrice", update);

    protected override void OnFundingRate(FundingRateUpdate update) => Hook("OnFundingRate", update);

    protected override void OnData(IData data) => Hook("OnData", data);

    protected override void OnSignal(Signal signal) => Hook("OnSignal", signal);

    protected override void OnHistoricalData(IData data) => Hook("OnHistoricalData", data);

    protected override void OnDataResponse(DataResponse response) => Hook("OnDataResponse", response);

    protected override void OnTimeEvent(TimeEvent e) => Hook("OnTimeEvent", e);

    protected override void OnEvent(Event e) => Hook("OnEvent");

    public void DoSubscribeInstruments(Venue venue) => SubscribeInstruments(venue);

    public void DoSubscribeInstrument(InstrumentId id) => SubscribeInstrument(id);

    public void DoSubscribeQuotes(InstrumentId id) => SubscribeQuoteTicks(id);

    public void DoUnsubscribeQuotes(InstrumentId id) => UnsubscribeQuoteTicks(id);

    public void DoSubscribeTrades(InstrumentId id) => SubscribeTradeTicks(id);

    public void DoSubscribeBars(BarType barType) => SubscribeBars(barType);

    public void DoSubscribeBookDeltas(InstrumentId id) => SubscribeOrderBookDeltas(id);

    public void DoSubscribeBook(InstrumentId id) => SubscribeOrderBook(id);

    public void DoSubscribeStatus(InstrumentId id) => SubscribeInstrumentStatus(id);

    public void DoSubscribeMarkPrices(InstrumentId id) => SubscribeMarkPrices(id);

    public void DoSubscribeIndexPrices(InstrumentId id) => SubscribeIndexPrices(id);

    public void DoSubscribeFundingRates(InstrumentId id) => SubscribeFundingRates(id);

    public void DoSubscribeData<T>(IReadOnlyDictionary<string, string>? metadata = null) where T : IData => SubscribeData<T>(metadata);

    public void DoSubscribeSignal(string name) => SubscribeSignal(name);

    public void DoUnsubscribeSignal(string name) => UnsubscribeSignal(name);

    public void DoPublishSignal(string name, decimal value) => PublishSignal(name, value);

    public void DoPublishData<T>(T data, IReadOnlyDictionary<string, string>? metadata = null) where T : IData => PublishData(data, metadata);

    public Guid DoRequestBars(BarType barType, int? limit = null) => RequestBars(barType, limit: limit);

    public bool IsPending(Guid requestId) => IsPendingRequest(requestId);

    public void DoRegisterForBars(BarType barType, IIndicator indicator) => RegisterIndicatorForBars(barType, indicator);

    public void DoRegisterForQuotes(InstrumentId id, IIndicator indicator) => RegisterIndicatorForQuoteTicks(id, indicator);

    public void DoRegisterForTrades(InstrumentId id, IIndicator indicator) => RegisterIndicatorForTradeTicks(id, indicator);

    public void DoSetTimer(string name, TimeSpan interval, Action<TimeEvent>? callback = null) => SetTimer(name, interval, callback: callback);

    public void DoSetTimeAlert(string name, UnixNanos at, Action<TimeEvent>? callback = null) => SetTimeAlert(name, at, callback);

    public void DoCancelTimer(string name) => CancelTimer(name);

    public void DoRunInBackground(Func<CancellationToken, Task> work, Action<Exception>? onError = null) => RunInBackground(work, onError);

    public void DoPost(Action action) => Post(action);

    public decimal NetPositionFromPortfolio(InstrumentId id) => Portfolio.NetPosition(id);

    public int CachedInstrumentCount => Cache.Instruments().Count;
}

/// <summary>
/// Strategy that logs every order and position hook and exposes the protected trading API to tests.
/// </summary>
internal class ProbeStrategy : Strategy
{
    public ProbeStrategy(StrategyConfig? config = null)
        : base(config)
    {
    }

    public List<string> Calls { get; } = new();

    public string? ThrowIn { get; set; }

    public OrderFactory Factory => OrderFactory;

    private void Hook(string name)
    {
        Calls.Add(name);
        if (ThrowIn == name)
        {
            throw new InvalidOperationException(name + " failed");
        }
    }

    protected override void OnStart() => Hook("OnStart");

    protected override void OnStop() => Hook("OnStop");

    protected override void OnOrderInitialized(OrderInitialized e) => Hook("OnOrderInitialized");

    protected override void OnOrderDenied(OrderDenied e) => Hook("OnOrderDenied");

    protected override void OnOrderEmulated(OrderEmulated e) => Hook("OnOrderEmulated");

    protected override void OnOrderReleased(OrderReleased e) => Hook("OnOrderReleased");

    protected override void OnOrderSubmitted(OrderSubmitted e) => Hook("OnOrderSubmitted");

    protected override void OnOrderRejected(OrderRejected e) => Hook("OnOrderRejected");

    protected override void OnOrderAccepted(OrderAccepted e) => Hook("OnOrderAccepted");

    protected override void OnOrderCanceled(OrderCanceled e) => Hook("OnOrderCanceled");

    protected override void OnOrderExpired(OrderExpired e) => Hook("OnOrderExpired");

    protected override void OnOrderTriggered(OrderTriggered e) => Hook("OnOrderTriggered");

    protected override void OnOrderPendingUpdate(OrderPendingUpdate e) => Hook("OnOrderPendingUpdate");

    protected override void OnOrderPendingCancel(OrderPendingCancel e) => Hook("OnOrderPendingCancel");

    protected override void OnOrderModifyRejected(OrderModifyRejected e) => Hook("OnOrderModifyRejected");

    protected override void OnOrderCancelRejected(OrderCancelRejected e) => Hook("OnOrderCancelRejected");

    protected override void OnOrderUpdated(OrderUpdated e) => Hook("OnOrderUpdated");

    protected override void OnOrderFilled(OrderFilled e) => Hook("OnOrderFilled");

    protected override void OnOrderEvent(OrderEvent e) => Hook("OnOrderEvent");

    protected override void OnPositionOpened(PositionOpened e) => Hook("OnPositionOpened");

    protected override void OnPositionChanged(PositionChanged e) => Hook("OnPositionChanged");

    protected override void OnPositionClosed(PositionClosed e) => Hook("OnPositionClosed");

    protected override void OnPositionEvent(PositionEvent e) => Hook("OnPositionEvent");

    protected override void OnEvent(Event e) => Hook("OnEvent");

    public void DoSubmit(Order order, PositionId? positionId = null, ClientId? clientId = null) => SubmitOrder(order, positionId, clientId);

    public void DoSubmitList(OrderList list) => SubmitOrderList(list);

    public void DoModify(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null) => ModifyOrder(order, quantity, price, triggerPrice);

    public void DoCancel(Order order) => CancelOrder(order);

    public void DoCancelOrders(IReadOnlyList<Order> orders) => CancelOrders(orders);

    public void DoCancelAll(InstrumentId instrumentId, OrderSide? side = null) => CancelAllOrders(instrumentId, side);

    public void DoCancelAllInstruments() => CancelAllOrdersAllInstruments();

    public void DoClosePosition(Position position) => ClosePosition(position);

    public void DoCloseAllPositions(InstrumentId instrumentId, PositionSide? side = null) => CloseAllPositions(instrumentId, side);

    public void DoCloseAllPositionsAllInstruments() => CloseAllPositionsAllInstruments();

    public void DoQuery(Order order) => QueryOrder(order);

    public void DoCancelGtdExpiry(Order order) => CancelGtdExpiry(order);
}

/// <summary>Configuration record used to test typed strategies and the importable plugin provider.</summary>
internal sealed record TypedProbeConfig : StrategyConfig
{
    public int FastPeriod { get; init; } = 10;

    public InstrumentId? Instrument { get; init; }
}

internal sealed class TypedProbeStrategy : Strategy<TypedProbeConfig>
{
    public TypedProbeStrategy(TypedProbeConfig config)
        : base(config)
    {
    }
}
