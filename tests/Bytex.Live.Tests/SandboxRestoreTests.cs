using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Live.Sandbox;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: a paper node on a perpetual could not be stopped and started again while it held a position. The simulated venue
// kept the position and its resting stop and target in memory, and there was no way to hand them back, so a restart either
// lost them or had to be refused. These tests restart such a node from configuration and then let the market hit the stop:
// the engine must know the position as the strategy's own, the stop must close it at the venue with the right P&L, and the
// target must go with it.
public sealed class SandboxRestoreTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private static CryptoPerpetual Perp() => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), new Venue("FAKE")),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
        MakerFee = 0m,
        TakerFee = 0m,
    });

    private static SandboxExecutionClientConfig Sandbox(SandboxRestoreConfig? restore, AccountType account = AccountType.Margin) => new()
    {
        Venue = "FAKE",
        AccountType = account,
        OmsType = OmsType.Netting,
        StartingBalances = ["10000 USDT"],
        DefaultLeverage = 5m,
        Restore = restore,
    };

    private static readonly SandboxRestoreConfig LongWithStopAndTarget = new()
    {
        Positions = [new SandboxRestoredPosition { InstrumentId = "BTCUSDT-PERP.FAKE", Side = PositionSide.Long, Quantity = 0.1m, AvgPx = 50_000m }],
        Orders =
        [
            new SandboxRestoredOrder { ClientOrderId = "O-STOP", InstrumentId = "BTCUSDT-PERP.FAKE", Side = OrderSide.Sell, Type = OrderType.StopMarket, Quantity = 0.1m, TriggerPrice = 49_000m, ReduceOnly = true, LinkedOrderIds = ["O-TARGET"] },
            new SandboxRestoredOrder { ClientOrderId = "O-TARGET", InstrumentId = "BTCUSDT-PERP.FAKE", Side = OrderSide.Sell, Type = OrderType.Limit, Quantity = 0.1m, Price = 52_000m, ReduceOnly = true, LinkedOrderIds = ["O-STOP"] },
        ],
    };

    private sealed record Rig(TradingNode Node, ProbeStrategy Strategy, FakeDataClient Data, Instrument Instrument) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(SandboxExecutionClientConfig sandbox, Instrument? instrument = null)
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());
        instrument ??= Perp();

        TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Sandbox, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("SANDBOX", "FAKE", sandbox)],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);
        node.AddInstrument(instrument);
        ProbeStrategy strategy = new("Probe-001", journal, [instrument.Id]) { SubscribeTo = instrument.Id };
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);
        return new Rig(node, strategy, dataFactory.Created.Single().Client, instrument);
    }

    private static async Task<T> OnLoopAsync<T>(TradingNode node, Func<T> read) => await node.Loop.InvokeAsync(read).WaitAsync(_timeout);

    private static async Task QuoteAsync(Rig rig, decimal bid, decimal ask)
    {
        int seen = await OnLoopAsync(rig.Node, () => rig.Strategy.Quotes.Count);
        rig.Data.Emit(TestInstruments.Quote(rig.Instrument, bid, ask, rig.Node.Kernel.Clock.Timestamp));
        DateTime deadline = DateTime.UtcNow + _timeout;
        while (await OnLoopAsync(rig.Node, () => rig.Strategy.Quotes.Count) == seen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static decimal Usdt(Account account) => account.Balances[Currencies.USDT].Total.Amount;

    [Fact]
    public async Task A_restarted_node_holds_the_position_as_its_strategys_own_with_its_stop_and_target_open()
    {
        await using Rig rig = await StartAsync(Sandbox(LongWithStopAndTarget));

        Position position = Assert.Single(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id)));
        Assert.Equal(new StrategyId("Probe-001"), position.StrategyId);
        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Equal(0.1m, position.Quantity.Value);
        Assert.Equal(50_000m, position.AvgPxOpen);

        IReadOnlyList<Order> open = await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.OrdersOpen(instrumentId: rig.Instrument.Id));
        Assert.Equal(["O-STOP", "O-TARGET"], open.Select(o => o.ClientOrderId.Value).Order());
        Assert.All(open, o => Assert.Equal(new StrategyId("Probe-001"), o.StrategyId));
        Order stop = open.Single(o => o.ClientOrderId.Value == "O-STOP");
        Assert.Equal((OrderType.StopMarket, OrderSide.Sell, 49_000m, true), (stop.Type, stop.Side, stop.TriggerPrice!.Value.Value, stop.IsReduceOnly));
        Order target = open.Single(o => o.ClientOrderId.Value == "O-TARGET");
        Assert.Equal((OrderType.Limit, 52_000m), (target.Type, target.Price!.Value.Value));

        // The venue agrees: the same position, and the restored balance untouched by it.
        SandboxExecutionClient sandbox = (SandboxExecutionClient)rig.Node.ExecutionClients.Single();
        Assert.Equal(0.1m, sandbox.Exchange.NetPosition(rig.Instrument.Id));
        Assert.Equal(10_000m, sandbox.Exchange.Balances[Currencies.USDT]);
    }

    [Fact]
    public async Task The_restored_stop_closes_the_position_at_the_venue_and_takes_the_target_with_it()
    {
        await using Rig rig = await StartAsync(Sandbox(LongWithStopAndTarget));
        SandboxExecutionClient sandbox = (SandboxExecutionClient)rig.Node.ExecutionClients.Single();

        await QuoteAsync(rig, 50_100m, 50_101m);
        Assert.Equal(2, sandbox.Exchange.OpenOrderCount);
        Assert.Single(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id)));

        await QuoteAsync(rig, 48_990m, 48_991m);

        Assert.Empty(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id)));
        Assert.Empty(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.OrdersOpen(instrumentId: rig.Instrument.Id)));
        Assert.Equal(0, sandbox.Exchange.OpenOrderCount);
        Assert.Equal(0m, sandbox.Exchange.NetPosition(rig.Instrument.Id));

        List<OrderEvent> events = await OnLoopAsync(rig.Node, () => rig.Strategy.OrderEvents.ToList());
        OrderFilled fill = Assert.Single(events.OfType<OrderFilled>(), f => f.ClientOrderId.Value == "O-STOP");
        Assert.Equal((0.1m, 48_990m), (fill.LastQty.Value, fill.LastPx.Value));
        Assert.Contains(events, e => e is OrderCanceled c && c.ClientOrderId.Value == "O-TARGET");

        // 0.1 bought at 50,000 and sold at 48,990 loses 101 USDT, measured from the restored average price.
        Assert.Equal(10_000m - 101m, sandbox.Exchange.Balances[Currencies.USDT]);
        Account account = (await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.Account(sandbox.AccountId)))!;
        Assert.Equal(9_899m, Usdt(account));
    }

    [Fact]
    public async Task A_restored_stop_can_be_moved_and_cancelled_by_the_strategy()
    {
        await using Rig rig = await StartAsync(Sandbox(LongWithStopAndTarget));
        SandboxExecutionClient sandbox = (SandboxExecutionClient)rig.Node.ExecutionClients.Single();
        await QuoteAsync(rig, 50_100m, 50_101m);

        await rig.Node.Loop.InvokeAsync(() =>
        {
            Order stop = rig.Node.Kernel.Cache.Order(new ClientOrderId("O-STOP"))!;
            rig.Strategy.Modify(stop, triggerPrice: rig.Instrument.MakePrice(49_500m));
        }).WaitAsync(_timeout);
        await QuoteAsync(rig, 50_100m, 50_101m);
        Order moved = (await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.Order(new ClientOrderId("O-STOP"))))!;
        Assert.Equal(49_500m, moved.TriggerPrice!.Value.Value);

        // Between the old trigger and the new one: only the moved stop fires.
        await QuoteAsync(rig, 49_400m, 49_401m);
        Assert.Equal(0m, sandbox.Exchange.NetPosition(rig.Instrument.Id));
        Assert.Empty(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id)));
    }

    [Fact]
    public async Task A_short_position_is_restored_as_a_short()
    {
        SandboxRestoreConfig restore = new() { Positions = [new SandboxRestoredPosition { InstrumentId = "BTCUSDT-PERP.FAKE", Side = PositionSide.Short, Quantity = 0.2m, AvgPx = 50_000m }] };
        await using Rig rig = await StartAsync(Sandbox(restore));

        Position position = Assert.Single(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id)));
        Assert.Equal((PositionSide.Short, 0.2m, 50_000m), (position.Side, position.Quantity.Value, position.AvgPxOpen));
        Assert.Equal(-0.2m, ((SandboxExecutionClient)rig.Node.ExecutionClients.Single()).Exchange.NetPosition(rig.Instrument.Id));
    }

    [Fact]
    public async Task Without_a_restore_the_node_starts_flat()
    {
        await using Rig rig = await StartAsync(Sandbox(null));

        Assert.Empty(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.PositionsOpen()));
        Assert.Empty(await OnLoopAsync(rig.Node, () => rig.Node.Kernel.Cache.Orders()));
    }

    // Starting flat where a position was asked for would be a lie about the account, so the start fails and says why.
    [Fact]
    public async Task A_restore_the_venue_cannot_honour_stops_the_start()
    {
        SandboxRestoreConfig unknown = new() { Positions = [new SandboxRestoredPosition { InstrumentId = "ETHUSDT-PERP.FAKE", Side = PositionSide.Long, Quantity = 1m, AvgPx = 3_000m }] };
        InvalidOperationException notLoaded = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(Sandbox(unknown)));
        Assert.Contains("ETHUSDT-PERP.FAKE is not loaded", notLoaded.Message, StringComparison.Ordinal);

        SandboxRestoreConfig spot = new() { Positions = [new SandboxRestoredPosition { InstrumentId = "BTCUSDT.FAKE", Side = PositionSide.Long, Quantity = 1m, AvgPx = 50_000m }] };
        InvalidOperationException cash = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(Sandbox(spot, AccountType.Cash), TestInstruments.BtcUsdt("FAKE")));
        Assert.Contains("starting balances", cash.Message, StringComparison.Ordinal);

        SandboxRestoreConfig market = new() { Orders = [new SandboxRestoredOrder { ClientOrderId = "O-1", InstrumentId = "BTCUSDT-PERP.FAKE", Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 0.1m }] };
        InvalidOperationException notResting = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(Sandbox(market)));
        Assert.Contains("O-1 is a Market", notResting.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restore_block_is_read_from_the_nodes_json_configuration()
    {
        const string json = """
            {
              "venue": "BYBIT", "accountType": "margin", "startingBalances": ["9899 USDT"],
              "restore": {
                "positions": [ { "instrumentId": "BTCUSDT-PERP.BYBIT", "side": "short", "quantity": "0.25", "avgPx": "61234.5" } ],
                "orders": [
                  { "clientOrderId": "O-7", "instrumentId": "BTCUSDT-PERP.BYBIT", "side": "buy", "type": "stopMarket", "quantity": 0.25, "triggerPrice": 62000, "reduceOnly": true, "linkedOrderIds": ["O-8"] },
                  { "clientOrderId": "O-8", "instrumentId": "BTCUSDT-PERP.BYBIT", "side": "buy", "type": "limit", "quantity": 0.25, "price": 60000, "postOnly": true }
                ]
              }
            }
            """;

        SandboxExecutionClientConfig config = System.Text.Json.JsonSerializer.Deserialize<SandboxExecutionClientConfig>(json, Bytex.Core.Serialization.BytexJson.Options)!;

        SandboxRestoredPosition position = Assert.Single(config.Restore!.Positions);
        Assert.Equal(("BTCUSDT-PERP.BYBIT", PositionSide.Short, 0.25m, 61_234.5m), (position.InstrumentId, position.Side, position.Quantity, position.AvgPx));
        Assert.Equal(2, config.Restore.Orders.Count);
        SandboxRestoredOrder stop = config.Restore.Orders[0];
        Assert.Equal(("O-7", OrderSide.Buy, OrderType.StopMarket, 0.25m, 62_000m, true, TimeInForce.Gtc), (stop.ClientOrderId, stop.Side, stop.Type, stop.Quantity, stop.TriggerPrice, stop.ReduceOnly, stop.TimeInForce));
        Assert.Equal(["O-8"], stop.LinkedOrderIds);
        Assert.Equal((OrderType.Limit, 60_000m, true), (config.Restore.Orders[1].Type, config.Restore.Orders[1].Price, config.Restore.Orders[1].PostOnly));
    }
}
