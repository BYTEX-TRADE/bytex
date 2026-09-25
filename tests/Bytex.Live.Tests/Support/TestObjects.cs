using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Any spot pair against USDT on the given venue, for a test that needs more than one instrument. The base
    /// currency is whatever the symbol says before USDT.
    /// </summary>
    public static CurrencyPair Spot(string venue, string symbol) => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol(symbol), new Venue(venue)),
        RawSymbol = new Symbol(symbol),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currency.FromCode(symbol.Replace("USDT", string.Empty, StringComparison.Ordinal)),
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

    public void Modify(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null) => ModifyOrder(order, quantity, price, triggerPrice);

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

    public List<DataResponse> DataResponses { get; } = new();

    public TaskCompletionSource<DataResponse> FirstDataResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid AskForBars(BarType barType) => RequestBars(barType);

    public bool HasPending(Guid requestId) => IsPendingRequest(requestId);

    protected override void OnDataResponse(DataResponse response)
    {
        DataResponses.Add(response);
        FirstDataResponse.TrySetResult(response);
    }
}

/// <summary>Counts the quotes it saw and keeps the count over a restart, so a test can see the state come back.</summary>
internal sealed class CountingStrategy : Strategy
{
    public CountingStrategy(InstrumentId instrumentId)
        : base(new StrategyConfig { StrategyId = new StrategyId("Counter-001") })
    {
        InstrumentId = instrumentId;
    }

    public InstrumentId InstrumentId { get; }

    public int Seen { get; private set; }

    public TaskCompletionSource<int> FirstQuote { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OrderFactory Orders => OrderFactory;

    public void Submit(Order order) => SubmitOrder(order);

    protected override void OnStart()
    {
        Log.LogInformation("Counting strategy started with {Seen} quotes behind it", Seen);
        SubscribeQuoteTicks(InstrumentId);
    }

    protected override void OnQuoteTick(QuoteTick tick)
    {
        Seen++;
        FirstQuote.TrySetResult(Seen);
    }

    protected override IDictionary<string, byte[]> OnSave() =>
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["seen"] = BitConverter.GetBytes(Seen) };

    protected override void OnLoad(IDictionary<string, byte[]> state)
    {
        if (state.TryGetValue("seen", out byte[]? bytes) && bytes.Length == sizeof(int))
        {
            Seen = BitConverter.ToInt32(bytes);
        }
    }
}

/// <summary>Sets an alert for a time that has already passed while it is starting: the alert must still arrive.</summary>
internal sealed class AlertOnStartStrategy : Strategy
{
    public AlertOnStartStrategy()
        : base(new StrategyConfig { StrategyId = new StrategyId("Alert-001") })
    {
    }

    public TaskCompletionSource<TimeEvent> Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void OnStart() => SetTimeAlert("due-at-start", Clock.Timestamp.AddNanos(-UnixNanos.NanosPerSecond));

    protected override void OnTimeEvent(TimeEvent e) => Fired.TrySetResult(e);
}
