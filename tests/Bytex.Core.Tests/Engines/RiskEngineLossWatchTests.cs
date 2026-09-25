using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.14 and R4.15. The loss limit counted what had been realised and looked only at the order in front of it, so
// the two things that do the damage were both invisible to it: a position held through a fall costs the account just
// as much as one closed in it, and a strategy that holds and sends nothing is never judged at all. A limit like that
// reports a loss instead of stopping it. What is pinned here is that the open loss counts, that it is watched between
// fills, and that reaching the limit stops the engine trading rather than only denying the next order - stops it
// where a position can still be got out of, once per period, and until a host resumes it.
public class RiskEngineLossWatchTests
{
    private static readonly Money Thousand = new(1_000m, Currencies.USDT);

    private static RiskHarness Harness(RiskLimits limits, decimal equity = 10_000m)
    {
        RiskHarness h = new(new RiskEngineConfig { Limits = limits });
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, equity, 0m), (Currencies.BTC, 10m, 0m))));
        return h;
    }

    /// <summary>A long position of one bitcoin opened at 50,000, which is what the price is then moved against.</summary>
    private static void OpenLong(RiskHarness h) =>
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00", positionId: "P-OPEN");

    /// <summary>Publishes a price, the way a venue does: the engine's watch is driven by exactly this.</summary>
    private static void PriceIs(RiskHarness h, string price)
    {
        QuoteTick quote = new(TestIds.BtcUsdt, Price.Parse(price), Price.Parse(price), Quantity.Parse("1.000"), Quantity.Parse("1.000"), h.Clock.Timestamp, h.Clock.Timestamp);
        h.Cache.AddQuoteTick(quote);
        h.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), quote);
    }

    private static Order Adds(string id) => TestOrders.Limit(id, TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00");

    private static Order GetsOut(string id) => TestOrders.Market(id, TestIds.BtcUsdt, OrderSide.Sell, "1.000");

    [Theory]
    [InlineData("49200.00", true)]  // 800 down: inside the limit
    [InlineData("49000.00", false)] // 1,000 down: the limit is reached, not merely passed
    [InlineData("45000.00", false)]
    public void What_an_open_position_is_down_counts_towards_the_limit(string price, bool allowed)
    {
        // Told only to deny, so that the reason on the order is the loss limit itself rather than the state the
        // engine would otherwise have put itself into: what it does about a breach is the subject further down.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.DenyAdds });
        OpenLong(h);
        PriceIs(h, price);

        h.Submit(Adds("O-1"));

        if (allowed)
        {
            Assert.Single(h.Forwarded);
            Assert.Equal(0, h.Engine.LossLimitDeniedCount);
            Assert.Equal(TradingState.Active, h.Engine.TradingState);
        }
        else
        {
            Assert.Empty(h.Forwarded);
            Assert.StartsWith("LOSS_LIMIT", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
            Assert.Contains("realised and open", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
            Assert.Equal(1, h.Engine.LossLimitDeniedCount);
        }
    }

    [Fact]
    public void What_is_realised_and_what_is_open_are_counted_together()
    {
        // 600 realised and 600 open is 1,200: neither half reaches the limit on its own.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.DenyAdds });
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00", positionId: "P-CLOSED");
        h.Cache.Position(new PositionId("P-CLOSED"))!
            .Apply(TestEvents.Filled(TestOrders.Market("O-CLOSE", TestIds.BtcUsdt, OrderSide.Sell, "1.000"), "T-CLOSE", "1.000", "49400.00", positionId: new PositionId("P-CLOSED")));
        OpenLong(h);
        PriceIs(h, "49400.00");

        h.Submit(Adds("O-1"));

        Assert.Empty(h.Forwarded);
        Assert.Contains(new Money(1_200m, Currencies.USDT).ToString(), Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_moving_against_the_node_stops_it_trading_with_no_order_in_sight()
    {
        // Nothing is submitted at all here: the whole point is that the engine sees the loss between fills.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand) });
        OpenLong(h);

        PriceIs(h, "49500.00");
        Assert.Equal(TradingState.Active, h.Engine.TradingState);
        Assert.Equal(0, h.Engine.LossLimitStoppedCount);

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48500.00");

        Assert.Equal(TradingState.Reducing, h.Engine.TradingState);
        Assert.Equal(1, h.Engine.LossLimitStoppedCount);

        // Stopped where a position can still be got out of: what adds is denied, what closes goes through.
        h.Submit(Adds("O-ADD"));
        h.Submit(GetsOut("O-OUT"));

        Assert.StartsWith("TRADING_REDUCING", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
        Assert.Single(h.Forwarded);
    }

    [Fact]
    public void The_watch_runs_no_more_often_than_its_interval()
    {
        RiskHarness h = new(new RiskEngineConfig
        {
            Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand) },
            LossWatchInterval = TimeSpan.FromSeconds(10),
        });
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 10_000m, 0m))));

        for (int i = 0; i < 5; i++)
        {
            h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromSeconds(1));
            PriceIs(h, "50000.00");
        }

        Assert.Equal(1, h.Engine.LossWatchCount);

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromSeconds(10));
        PriceIs(h, "50000.00");

        Assert.Equal(2, h.Engine.LossWatchCount);
    }

    [Fact]
    public void A_node_with_no_loss_limit_watches_nothing()
    {
        RiskHarness h = Harness(new RiskLimits { MaxExposure = RiskLimit.Of(Thousand) });
        OpenLong(h);

        for (int i = 0; i < 10; i++)
        {
            h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
            PriceIs(h, "10000.00");
        }

        Assert.Equal(0, h.Engine.LossWatchCount);
        Assert.Equal(TradingState.Active, h.Engine.TradingState);
    }

    [Fact]
    public void A_limit_told_only_to_deny_never_touches_the_trading_state()
    {
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.DenyAdds });
        OpenLong(h);
        PriceIs(h, "45000.00");

        Assert.Equal(TradingState.Active, h.Engine.TradingState);
        Assert.Equal(0, h.Engine.LossLimitStoppedCount);

        h.Submit(Adds("O-1"));
        Assert.StartsWith("LOSS_LIMIT", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
        Assert.Equal(TradingState.Active, h.Engine.TradingState);

        // And the order after it is judged on its own merits: the loss recovers and it goes through.
        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "50000.00");
        h.Submit(Adds("O-2"));

        Assert.Single(h.Forwarded);
    }

    [Fact]
    public void The_engine_stops_itself_once_in_a_period_and_a_host_that_resumes_it_is_not_overruled()
    {
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), LossPeriod = TimeSpan.FromDays(1) });
        OpenLong(h);
        PriceIs(h, "45000.00");

        Assert.Equal(TradingState.Reducing, h.Engine.TradingState);

        // What a host does through TradingNode.Resume.
        h.Engine.SetTradingState(TradingState.Active);
        for (int i = 0; i < 5; i++)
        {
            h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
            PriceIs(h, "44000.00");
        }

        Assert.Equal(TradingState.Active, h.Engine.TradingState);
        Assert.Equal(1, h.Engine.LossLimitStoppedCount);

        // The limit still stands, though: the rest of the period is judged order by order.
        h.Submit(Adds("O-1"));
        Assert.StartsWith("LOSS_LIMIT", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_period_opens_where_the_positions_stand_so_an_old_loss_does_not_trip_it_again()
    {
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), LossPeriod = TimeSpan.FromDays(1) });
        OpenLong(h);
        PriceIs(h, "45000.00");

        Assert.Equal(TradingState.Reducing, h.Engine.TradingState);
        h.Engine.SetTradingState(TradingState.Active);

        // A day later the position is still 5,000 down, and that loss belongs to the period it happened in: the new
        // period opens where the position stands, so only what it loses from here counts against the new day.
        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromDays(1));
        PriceIs(h, "45000.00");

        Assert.Equal(TradingState.Active, h.Engine.TradingState);
        h.Submit(Adds("O-1"));
        Assert.Single(h.Forwarded);

        // And a fall of a further 1,000 inside the new period does stop it.
        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "44000.00");

        Assert.Equal(TradingState.Reducing, h.Engine.TradingState);
        Assert.Equal(2, h.Engine.LossLimitStoppedCount);
    }

    [Fact]
    public void A_bypassed_engine_watches_nothing()
    {
        RiskHarness h = new(new RiskEngineConfig { Bypass = true, Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand) } });
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 10_000m, 0m))));
        OpenLong(h);
        PriceIs(h, "40000.00");

        Assert.Equal(0, h.Engine.LossWatchCount);
        Assert.Equal(TradingState.Active, h.Engine.TradingState);
    }

    // Why: stopping is not closing. A node that stops itself holds what it held, and the account goes on losing on the
    // very position that breached the limit until a person closes it - which is half a kill switch. Flatten is the
    // other half, and because it closes someone's position without being asked at the moment it happens, it is never
    // a default and everything it does has to be visible.
    [Fact]
    public void Flatten_takes_the_book_off_the_venue_when_the_limit_is_reached()
    {
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.Flatten });
        OpenLong(h);
        LimitOrder resting = h.AddAccepted(TestOrders.Limit("O-RESTING", TestIds.BtcUsdt, OrderSide.Buy, "0.500", "48000.00"));

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48500.00");

        // Stopped, as StopTrading would, and then emptied: the resting order cancelled and the position sent a
        // closing order of exactly its size, reduce-only, so it can never open the other side.
        Assert.Equal(TradingState.Reducing, h.Engine.TradingState);
        Assert.Equal(1, h.Engine.LossLimitStoppedCount);
        Assert.Equal(1, h.Engine.LossLimitFlattenedCount);

        Assert.Single(h.Forwarded.OfType<CancelAllOrders>());
        SubmitOrder closing = Assert.Single(h.Forwarded.OfType<SubmitOrder>());
        Assert.Equal(OrderSide.Sell, closing.Order.Side);
        Assert.Equal(Quantity.Parse("1.000"), closing.Order.Quantity);
        Assert.True(closing.Order.IsReduceOnly, "the closing order could open a position");
        Assert.Equal(OrderType.Market, closing.Order.Type);
        Assert.Contains("LOSS_LIMIT_FLATTEN", closing.Order.Tags);
        Assert.Equal(new PositionId("P-OPEN"), closing.PositionId);
        Assert.Equal(resting.StrategyId, closing.StrategyId);
    }

    [Fact]
    public void Flatten_is_not_what_happens_unless_it_was_asked_for()
    {
        // The default stops and leaves the position alone: closing it is a decision a host makes, not a default.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand) });
        OpenLong(h);

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48500.00");

        Assert.Equal(TradingState.Reducing, h.Engine.TradingState);
        Assert.Equal(0, h.Engine.LossLimitFlattenedCount);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Flatten_closes_every_position_once_and_only_once()
    {
        // Two instruments, and a second price after the breach: the closing orders go out on the first breach and the
        // engine does not send them again - a position closed twice would open the other side.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.Flatten });
        h.Cache.AddInstrument(TestInstruments.EthUsdt());
        OpenLong(h);
        h.AddPosition(TestInstruments.EthUsdt(), OrderSide.Sell, "10.000", "3000.00", positionId: "P-ETH");

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48500.00");
        Assert.Equal(2, h.Engine.LossLimitFlattenedCount);

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48000.00");

        Assert.Equal(2, h.Engine.LossLimitFlattenedCount);
        Assert.Equal(2, h.Forwarded.OfType<SubmitOrder>().Count());
        Assert.Equal([OrderSide.Sell, OrderSide.Buy], h.Forwarded.OfType<SubmitOrder>().Select(c => c.Order.Side));
    }

    [Fact]
    public void A_reset_engine_has_closed_nothing()
    {
        // A count that survived a reset would be another run's, and the number of positions a limit closed is
        // something a report puts its name to.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.Flatten });
        OpenLong(h);
        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48500.00");
        Assert.Equal(1, h.Engine.LossLimitFlattenedCount);

        h.Engine.Reset();

        Assert.Equal(0, h.Engine.LossLimitFlattenedCount);
        Assert.Equal(0, h.Engine.LossLimitStoppedCount);
    }

    [Fact]
    public void A_closing_order_the_engine_sent_itself_is_not_denied_by_the_state_it_just_set()
    {
        // The orders go through this engine, and it has just put itself into Reducing: an engine that denied its own
        // closing orders would stop the node and leave the position open, which is the thing Flatten exists to fix.
        RiskHarness h = Harness(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), OnLossLimit = LossLimitBreach.Flatten });
        OpenLong(h);

        h.Clock.SetTime(h.Clock.Timestamp + TimeSpan.FromMinutes(1));
        PriceIs(h, "48500.00");

        Assert.Empty(h.Denied);
        Assert.Single(h.Forwarded.OfType<SubmitOrder>());
    }
}
