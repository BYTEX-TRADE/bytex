using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.10 - a graph that has run away, or someone experimenting, can put hundreds of orders on a venue before
// anything complains, and the account is left holding whatever the venue accepted. These caps are the ceiling on how
// much may be going at once: how many positions are open and how many orders are working, on one instrument and
// across the account. What is pinned here is what counts towards a cap (what the cache holds, plus the orders of the
// same submitted list), what never does (anything that reduces), and that a denial names the cap it hit.
public class RiskEngineCapsTests
{
    private static RiskHarness Harness(RiskEngineConfig config)
    {
        RiskHarness h = new(config);
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddInstrument(TestInstruments.EthUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 1_000_000m, 0m), (Currencies.BTC, 10m, 0m))));
        return h;
    }

    private static LimitOrder Buy(string id, Instrument instrument, string quantity = "0.010") =>
        TestOrders.Limit(id, instrument.Id, OrderSide.Buy, quantity, "50000.00");

    private static OrderDenied AssertDenied(RiskHarness h, string code)
    {
        Assert.Empty(h.Forwarded);
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.StartsWith(code, denied.Reason, StringComparison.Ordinal);
        return denied;
    }

    [Fact]
    public void An_instrument_at_its_position_cap_takes_nothing_that_would_add()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxOpenPositionsPerInstrument = 1 } });
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "0.010", "50000.00");

        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));

        OrderDenied denied = AssertDenied(h, "POSITION_CAP");
        Assert.Contains("1 open on BTCUSDT.BINANCE reaches the cap of 1", denied.Reason, StringComparison.Ordinal);
        Assert.Equal(1, h.Engine.CapDeniedCount);
    }

    [Fact]
    public void The_account_cap_counts_the_instruments_it_is_flat_on()
    {
        // One position open, cap of one: another instrument would make it two, and more of the same instrument is
        // the position the account already holds rather than a new one.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxOpenPositions = 1 } });
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "0.010", "50000.00");

        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));
        Assert.Single(h.Forwarded);

        h.Submit(TestOrders.Limit("O-2", TestIds.EthUsdt, OrderSide.Buy, "1.000", "2500.00"));

        OrderDenied denied = Assert.Single(h.Denied);
        Assert.StartsWith("POSITION_CAP", denied.Reason, StringComparison.Ordinal);
        Assert.Contains("across the account reaches the cap of 1", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_instrument_at_its_working_order_cap_takes_no_more()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrdersPerInstrument = 2 } });
        h.AddAccepted(Buy("O-RESTING-1", TestInstruments.BtcUsdt()));
        h.AddAccepted(Buy("O-RESTING-2", TestInstruments.BtcUsdt()));

        // Another instrument is not at its own cap, so the cap is about this instrument and not about the account.
        h.Submit(TestOrders.Limit("O-ETH", TestIds.EthUsdt, OrderSide.Buy, "1.000", "2500.00"));
        Assert.Single(h.Forwarded);

        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));

        Assert.Contains("2 working on BTCUSDT.BINANCE reaches the cap of 2", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
        Assert.Equal(1, h.Engine.CapDeniedCount);
    }

    [Fact]
    public void The_account_cap_counts_the_orders_working_everywhere()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrders = 2 } });
        h.AddAccepted(Buy("O-RESTING-1", TestInstruments.BtcUsdt()));
        h.AddAccepted(TestOrders.Limit("O-RESTING-2", TestIds.EthUsdt, OrderSide.Buy, "1.000", "2500.00"));

        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));

        Assert.Contains("2 working across the account reaches the cap of 2", AssertDenied(h, "ORDER_CAP").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cap_never_stops_an_order_getting_out()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxOpenPositionsPerInstrument = 1, MaxOpenPositions = 1, MaxWorkingOrdersPerInstrument = 1, MaxWorkingOrders = 1 } });
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00");
        h.AddAccepted(Buy("O-RESTING", TestInstruments.BtcUsdt()));

        h.Submit(TestOrders.Market("O-REDUCE-ONLY", TestIds.BtcUsdt, OrderSide.Buy, "0.010", reduceOnly: true));
        h.Submit(TestOrders.Limit("O-CLOSING", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "50000.00"));

        Assert.Equal(2, h.Forwarded.Count);
        Assert.Empty(h.Denied);
        Assert.Equal(0, h.Engine.CapDeniedCount);
    }

    [Fact]
    public void A_list_cannot_walk_past_an_order_cap_one_order_at_a_time()
    {
        // Nothing is working yet, so each of these fits under a cap of two - until the ones before it are counted.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrdersPerInstrument = 2 } });

        h.SubmitList(Buy("O-1", TestInstruments.BtcUsdt()), Buy("O-2", TestInstruments.BtcUsdt()), Buy("O-3", TestInstruments.BtcUsdt()));

        Assert.Empty(h.Forwarded);
        Assert.Equal(3, h.Denied.Count);
        Assert.All(h.Denied, d => Assert.StartsWith("ORDER_CAP", d.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_list_across_instruments_is_counted_against_the_account_cap()
    {
        // Two instruments, one order each, and a cap of one across the account: the second is over it although
        // nothing is working on its own instrument.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrders = 1 } });

        h.SubmitList(Buy("O-1", TestInstruments.BtcUsdt()), TestOrders.Limit("O-2", TestIds.EthUsdt, OrderSide.Buy, "1.000", "2500.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal(2, h.Denied.Count);
        Assert.All(h.Denied, d => Assert.Contains("1 working across the account reaches the cap of 1", d.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_list_inside_the_caps_goes_whole()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrdersPerInstrument = 3, MaxWorkingOrders = 3 } });

        h.SubmitList(Buy("O-1", TestInstruments.BtcUsdt()), Buy("O-2", TestInstruments.BtcUsdt()), Buy("O-3", TestInstruments.BtcUsdt()));

        Assert.Single(h.Forwarded);
        Assert.Empty(h.Denied);
        Assert.Equal(0, h.Engine.CapDeniedCount);
    }

    [Fact]
    public void An_engine_told_of_no_caps_has_none()
    {
        RiskHarness h = Harness(new RiskEngineConfig());
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "0.010", "50000.00");
        for (int i = 0; i < 20; i++)
        {
            h.AddAccepted(Buy("O-RESTING-" + i, TestInstruments.BtcUsdt()));
        }

        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));

        Assert.Single(h.Forwarded);
        Assert.Equal(0, h.Engine.CapDeniedCount);
    }

    [Fact]
    public void A_reset_engine_starts_its_count_again()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrdersPerInstrument = 1 } });
        h.AddAccepted(Buy("O-RESTING", TestInstruments.BtcUsdt()));
        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));
        Assert.Equal(1, h.Engine.CapDeniedCount);

        h.Engine.Reset();

        Assert.Equal(0, h.Engine.CapDeniedCount);
        Assert.Equal(0, h.Engine.DeniedCount);
    }

    [Fact]
    public void A_bypassed_engine_ignores_the_caps_with_everything_else()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Bypass = true, Limits = new RiskLimits { MaxWorkingOrdersPerInstrument = 1, MaxOpenPositionsPerInstrument = 1 } });
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "0.010", "50000.00");
        h.AddAccepted(Buy("O-RESTING", TestInstruments.BtcUsdt()));

        h.Submit(Buy("O-1", TestInstruments.BtcUsdt()));

        Assert.Single(h.Forwarded);
        Assert.Empty(h.Denied);
    }
}
