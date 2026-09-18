using System.Globalization;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;

namespace Bytex.Backtest.Tests.Support;

public sealed record ScriptedStrategyConfig : StrategyConfig
{
    public IReadOnlyList<InstrumentId> QuoteSubscriptions { get; init; } = [];

    public IReadOnlyList<InstrumentId> TradeSubscriptions { get; init; } = [];

    public IReadOnlyList<BarType> BarSubscriptions { get; init; } = [];

    public IReadOnlyList<InstrumentId> BookSubscriptions { get; init; } = [];

    /// <summary>Flatten and cancel everything when the strategy stops (the pattern the docs recommend).</summary>
    public bool FlattenOnStop { get; init; }
}

/// <summary>
/// A strategy driven by a script: actions run at fixed simulated times (through clock alerts) or when data arrives.
/// Everything the strategy observes is appended to <see cref="Journal"/> with the simulated clock time, so tests can
/// assert on ordering as well as on content.
/// </summary>
public sealed class ScriptedStrategy : Strategy<ScriptedStrategyConfig>
{
    private readonly List<(UnixNanos Time, Action<ScriptedStrategy> Action)> _script = new();
    private readonly List<string> _journal = new();

    public ScriptedStrategy(ScriptedStrategyConfig config)
        : base(config)
    {
    }

    /// <summary>Lines of the form "milliseconds-since-epoch-of-timeline|what happened".</summary>
    public IReadOnlyList<string> Journal => _journal;

    public Action<ScriptedStrategy, QuoteTick>? QuoteHandler { get; set; }

    public Action<ScriptedStrategy, Bar>? BarHandler { get; set; }

    public Action<ScriptedStrategy>? StopHandler { get; set; }

    public OrderFactory Orders => OrderFactory;

    public ICache Store => Cache;

    public UnixNanos Now => Clock.Timestamp;

    public void At(UnixNanos time, Action<ScriptedStrategy> action) => _script.Add((time, action));

    public T Submit<T>(T order, PositionId? positionId = null)
        where T : Order
    {
        SubmitOrder(order, positionId);
        return order;
    }

    public OrderList SubmitList(OrderList list)
    {
        SubmitOrderList(list);
        return list;
    }

    public void Modify(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null) => ModifyOrder(order, quantity, price, triggerPrice);

    public void Cancel(Order order) => CancelOrder(order);

    public void CancelBatch(IReadOnlyList<Order> orders) => CancelOrders(orders);

    public void CancelAll(InstrumentId instrumentId, OrderSide? side = null) => CancelAllOrders(instrumentId, side);

    public void Close(Position position) => ClosePosition(position);

    public void CloseAll(InstrumentId instrumentId) => CloseAllPositions(instrumentId);

    public void Timer(string name, TimeSpan interval, UnixNanos? stop = null) => SetTimer(name, interval, stop: stop);

    public void Note(string text) => _journal.Add(Stamp(text));

    protected override void OnStart()
    {
        foreach (InstrumentId id in Config.QuoteSubscriptions)
        {
            SubscribeQuoteTicks(id);
        }

        foreach (InstrumentId id in Config.TradeSubscriptions)
        {
            SubscribeTradeTicks(id);
        }

        foreach (BarType barType in Config.BarSubscriptions)
        {
            SubscribeBars(barType);
        }

        foreach (InstrumentId id in Config.BookSubscriptions)
        {
            SubscribeOrderBookDeltas(id);
        }

        for (int i = 0; i < _script.Count; i++)
        {
            Action<ScriptedStrategy> action = _script[i].Action;
            SetTimeAlert("script-" + i.ToString(CultureInfo.InvariantCulture), _script[i].Time, _ => action(this));
        }
    }

    protected override void OnStop()
    {
        StopHandler?.Invoke(this);
        if (Config.FlattenOnStop)
        {
            foreach (InstrumentId id in Cache.InstrumentIds())
            {
                CancelAllOrders(id);
                CloseAllPositions(id);
            }
        }
    }

    protected override void OnReset() => _journal.Clear();

    protected override void OnQuoteTick(QuoteTick tick)
    {
        Note($"quote {tick.InstrumentId.Symbol} {tick.Bid}/{tick.Ask}");
        QuoteHandler?.Invoke(this, tick);
    }

    protected override void OnTradeTick(TradeTick tick) => Note($"trade {tick.InstrumentId.Symbol} {tick.Price}");

    protected override void OnBar(Bar bar)
    {
        Note($"bar {bar.BarType.InstrumentId.Symbol} {bar.Close}");
        BarHandler?.Invoke(this, bar);
    }

    protected override void OnOrderBookDeltas(OrderBookDeltas deltas) => Note($"book {deltas.InstrumentId.Symbol} {deltas.Deltas.Count} deltas");

    protected override void OnTimeEvent(TimeEvent e) => Note($"timer {e.Name[(e.Name.IndexOf(':', StringComparison.Ordinal) + 1)..]} due {Offset(e.TsEvent)}");

    protected override void OnOrderEvent(OrderEvent e) => Note(e.GetType().Name);

    private string Stamp(string text) => Offset(Clock.Timestamp) + "|" + text;

    private static string Offset(UnixNanos ts) => ((ts.Value - Scripted.Epoch.Value) / UnixNanos.NanosPerMillisecond).ToString(CultureInfo.InvariantCulture);
}
