using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: a strategy on bars alone leaves everything that watches the node with the last candle close, and between bars
// a frozen price reads as a stalled node. So a node can be told to subscribe quotes for display, and what is pinned
// here is that they are for display only: they reach the cache and whoever asks the node, they reach no strategy that
// did not ask for them, they are given back when the node stops, and a node holding a venue's whole catalogue is not
// quietly turned into a hundred subscriptions.
public sealed class DisplayPriceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    /// <summary>A strategy that asks for bars and nothing else, and records every quote it is ever handed.</summary>
    private sealed class BarsOnlyStrategy : Strategy
    {
        private readonly BarType _barType;

        public BarsOnlyStrategy(BarType barType)
            : base(new StrategyConfig { StrategyId = new StrategyId("Bars-001") }) => _barType = barType;

        public List<QuoteTick> Quotes { get; } = new();

        protected override void OnStart() => SubscribeBars(_barType);

        protected override void OnQuoteTick(QuoteTick tick) => Quotes.Add(tick);
    }

    private sealed record Rig(TradingNode Node, FakeDataClient Data, CurrencyPair Instrument, BarsOnlyStrategy Strategy) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(bool displayPrices = true, IReadOnlyList<InstrumentId>? named = null, IReadOnlyList<Instrument>? extra = null, Microsoft.Extensions.Logging.ILoggerFactory? logging = null)
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        registry.AddDataClientFactory(dataFactory);
        CurrencyPair instrument = TestInstruments.BtcUsdt("FAKE");
        BarType barType = new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

        TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
            DisplayPrices = displayPrices,
            DisplayPriceInstruments = named ?? [],
        }, registry, logging);
        node.AddInstrument(instrument);
        foreach (Instrument other in extra ?? [])
        {
            node.AddInstrument(other);
        }

        BarsOnlyStrategy strategy = new(barType);
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);
        return new Rig(node, dataFactory.Created.Single().Client, instrument, strategy);
    }

    private static IReadOnlyList<SubscribeQuoteTicks> QuoteSubscriptions(Rig rig) =>
        rig.Data.Subscriptions.OfType<SubscribeQuoteTicks>().ToList();

    private static QuoteTick Quote(Instrument instrument, decimal bid, decimal ask, long seconds = 1) =>
        new(instrument.Id, instrument.MakePrice(bid), instrument.MakePrice(ask), instrument.MakeQuantity(1m), instrument.MakeQuantity(1m),
            UnixNanos.FromSeconds(seconds), UnixNanos.FromSeconds(seconds));

    [Fact]
    public async Task A_node_asked_to_show_prices_subscribes_quotes_for_the_instruments_it_was_given()
    {
        await using Rig rig = await StartAsync();

        Assert.Equal([rig.Instrument.Id], QuoteSubscriptions(rig).Select(s => s.InstrumentId));
        Assert.Equal([rig.Instrument.Id], rig.Node.DisplayPriceInstruments);
    }

    [Fact]
    public async Task A_node_not_asked_to_show_prices_subscribes_nothing_of_its_own()
    {
        await using Rig rig = await StartAsync(displayPrices: false);

        Assert.Empty(QuoteSubscriptions(rig));
        Assert.Empty(rig.Node.DisplayPriceInstruments);
    }

    [Fact]
    public async Task A_node_told_which_instruments_to_show_subscribes_those()
    {
        CurrencyPair other = TestInstruments.Spot("FAKE", "ETHUSDT");

        await using Rig rig = await StartAsync(named: [other.Id], extra: [other]);

        Assert.Equal([other.Id], QuoteSubscriptions(rig).Select(s => s.InstrumentId));
        Assert.Equal([other.Id], rig.Node.DisplayPriceInstruments);
    }

    [Fact]
    public async Task A_display_quote_reaches_the_cache_and_no_strategy_that_did_not_ask_for_it()
    {
        await using Rig rig = await StartAsync();

        rig.Data.Emit(Quote(rig.Instrument, 49_999m, 50_001m));
        await rig.Node.Loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        QuoteTick? cached = await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.QuoteTick(rig.Instrument.Id)).WaitAsync(_timeout);
        Assert.NotNull(cached);
        Assert.Equal(rig.Instrument.MakePrice(49_999m), cached.Value.Bid);

        // The price a monitor draws, from the cache the node keeps.
        Price? mid = await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.Price(rig.Instrument.Id, PriceType.Mid)).WaitAsync(_timeout);
        Assert.Equal(rig.Instrument.MakePrice(50_000m), mid);

        // And the strategy, which asked for bars, was handed nothing it did not ask for.
        Assert.Empty(rig.Strategy.Quotes);
    }

    [Fact]
    public async Task A_strategy_that_asked_for_quotes_itself_still_gets_them()
    {
        // Display prices are not a filter: what a strategy subscribes to reaches it as it always did.
        await using Rig rig = await StartAsync();
        await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.MessageBus.Subscribe(Topics.Quotes(rig.Instrument.Id), m => rig.Strategy.Quotes.Add((QuoteTick)m))).WaitAsync(_timeout);

        rig.Data.Emit(Quote(rig.Instrument, 49_998m, 50_002m, seconds: 2));
        await rig.Node.Loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Single(rig.Strategy.Quotes);
    }

    [Fact]
    public async Task The_display_subscription_is_given_back_when_the_node_stops()
    {
        Rig rig = await StartAsync();
        await using (rig)
        {
            Assert.NotEmpty(QuoteSubscriptions(rig));

            await rig.Node.StopAsync().WaitAsync(_timeout);

            Assert.Equal([rig.Instrument.Id], rig.Data.Unsubscriptions.OfType<UnsubscribeQuoteTicks>().Select(u => u.InstrumentId));
            Assert.Empty(rig.Node.DisplayPriceInstruments);
        }
    }

    [Fact]
    public async Task A_node_holding_more_instruments_than_it_will_show_asks_to_be_told_which()
    {
        // A node that loaded a venue's catalogue must not open a quote stream per instrument because someone wanted a
        // moving price; it says what to do instead and subscribes nothing.
        List<Instrument> many = Enumerable.Range(0, TradingNode.MaxDisplayPriceInstruments)
            .Select(i => (Instrument)TestInstruments.Spot("FAKE", $"ALT{i}USDT"))
            .ToList();
        CapturingLoggerFactory logging = new();

        await using Rig rig = await StartAsync(extra: many, logging: logging);

        Assert.Empty(QuoteSubscriptions(rig));
        Assert.Empty(rig.Node.DisplayPriceInstruments);
        string said = await logging.WaitForAsync("Display prices:");
        Assert.Contains("DisplayPriceInstruments", said, StringComparison.Ordinal);
        Assert.Contains(TradingNode.MaxDisplayPriceInstruments.ToString(System.Globalization.CultureInfo.InvariantCulture), said, StringComparison.Ordinal);
    }
}
