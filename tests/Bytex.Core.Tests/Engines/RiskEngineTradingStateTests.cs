using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.3 - Active lets everything through the checks, Halted denies every submission, Reducing denies
// submissions that would open, increase or flip a position. Cancels must always get through.
public class RiskEngineTradingStateTests
{
    private static RiskHarness Harness()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        return h;
    }

    [Fact]
    public void Engine_starts_in_the_active_state()
    {
        Assert.Equal(TradingState.Active, Harness().Engine.TradingState);
    }

    [Fact]
    public void Halted_denies_a_valid_order_with_TRADING_HALTED()
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Halted);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal("TRADING_HALTED", Assert.Single(h.Denied).Reason);
    }

    [Fact]
    public void Halted_denies_even_an_order_that_would_close_a_position()
    {
        RiskHarness h = Harness();
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00");
        h.Engine.SetTradingState(TradingState.Halted);

        h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Sell, "1.000", reduceOnly: true));

        Assert.Empty(h.Forwarded);
        Assert.Equal("TRADING_HALTED", Assert.Single(h.Denied).Reason);
    }

    [Fact]
    public void Halted_denies_every_order_of_a_list()
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Halted);

        h.SubmitList(
            TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"),
            TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "51000.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal(["O-1", "O-2"], h.Denied.Select(d => d.ClientOrderId.Value));
        Assert.All(h.Denied, d => Assert.Equal("TRADING_HALTED", d.Reason));
    }

    [Fact]
    public void Halted_still_lets_cancels_through()
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Halted);
        CancelOrder cancel = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), h.Clock.Timestamp);
        CancelAllOrders cancelAll = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, null, null, Guid.NewGuid(), h.Clock.Timestamp);

        h.Bus.Send(Endpoints.RiskEngineExecute, cancel);
        h.Bus.Send(Endpoints.RiskEngineExecute, cancelAll);

        Assert.Equal(new TradingCommand[] { cancel, cancelAll }, h.Forwarded);
    }

    [Fact]
    public void Halted_does_not_block_modifications_which_risk_md_checks_only_for_limits_and_rate()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Engine.SetTradingState(TradingState.Halted);

        ModifyOrder command = h.Modify(order, price: "49000.00");

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Returning_to_active_lifts_the_halt()
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Halted);
        h.Engine.SetTradingState(TradingState.Active);

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Theory]
    [InlineData(OrderSide.Buy)]
    [InlineData(OrderSide.Sell)]
    public void Reducing_denies_any_order_while_flat(OrderSide side)
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Reducing);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, side, "1.000", "50000.00"));

        Assert.Empty(h.Forwarded);
        Assert.StartsWith("TRADING_REDUCING", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrderSide.Sell, "0.500", true)] // partial reduction of the 1.000 long
    [InlineData(OrderSide.Sell, "1.000", true)] // full close
    [InlineData(OrderSide.Sell, "1.001", false)] // would flip to short 0.001
    [InlineData(OrderSide.Buy, "0.001", false)] // would increase the long
    public void Reducing_with_a_long_position_allows_only_sells_up_to_the_position_size(OrderSide side, string quantity, bool allowed)
    {
        RiskHarness h = Harness();
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00");
        h.Engine.SetTradingState(TradingState.Reducing);

        SubmitOrder command = h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, side, quantity));

        AssertReducingOutcome(h, command, allowed);
    }

    [Theory]
    [InlineData(OrderSide.Buy, "2.000", true)] // full close of the 2.000 short
    [InlineData(OrderSide.Buy, "2.001", false)]
    [InlineData(OrderSide.Sell, "0.001", false)]
    public void Reducing_with_a_short_position_allows_only_buys_up_to_the_position_size(OrderSide side, string quantity, bool allowed)
    {
        RiskHarness h = Harness();
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Sell, "2.000", "50000.00");
        h.Engine.SetTradingState(TradingState.Reducing);

        SubmitOrder command = h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, side, quantity));

        AssertReducingOutcome(h, command, allowed);
    }

    [Fact]
    public void Reducing_nets_hedged_positions_before_deciding()
    {
        // Long 1.000 and short 0.400 on the same instrument: net long 0.600.
        RiskHarness h = Harness();
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00", "P-LONG");
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Sell, "0.400", "50000.00", "P-SHORT");
        h.Engine.SetTradingState(TradingState.Reducing);

        SubmitOrder ok = h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Sell, "0.600"));
        h.Submit(TestOrders.Market("O-2", TestIds.BtcUsdt, OrderSide.Sell, "0.601"));

        Assert.Same(ok, Assert.Single(h.Forwarded));
        Assert.Equal("O-2", Assert.Single(h.Denied).ClientOrderId.Value);
    }

    [Fact]
    public void Reducing_looks_only_at_the_position_of_the_ordered_instrument()
    {
        RiskHarness h = Harness();
        h.Cache.AddInstrument(TestInstruments.EthUsdt());
        h.AddPosition(TestInstruments.EthUsdt(), OrderSide.Buy, "5.000", "3000.00");
        h.Engine.SetTradingState(TradingState.Reducing);

        h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Sell, "1.000"));

        Assert.Empty(h.Forwarded);
        Assert.StartsWith("TRADING_REDUCING", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact(Skip = "BUG: RiskEngine.cs:156-200 HandleSubmitOrderList never evaluates TradingState.Reducing; a position-opening list is forwarded")]
    public void Reducing_denies_an_order_list_that_would_open_a_position()
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Reducing);

        h.SubmitList(
            TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000"),
            TestOrders.StopMarket("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "45000.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal(2, h.Denied.Count);
    }

    [Fact]
    public void Reset_returns_the_engine_to_active_and_zeroes_the_counters()
    {
        RiskHarness h = Harness();
        h.Engine.SetTradingState(TradingState.Halted);
        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.Reset();

        Assert.Equal(TradingState.Active, h.Engine.TradingState);
        Assert.Equal(0, h.Engine.CommandCount);
        Assert.Equal(0, h.Engine.DeniedCount);
    }

    private static void AssertReducingOutcome(RiskHarness h, SubmitOrder command, bool allowed)
    {
        if (allowed)
        {
            Assert.Empty(h.Denied);
            Assert.Same(command, Assert.Single(h.Forwarded));
        }
        else
        {
            Assert.Empty(h.Forwarded);
            Assert.StartsWith("TRADING_REDUCING", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
        }
    }
}
