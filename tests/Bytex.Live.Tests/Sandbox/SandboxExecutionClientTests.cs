using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Timing;
using Bytex.Live.Sandbox;
using Bytex.Core.Adapters;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests.Sandbox;

// Why: sandbox mode is what users run before risking money. Orders must fill against the market data that
// flows through the kernel, at the prices that data implies, and balances must move accordingly - and the venue must
// be able to say which of that data a fill came out of, because a fill measured against a book, a quote and a bar
// are three different claims.
public sealed class SandboxExecutionClientTests
{
    [Fact]
    public async Task A_paper_venue_asks_for_the_book_of_an_instrument_it_already_had()
    {
        // The defect: the book was asked for only when an instrument ARRIVED, so an instrument already in the cache
        // when the venue connected - which is every instrument of a node that loads them before it connects - was
        // never asked about. Depth never came, and paper matched against bars while the release claimed otherwise.
        using Harness h = new();
        await h.ConnectAsync();

        MatchingAgainst row = Assert.Single(h.Client.Matching());
        Assert.True(row.WantsBook, "the paper venue is configured to match against a book by default");
        Assert.True(row.BookRequested, "the venue never asked its data client for the book of an instrument it held");
    }

    [Fact]
    public async Task A_paper_venue_told_not_to_use_the_book_asks_for_nothing()
    {
        using Harness h = new(new SandboxExecutionClientConfig
        {
            Venue = "BINANCE",
            StartingBalances = ["100000 USDT"],
            MatchAgainstBook = false,
        });
        await h.ConnectAsync();

        MatchingAgainst row = Assert.Single(h.Client.Matching());
        Assert.False(row.WantsBook);
        Assert.False(row.BookRequested, "a venue that does not match against the book should not be asking for depth");
    }

    [Fact]
    public async Task A_paper_venue_says_what_it_is_matching_against_and_changes_its_answer_when_the_book_arrives()
    {
        using Harness h = new();
        await h.ConnectAsync();

        // Nothing has arrived: the venue holds the instrument and would match nothing.
        MatchingAgainst nothing = Assert.Single(h.Client.Matching());
        Assert.Equal(h.Instrument.Id, nothing.InstrumentId);
        Assert.Equal(MatchingAgainst.Nothing, nothing.Against);
        Assert.True(nothing.WantsBook, "the paper venue is configured to match against a book by default");

        // A quote, and no book yet: it would match at the touch, whatever it was configured to want.
        h.Quote(29_999.00m, 30_000.00m, 1);
        Assert.Equal(MatchingAgainst.Quotes, Assert.Single(h.Client.Matching()).Against);

        // The book arrives and the answer changes with it.
        h.Book(bid: 29_999.00m, bidSize: 3m, ask: 30_000.00m, askSize: 3m, secondsAfterStart: 2);
        Assert.Equal(MatchingAgainst.Book, Assert.Single(h.Client.Matching()).Against);
    }

    [Fact]
    public async Task A_paper_venue_told_not_to_match_against_the_book_says_quotes_even_with_a_book_in_the_cache()
    {
        // The dangerous case to get wrong: depth is flowing, the cache holds it, and this venue is not using it.
        using Harness h = new(new SandboxExecutionClientConfig
        {
            Venue = "BINANCE",
            StartingBalances = ["100000 USDT", "1 BTC"],
            MatchAgainstBook = false,
        });
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);
        h.Book(bid: 29_999.00m, bidSize: 3m, ask: 30_000.00m, askSize: 3m, secondsAfterStart: 2);

        MatchingAgainst row = Assert.Single(h.Client.Matching());
        Assert.Equal(MatchingAgainst.Quotes, row.Against);
        Assert.False(row.WantsBook, "this venue was told not to match against a book");
    }

    private static readonly UnixNanos _t0 = UnixNanos.FromSeconds(1_700_000_000);

    private sealed class Harness : IDisposable
    {
        public Harness(SandboxExecutionClientConfig? config = null)
        {
            Clock = new TestClock(_t0);
            Kernel = new Kernel(new KernelConfig { Environment = TradingEnvironment.Sandbox }, Clock);
            Instrument = TestInstruments.BtcUsdt();
            Kernel.Cache.AddInstrument(Instrument);
            Client = new SandboxExecutionClient(new ClientId("BINANCE-SANDBOX"), config ?? new SandboxExecutionClientConfig { Venue = "BINANCE", StartingBalances = ["100000 USDT", "1 BTC"] }, Kernel.Services);
            Kernel.ExecutionEngine.RegisterClient(Client);
            Strategy = new ProbeStrategy();
            Kernel.Trader.AddStrategy(Strategy);
            Kernel.Start();
        }

        public TestClock Clock { get; }

        public Kernel Kernel { get; }

        public CurrencyPair Instrument { get; }

        public SandboxExecutionClient Client { get; }

        public ProbeStrategy Strategy { get; }

        public Task ConnectAsync() => Client.ConnectAsync(CancellationToken.None);

        public void Quote(decimal bid, decimal ask, int secondsAfterStart)
        {
            UnixNanos ts = _t0.Add(TimeSpan.FromSeconds(secondsAfterStart));
            Clock.SetTime(ts);
            Kernel.DataEngine.OnData(TestInstruments.Quote(Instrument, bid, ask, ts));
        }

        /// <summary>
        /// One level a side, the way a venue's depth stream builds a book: the deltas go through the data engine, so
        /// the cache maintains the book and the venue matches against what the cache holds - the same route a
        /// backtest takes.
        /// </summary>
        public void Book(decimal bid, decimal bidSize, decimal ask, decimal askSize, int secondsAfterStart)
        {
            UnixNanos ts = _t0.Add(TimeSpan.FromSeconds(secondsAfterStart));
            Clock.SetTime(ts);
            ulong sequence = (ulong)secondsAfterStart + 1UL;
            OrderBookDelta Level(OrderSide side, decimal price, decimal size, ulong id) =>
                new(Instrument.Id, BookAction.Update, new BookOrder(side, Instrument.MakePrice(price), Instrument.MakeQuantity(size), id), RecordFlags.None, sequence, ts, ts);

            Kernel.DataEngine.OnData(new OrderBookDeltas(
                Instrument.Id,
                [Level(OrderSide.Buy, bid, bidSize, 1), Level(OrderSide.Sell, ask, askSize, 2)],
                RecordFlags.None,
                sequence,
                ts,
                ts));
        }


        public decimal Total(Currency currency)
        {
            Account account = Kernel.Cache.Account(Client.AccountId) ?? throw new InvalidOperationException("sandbox account missing from the cache");
            return account.LastEvent.Balances.Single(b => b.Currency.Equals(currency)).Total.Amount;
        }

        public void Dispose() => Kernel.Dispose();
    }

    // ----- The book (0.6 Depth) -----

    [Fact]
    public async Task A_paper_venue_matches_against_the_book_the_venue_is_streaming()
    {
        // The point of paper trading: it is the step before somebody risks money, and the question it exists to
        // answer - would this have filled, and at what - is the one a book answers. Until now the book went past the
        // venue untouched while it matched against quotes and bars.
        using Harness h = new();
        await h.ConnectAsync();
        h.Book(bid: 29_999.00m, bidSize: 3m, ask: 30_000.00m, askSize: 3m, secondsAfterStart: 1);

        MarketOrder order = h.Strategy.Orders.Market(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(0.5m));
        h.Strategy.Submit(order);

        OrderFilled fill = Assert.Single(h.Strategy.OrderEvents.OfType<OrderFilled>());
        Assert.Equal(30_000.00m, fill.LastPx.Value);
        Assert.Equal(0.5m, fill.LastQty.Value);
    }

    [Fact]
    public async Task What_the_book_has_bounds_a_paper_fill()
    {
        // The whole reason to match against a book rather than a quote: three on the offer, five wanted, three filled.
        using Harness h = new();
        await h.ConnectAsync();
        h.Book(bid: 29_999.00m, bidSize: 0.3m, ask: 30_000.00m, askSize: 0.3m, secondsAfterStart: 1);

        MarketOrder order = h.Strategy.Orders.Market(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(2m));
        h.Strategy.Submit(order);

        // Three tenths on the offer against two whole units wanted: what the book had, and the rest given up, because
        // a market order rests nowhere.
        Assert.Equal(0.3m, order.FilledQuantity.Value);
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public async Task A_paper_venue_asks_the_data_client_for_the_book_it_means_to_match_against()
    {
        // A venue that matched against a book nobody subscribed to would quietly go on matching against quotes, and
        // the node would look like it was modelling depth while doing nothing of the kind.
        using Harness h = new();

        await h.ConnectAsync();
        h.Kernel.DataEngine.OnInstrument(h.Instrument);

        Assert.Contains(h.Kernel.DataEngine.SubscribedTopics, t => t.Contains("book.deltas", StringComparison.Ordinal) && t.Contains("BTCUSDT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_venue_told_not_to_match_against_the_book_neither_asks_for_it_nor_uses_it()
    {
        using Harness h = new(new SandboxExecutionClientConfig
        {
            Venue = "BINANCE",
            StartingBalances = ["100000 USDT", "1 BTC"],
            MatchAgainstBook = false,
        });
        await h.ConnectAsync();
        h.Kernel.DataEngine.OnInstrument(h.Instrument);
        h.Book(bid: 29_999.00m, bidSize: 0.3m, ask: 30_000.00m, askSize: 0.3m, secondsAfterStart: 1);

        MarketOrder order = h.Strategy.Orders.Market(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(2m));
        h.Strategy.Submit(order);

        // No subscription asked for, and the book that arrived anyway was not matched against: with no quote and no
        // bar behind it, the venue has no market to fill against at all.
        Assert.DoesNotContain(h.Kernel.DataEngine.SubscribedTopics, t => t.Contains("book.deltas", StringComparison.Ordinal));
        Assert.Empty(h.Strategy.OrderEvents.OfType<OrderFilled>());
    }

    [Fact]
    public async Task Connecting_publishes_the_starting_balances_under_the_venue_sandbox_account()
    {
        using Harness h = new();

        await h.ConnectAsync();

        Assert.Equal(new AccountId("BINANCE-SANDBOX"), h.Client.AccountId);
        Assert.True(h.Client.IsConnected);
        Assert.Equal(100_000m, h.Total(Currencies.USDT));
        Assert.Equal(1m, h.Total(Currencies.BTC));
    }

    [Fact]
    public async Task A_market_buy_fills_at_the_ask_of_the_latest_quote_and_moves_both_balances()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);

        MarketOrder order = h.Strategy.Orders.Market(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(0.5m));
        h.Strategy.Submit(order);

        OrderFilled fill = Assert.Single(h.Strategy.OrderEvents.OfType<OrderFilled>());
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(30_000.00m, fill.LastPx.Value);
        Assert.Equal(0.5m, fill.LastQty.Value);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal(new AccountId("BINANCE-SANDBOX"), fill.AccountId);
        Assert.Equal(100_000m - 15_000m, h.Total(Currencies.USDT));
        Assert.Equal(1.5m, h.Total(Currencies.BTC));
    }

    [Fact]
    public async Task A_market_sell_fills_at_the_bid()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);

        MarketOrder order = h.Strategy.Orders.Market(h.Instrument.Id, OrderSide.Sell, h.Instrument.MakeQuantity(1m));
        h.Strategy.Submit(order);

        OrderFilled fill = Assert.Single(h.Strategy.OrderEvents.OfType<OrderFilled>());
        Assert.Equal(29_999.00m, fill.LastPx.Value);
        Assert.Equal(100_000m + 29_999m, h.Total(Currencies.USDT));
        Assert.Equal(0m, h.Total(Currencies.BTC));
    }

    [Fact]
    public async Task A_resting_limit_buy_stays_open_until_a_later_quote_crosses_its_price()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);
        LimitOrder order = h.Strategy.Orders.Limit(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(1m), h.Instrument.MakePrice(29_500.00m));
        h.Strategy.Submit(order);

        h.Quote(29_600.00m, 29_601.00m, 2);
        OrderStatus afterNearMiss = order.Status;
        h.Quote(29_499.00m, 29_500.00m, 3);

        Assert.Equal(OrderStatus.Accepted, afterNearMiss);
        Assert.Equal(OrderStatus.Filled, order.Status);
        OrderFilled fill = Assert.Single(h.Strategy.OrderEvents.OfType<OrderFilled>());
        Assert.Equal(29_500.00m, fill.LastPx.Value);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(100_000m - 29_500m, h.Total(Currencies.USDT));
    }

    [Fact]
    public async Task An_order_larger_than_the_cash_balance_is_rejected_and_balances_do_not_move()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);

        MarketOrder order = h.Strategy.Orders.Market(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(10m));
        h.Strategy.Submit(order);

        Assert.Contains(order.Status, new[] { OrderStatus.Rejected, OrderStatus.Denied });
        Assert.Empty(h.Strategy.OrderEvents.OfType<OrderFilled>());
        Assert.Equal(100_000m, h.Total(Currencies.USDT));
        Assert.Equal(1m, h.Total(Currencies.BTC));
    }

    [Fact]
    public async Task Cancelling_a_resting_order_produces_a_cancel_event_and_removes_it_from_the_venue()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);
        LimitOrder order = h.Strategy.Orders.Limit(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(1m), h.Instrument.MakePrice(29_000.00m));
        h.Strategy.Submit(order);
        int openBefore = h.Client.Exchange.OpenOrderCount;

        await h.Client.CancelOrderAsync(new Core.Model.Commands.CancelOrder(h.Kernel.TraderId, h.Strategy.StrategyId, h.Instrument.Id, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp), CancellationToken.None);

        Assert.Equal(1, openBefore);
        Assert.Equal(0, h.Client.Exchange.OpenOrderCount);
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public async Task Quotes_received_after_disconnect_no_longer_fill_orders()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);
        LimitOrder order = h.Strategy.Orders.Limit(h.Instrument.Id, OrderSide.Buy, h.Instrument.MakeQuantity(1m), h.Instrument.MakePrice(29_500.00m));
        h.Strategy.Submit(order);

        await h.Client.DisconnectAsync(CancellationToken.None);
        h.Quote(29_000.00m, 29_001.00m, 2);

        Assert.False(h.Client.IsConnected);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public async Task Instruments_published_on_the_bus_after_connecting_become_tradable()
    {
        using Harness h = new();
        await h.ConnectAsync();
        CurrencyPair late = new(new InstrumentSpec
        {
            Id = new InstrumentId(new Symbol("ETHUSDT"), new Venue("BINANCE")),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.ETH,
            PricePrecision = 2,
            SizePrecision = 4,
            PriceIncrement = new Price(0.01m, 2),
            SizeIncrement = new Quantity(0.0001m, 4),
        });

        h.Kernel.DataEngine.OnInstrument(late);

        Assert.Contains(late.Id, h.Client.Exchange.Instruments.Keys);
    }

    [Fact]
    public async Task Mass_status_lists_resting_orders_so_a_restarted_node_can_reconcile()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);
        LimitOrder order = h.Strategy.Orders.Limit(h.Instrument.Id, OrderSide.Sell, h.Instrument.MakeQuantity(0.25m), h.Instrument.MakePrice(31_000.00m));
        h.Strategy.Submit(order);

        ExecutionMassStatus? status = await h.Client.GenerateMassStatusAsync(null, CancellationToken.None);

        Assert.NotNull(status);
        OrderStatusReport report = Assert.Single(status.OrderReports);
        Assert.Equal(order.ClientOrderId, report.ClientOrderId);
        Assert.Equal(OrderSide.Sell, report.OrderSide);
        Assert.Equal(0.25m, report.Quantity.Value);
        Assert.Equal(31_000.00m, report.Price!.Value.Value);
        Assert.Empty(status.PositionReports);
    }

    [Fact]
    public async Task Mass_status_is_reported_under_the_sandbox_account_and_client()
    {
        using Harness h = new();
        await h.ConnectAsync();
        h.Quote(29_999.00m, 30_000.00m, 1);
        h.Strategy.Submit(h.Strategy.Orders.Limit(h.Instrument.Id, OrderSide.Sell, h.Instrument.MakeQuantity(0.25m), h.Instrument.MakePrice(31_000.00m)));

        ExecutionMassStatus? status = await h.Client.GenerateMassStatusAsync(null, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(h.Client.AccountId, status.AccountId);
        Assert.Equal(h.Client.ClientId, status.ClientId);
        Assert.All(status.OrderReports, r => Assert.Equal(h.Client.AccountId, r.AccountId));
    }

    [Fact]
    public void The_factory_is_registered_as_SANDBOX_and_builds_a_client_for_the_configured_venue()
    {
        using Kernel kernel = new(new KernelConfig(), new TestClock(_t0));
        SandboxExecutionClientFactory factory = new();

        Core.Adapters.IExecutionClient client = factory.Create(new ClientId("BYBIT-SANDBOX"), new SandboxExecutionClientConfig { Venue = "BYBIT", AccountType = AccountType.Margin, OmsType = OmsType.Hedging }, kernel.Services);

        Assert.Equal("SANDBOX", factory.Name);
        Assert.Equal(typeof(SandboxExecutionClientConfig), factory.ConfigType);
        Assert.Equal(new Venue("BYBIT"), client.Venue);
        Assert.Equal(new AccountId("BYBIT-SANDBOX"), client.AccountId);
        Assert.Equal(AccountType.Margin, client.AccountType);
        Assert.Equal(OmsType.Hedging, client.OmsType);
    }
}
