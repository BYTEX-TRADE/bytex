using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: flattening at shutdown sends real orders. It must touch only the given strategy, only instruments
// with something open, and close positions with reduce-only orders of exactly the open size.
public sealed class ShutdownHelperTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kernel = new Kernel(new KernelConfig { Environment = TradingEnvironment.Live }, new TestClock(UnixNanos.FromSeconds(1_700_000_000)));
            Btc = TestInstruments.BtcUsdt("FAKE");
            Eth = new CurrencyPair(new InstrumentSpec
            {
                Id = new InstrumentId(new Symbol("ETHUSDT"), new Venue("FAKE")),
                AssetClass = AssetClass.Crypto,
                InstrumentClass = InstrumentClass.Spot,
                QuoteCurrency = Currencies.USDT,
                BaseCurrency = Currencies.ETH,
                PricePrecision = 2,
                SizePrecision = 4,
                PriceIncrement = new Price(0.01m, 2),
                SizeIncrement = new Quantity(0.0001m, 4),
            });
            Kernel.Cache.AddInstrument(Btc);
            Kernel.Cache.AddInstrument(Eth);
            Exec = new FakeExecutionClient(new ClientId("FAKE-EXEC"), new Venue("FAKE"), Kernel.Services, new Journal());
            Kernel.ExecutionEngine.RegisterClient(Exec);
            Mine = new ProbeStrategy("Mine-001");
            Other = new ProbeStrategy("Other-002");
            Kernel.Trader.AddStrategy(Mine);
            Kernel.Trader.AddStrategy(Other);
            Kernel.Start();
            Exec.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public Kernel Kernel { get; }

        public CurrencyPair Btc { get; }

        public CurrencyPair Eth { get; }

        public FakeExecutionClient Exec { get; }

        public ProbeStrategy Mine { get; }

        public ProbeStrategy Other { get; }

        public static LimitOrder RestingBuy(ProbeStrategy strategy, Instrument instrument, decimal price)
        {
            LimitOrder order = strategy.Orders.Limit(instrument.Id, OrderSide.Buy, instrument.MakeQuantity(0.1m), instrument.MakePrice(price));
            strategy.Submit(order);
            return order;
        }

        public void Dispose() => Kernel.Dispose();
    }

    [Fact]
    public void Cancel_sends_one_cancel_all_per_instrument_with_open_orders_of_that_strategy_only()
    {
        using Rig rig = new();
        LimitOrder mineBtc1 = Rig.RestingBuy(rig.Mine, rig.Btc, 20_000m);
        LimitOrder mineBtc2 = Rig.RestingBuy(rig.Mine, rig.Btc, 19_000m);
        LimitOrder mineEth = Rig.RestingBuy(rig.Mine, rig.Eth, 1_000m);
        LimitOrder othersBtc = Rig.RestingBuy(rig.Other, rig.Btc, 18_000m);

        ShutdownHelper.Flatten(rig.Mine, rig.Kernel, cancelOrders: true, closePositions: false);

        Assert.Equal(2, rig.Exec.CancelAlls.Count);
        Assert.Equal(new[] { rig.Btc.Id, rig.Eth.Id }.OrderBy(i => i.ToString()), rig.Exec.CancelAlls.Select(c => c.InstrumentId).OrderBy(i => i.ToString()));
        Assert.All(rig.Exec.CancelAlls, c => Assert.Equal(rig.Mine.StrategyId, c.StrategyId));
        Assert.All(new[] { mineBtc1, mineBtc2, mineEth }, o => Assert.Equal(OrderStatus.Canceled, o.Status));
        Assert.Equal(OrderStatus.Accepted, othersBtc.Status);
    }

    [Theory]
    [InlineData(OrderSide.Buy, OrderSide.Sell)]
    [InlineData(OrderSide.Sell, OrderSide.Buy)]
    public void Close_submits_a_reduce_only_market_order_on_the_closing_side_for_the_full_open_quantity(OrderSide entry, OrderSide expectedExit)
    {
        using Rig rig = new();
        rig.Exec.MarketFillPrice = 30_000m;
        rig.Mine.Submit(rig.Mine.Orders.Market(rig.Btc.Id, entry, rig.Btc.MakeQuantity(0.75m)));
        Position position = Assert.Single(rig.Kernel.Cache.PositionsOpen(strategyId: rig.Mine.StrategyId));
        int submittedBefore = rig.Exec.Submitted.Count;

        ShutdownHelper.Flatten(rig.Mine, rig.Kernel, cancelOrders: false, closePositions: true);

        SubmitOrder command = Assert.Single(rig.Exec.Submitted.Skip(submittedBefore));
        Assert.Equal(OrderType.Market, command.Order.Type);
        Assert.Equal(expectedExit, command.Order.Side);
        Assert.Equal(0.75m, command.Order.Quantity.Value);
        Assert.True(command.Order.IsReduceOnly);
        Assert.Contains("SHUTDOWN", command.Order.Tags);
        Assert.Equal(position.Id, command.PositionId);
        Assert.Equal(rig.Mine.StrategyId, command.StrategyId);
        Assert.Empty(rig.Kernel.Cache.PositionsOpen(strategyId: rig.Mine.StrategyId));
        Assert.Empty(rig.Exec.CancelAlls);
    }

    [Fact]
    public void Close_does_not_touch_positions_of_other_strategies()
    {
        using Rig rig = new();
        rig.Exec.MarketFillPrice = 30_000m;
        rig.Other.Submit(rig.Other.Orders.Market(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.2m)));
        int submittedBefore = rig.Exec.Submitted.Count;

        ShutdownHelper.Flatten(rig.Mine, rig.Kernel, cancelOrders: true, closePositions: true);

        Assert.Equal(submittedBefore, rig.Exec.Submitted.Count);
        Assert.Single(rig.Kernel.Cache.PositionsOpen(strategyId: rig.Other.StrategyId));
    }

    [Fact]
    public void Shutdown_order_ids_do_not_collide_with_ids_the_strategy_already_used()
    {
        using Rig rig = new();
        rig.Exec.MarketFillPrice = 30_000m;
        rig.Mine.Submit(rig.Mine.Orders.Market(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.5m)));
        rig.Mine.Submit(rig.Mine.Orders.Market(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.5m)));

        ShutdownHelper.Flatten(rig.Mine, rig.Kernel, cancelOrders: false, closePositions: true);

        List<ClientOrderId> ids = rig.Exec.Submitted.Select(s => s.Order.ClientOrderId).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
    }
}
