using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Orders;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.5 - the bypass switch must skip every check, including trading state and rate limits.
public class RiskEngineBypassTests
{
    [Fact]
    public void Bypass_forwards_an_order_that_would_fail_every_check()
    {
        RiskHarness h = new(new RiskEngineConfig { Bypass = true, MaxOrderSubmitRate = 0 });
        h.Engine.SetTradingState(TradingState.Halted);

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.0001", "-1.00"));

        Assert.True(h.Engine.IsBypassed);
        Assert.Same(command, Assert.Single(h.Forwarded));
        Assert.Empty(h.Events);
        Assert.Equal(0, h.Engine.DeniedCount);
    }

    [Fact]
    public void Bypass_forwards_a_modification_of_an_order_the_cache_does_not_know()
    {
        RiskHarness h = new(new RiskEngineConfig { Bypass = true });
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        ModifyOrder command = h.Modify(order, quantity: "0.000");

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Checks_are_on_by_default()
    {
        RiskHarness h = new();

        Assert.False(h.Engine.IsBypassed);
    }
}
