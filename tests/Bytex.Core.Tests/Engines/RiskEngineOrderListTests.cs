using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: an order list is a unit (a bracket must never reach the venue without its stop). One failing order
// denies every order of the list with the same reason, and nothing is forwarded.
public class RiskEngineOrderListTests
{
    private static RiskHarness Harness(RiskEngineConfig? config = null)
    {
        RiskHarness h = new(config);
        h.Cache.AddInstrument(TestInstruments.BtcUsdt(maxQuantity: Quantity.Parse("100.000")));
        return h;
    }

    [Fact]
    public void Valid_list_is_forwarded_as_one_command()
    {
        RiskHarness h = Harness();

        SubmitOrderList command = h.SubmitList(
            TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000"),
            TestOrders.StopMarket("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "45000.00"),
            TestOrders.Limit("O-3", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "55000.00"));

        Assert.Same(command, Assert.Single(h.Forwarded));
        Assert.Empty(h.Events);
    }

    [Fact]
    public void One_invalid_order_denies_every_order_of_the_list_with_that_reason()
    {
        RiskHarness h = Harness();

        h.SubmitList(
            TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000"),
            TestOrders.StopMarket("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "45000.123"),
            TestOrders.Limit("O-3", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "55000.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal(["O-1", "O-2", "O-3"], h.Denied.Select(d => d.ClientOrderId.Value));
        Assert.All(h.Denied, d => Assert.StartsWith("PRICE_PRECISION", d.Reason, StringComparison.Ordinal));
        Assert.Equal(3, h.Engine.DeniedCount);
    }

    [Fact]
    public void First_failing_order_determines_the_reason()
    {
        RiskHarness h = Harness();

        h.SubmitList(
            TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "100.001", "50000.00"),
            TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "0.00"));

        Assert.All(h.Denied, d => Assert.StartsWith("QUANTITY_EXCEEDS_MAX", d.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Order_for_an_unknown_instrument_denies_the_whole_list()
    {
        RiskHarness h = Harness();

        h.SubmitList(
            TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"),
            TestOrders.Limit("O-2", TestIds.EthUsdt, OrderSide.Buy, "1.000", "3000.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal(2, h.Denied.Count);
        Assert.All(h.Denied, d => Assert.Contains("ETHUSDT.BINANCE", d.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Bypass_forwards_a_list_without_looking_at_it()
    {
        RiskHarness h = new(new RiskEngineConfig { Bypass = true });

        SubmitOrderList command = h.SubmitList(TestOrders.Limit("O-1", TestIds.EthUsdt, OrderSide.Buy, "1.0001", "0.00"));

        Assert.Same(command, Assert.Single(h.Forwarded));
        Assert.Empty(h.Events);
    }
}
