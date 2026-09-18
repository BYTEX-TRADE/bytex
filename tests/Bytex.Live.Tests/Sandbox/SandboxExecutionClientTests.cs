using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Timing;
using Bytex.Live.Sandbox;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests.Sandbox;

// Why: sandbox mode is what users run before risking money. Orders must fill against the market data that
// flows through the kernel, at the prices that data implies, and balances must move accordingly.
public sealed class SandboxExecutionClientTests
{
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

        public decimal Total(Currency currency)
        {
            Account account = Kernel.Cache.Account(Client.AccountId) ?? throw new InvalidOperationException("sandbox account missing from the cache");
            return account.LastEvent.Balances.Single(b => b.Currency.Equals(currency)).Total.Amount;
        }

        public void Dispose() => Kernel.Dispose();
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

    [Fact(Skip = "BUG: sandbox mass status is issued under the inner simulated venue's identity (BINANCE-001 / client BINANCE), not the sandbox account BINANCE-SANDBOX that every other event uses")]
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
