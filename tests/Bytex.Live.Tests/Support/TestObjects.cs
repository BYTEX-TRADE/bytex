using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;

namespace Bytex.Live.Tests.Support;

internal static class TestInstruments
{
    /// <summary>BTCUSDT spot on the given venue: tick 0.01, step 0.00001, no fees so balances are easy to reason about.</summary>
    public static CurrencyPair BtcUsdt(string venue = "BINANCE") => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT"), new Venue(venue)),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 5,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.00001m, 5),
        MakerFee = 0m,
        TakerFee = 0m,
    });

    public static QuoteTick Quote(Instrument instrument, decimal bid, decimal ask, UnixNanos ts) =>
        new(instrument.Id, instrument.MakePrice(bid), instrument.MakePrice(ask), instrument.MakeQuantity(10m), instrument.MakeQuantity(10m), ts, ts);
}

/// <summary>
/// A strategy that does nothing on its own and lets the test drive order flow and observe callbacks.
/// </summary>
internal sealed class ProbeStrategy : Strategy
{
    private readonly Journal _journal;

    public ProbeStrategy(string id = "Probe-001", Journal? journal = null, IReadOnlyList<InstrumentId>? claims = null)
        : base(new StrategyConfig { StrategyId = new StrategyId(id), ExternalOrderClaims = claims ?? [] })
    {
        _journal = journal ?? new Journal();
    }

    public List<OrderEvent> OrderEvents { get; } = new();

    public List<(QuoteTick Tick, int ThreadId)> Quotes { get; } = new();

    public TaskCompletionSource<QuoteTick> FirstQuote { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InstrumentId? SubscribeTo { get; init; }

    public OrderFactory Orders => OrderFactory;

    public void Submit(Order order) => SubmitOrder(order);

    public void Close(Position position) => ClosePosition(position);

    protected override void OnStart()
    {
        _journal.Add("strategy.start");

        if (SubscribeTo is { } id)
        {
            SubscribeQuoteTicks(id);
        }
    }

    protected override void OnStop() => _journal.Add("strategy.stop");

    protected override void OnQuoteTick(QuoteTick tick)
    {
        Quotes.Add((tick, Environment.CurrentManagedThreadId));
        FirstQuote.TrySetResult(tick);
    }

    protected override void OnOrderEvent(OrderEvent e) => OrderEvents.Add(e);
}
