using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.8 and R4.9 - a day that has lost more than the account can afford, and a book bigger than it can carry, are
// the two ways an automated strategy does real damage, and the engine judged neither: it looked at one order at a time
// and never at what the account had already done. These limits are the account-wide ones, so what is pinned here is
// where they are measured from (the portfolio, at the start of the period the clock is in), what they let through no
// matter what (anything that reduces), and that they say which limit stopped an order and by how much.
public class RiskEngineLimitsTests
{
    private static readonly Money Thousand = new(1_000m, Currencies.USDT);

    private static RiskHarness Harness(RiskEngineConfig config, decimal equity = 10_000m, UnixNanos? now = null)
    {
        RiskHarness h = new(config, now);
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, equity, 0m), (Currencies.BTC, 10m, 0m))));
        return h;
    }

    /// <summary>A position opened and closed again, which is the only way realised PnL comes about.</summary>
    private static void Realize(RiskHarness h, decimal pnl, string positionId = "P-1")
    {
        Instrument instrument = TestInstruments.BtcUsdt();
        const decimal quantity = 1m;
        MarketOrder opening = TestOrders.Market("O-OPEN-" + positionId, instrument.Id, OrderSide.Buy, "1.000");
        MarketOrder closing = TestOrders.Market("O-CLOSE-" + positionId, instrument.Id, OrderSide.Sell, "1.000");
        Position position = new(instrument, TestEvents.Filled(opening, "T-OPEN-" + positionId, "1.000", "50000.00", positionId: new PositionId(positionId)));
        position.Apply(TestEvents.Filled(closing, "T-CLOSE-" + positionId, "1.000", Price.Parse(((50_000m * quantity) + pnl).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).ToString(), positionId: new PositionId(positionId)));
        h.Cache.AddPosition(position);
        Assert.Equal(pnl, position.RealizedPnl.Amount);
    }

    /// <summary>Puts a price in the cache, which is what an open position's exposure is measured at.</summary>
    private static void Price50k(RiskHarness h) =>
        h.Cache.AddQuoteTick(new QuoteTick(TestIds.BtcUsdt, Price.Parse("49999.00"), Price.Parse("50001.00"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0));

    private static OrderDenied AssertDenied(RiskHarness h, string code)
    {
        Assert.Empty(h.Forwarded);
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.StartsWith(code, denied.Reason, StringComparison.Ordinal);
        return denied;
    }

    [Theory]
    [InlineData(-900, true)]
    [InlineData(-1000, false)] // the limit is reached, not merely passed
    [InlineData(-1200, false)]
    public void A_period_that_has_lost_its_limit_denies_what_would_add(decimal realized, bool allowed)
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand) } });
        Realize(h, realized);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        if (allowed)
        {
            Assert.Single(h.Forwarded);
            Assert.Equal(0, h.Engine.LossLimitDeniedCount);
        }
        else
        {
            OrderDenied denied = AssertDenied(h, "LOSS_LIMIT");
            Assert.Contains(new Money(-realized, Currencies.USDT).ToString(), denied.Reason, StringComparison.Ordinal);
            Assert.Contains(Thousand.ToString(), denied.Reason, StringComparison.Ordinal);
            Assert.Equal(1, h.Engine.LossLimitDeniedCount);
            Assert.Equal(1, h.Engine.DeniedCount);
        }
    }

    [Fact]
    public void A_limit_never_stops_an_order_getting_out()
    {
        // The day is past its loss limit and the account is over its exposure limit, and both of these still go: one
        // says reduce-only, the other is the other side of a position the account holds.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), MaxExposure = RiskLimit.Of(Thousand) } });
        Realize(h, -5_000m);
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "1.000", "50000.00", positionId: "P-OPEN");
        Price50k(h);

        h.Submit(TestOrders.Market("O-REDUCE-ONLY", TestIds.BtcUsdt, OrderSide.Buy, "0.010", reduceOnly: true));
        h.Submit(TestOrders.Limit("O-CLOSING", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "50000.00"));

        Assert.Equal(2, h.Forwarded.Count);
        Assert.Empty(h.Denied);
        Assert.Equal(0, h.Engine.LossLimitDeniedCount);
        Assert.Equal(0, h.Engine.ExposureLimitDeniedCount);
    }

    [Fact]
    public void The_loss_is_measured_from_where_the_period_opened()
    {
        // The account lost 1,200 before this period: the limit is about what the period loses, so the period that
        // opens over that loss starts level, and the same order it denied a moment ago goes through. Told only to
        // deny, so that what is being measured here is where the loss is counted from and nothing else: a limit that
        // stops the engine trading is the subject of RiskEngineLossWatchTests.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand), LossPeriod = TimeSpan.FromDays(1), OnLossLimit = LossLimitBreach.DenyAdds } });
        Realize(h, -1_200m);
        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));
        AssertDenied(h, "LOSS_LIMIT");

        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromDays(1));
        h.Submit(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        Assert.Single(h.Forwarded);

        // And a loss inside the new period is counted from its own mark, not from zero.
        Realize(h, -1_000m, positionId: "P-2");
        h.Submit(TestOrders.Limit("O-3", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));
        Assert.Equal(2, h.Engine.LossLimitDeniedCount);
    }

    [Theory]
    [InlineData(-400, true)]
    [InlineData(-600, false)]
    public void A_limit_set_as_a_percentage_is_measured_against_the_accounts_equity(decimal realized, bool allowed)
    {
        // Five percent of 10,000 is 500.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.PercentOfEquity(5m) } }, equity: 10_000m);
        Realize(h, realized);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        if (allowed)
        {
            Assert.Single(h.Forwarded);
        }
        else
        {
            Assert.Contains(new Money(500m, Currencies.USDT).ToString(), AssertDenied(h, "LOSS_LIMIT").Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_limit_in_a_currency_the_figure_is_not_in_judges_nothing()
    {
        // The position's PnL and the account's exposure are in USDT; a limit set in BTC has nothing to say about
        // either, and saying nothing is better than comparing numbers of different things.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(new Money(0.1m, Currencies.BTC)), MaxExposure = RiskLimit.Of(new Money(0.1m, Currencies.BTC)) } });
        Realize(h, -5_000m);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.100", "50000.00"));

        Assert.Single(h.Forwarded);
        Assert.Empty(h.Denied);
    }

    [Theory]
    [InlineData("0.019", true)] // 950 of the 1,000 allowed
    [InlineData("0.021", false)] // 1,050
    public void Exposure_counts_the_order_being_judged(string quantity, bool allowed)
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxExposure = RiskLimit.Of(Thousand) } });

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, quantity, "50000.00"));

        if (allowed)
        {
            Assert.Single(h.Forwarded);
            Assert.Equal(0, h.Engine.ExposureLimitDeniedCount);
        }
        else
        {
            Assert.Contains("1050", AssertDenied(h, "EXPOSURE_LIMIT").Reason, StringComparison.Ordinal);
            Assert.Equal(1, h.Engine.ExposureLimitDeniedCount);
        }
    }

    [Fact]
    public void Exposure_counts_what_the_account_already_carries()
    {
        // A position worth 500 at the market, and an order for another 600: neither is over the limit on its own.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxExposure = RiskLimit.Of(Thousand) } });
        h.AddPosition(TestInstruments.BtcUsdt(), OrderSide.Buy, "0.010", "50000.00");
        Price50k(h);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.012", "50000.00"));

        AssertDenied(h, "EXPOSURE_LIMIT");
    }

    [Fact]
    public void A_list_cannot_walk_past_the_exposure_limit_one_order_at_a_time()
    {
        // Three orders of 400 each: any one of them fits under the limit of 1,000, and the three of them do not.
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxExposure = RiskLimit.Of(Thousand) } });

        h.SubmitList(
            TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.008", "50000.00"),
            TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "0.008", "50000.00"),
            TestOrders.Limit("O-3", TestIds.BtcUsdt, OrderSide.Buy, "0.008", "50000.00"));

        Assert.Empty(h.Forwarded);
        Assert.Equal(3, h.Denied.Count);
        Assert.All(h.Denied, d => Assert.StartsWith("EXPOSURE_LIMIT", d.Reason, StringComparison.Ordinal));
        Assert.Equal(1, h.Engine.ExposureLimitDeniedCount);
    }

    [Fact]
    public void An_engine_told_of_no_limits_has_none()
    {
        RiskHarness h = Harness(new RiskEngineConfig());
        Realize(h, -500_000m);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.100", "50000.00"));

        Assert.Single(h.Forwarded);
        Assert.Null(h.Engine.Limits.MaxLossPerPeriod);
        Assert.Null(h.Engine.Limits.MaxExposure);
    }

    [Fact]
    public void A_reset_engine_starts_the_period_and_the_counters_again()
    {
        RiskHarness h = Harness(new RiskEngineConfig { Limits = new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(Thousand) } });
        Realize(h, -1_200m);
        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));
        Assert.Equal(1, h.Engine.LossLimitDeniedCount);

        h.Engine.Reset();
        Assert.Equal(0, h.Engine.LossLimitDeniedCount);
        Assert.Equal(0, h.Engine.DeniedCount);

        h.Forwarded.Clear();
        h.Events.Clear();
        h.Submit(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        // A reset engine judges as a fresh one does - it kept no count and no mark - so the loss on the books is this
        // period's again and the order is denied again.
        AssertDenied(h, "LOSS_LIMIT");
        Assert.Equal(1, h.Engine.LossLimitDeniedCount);
    }

    [Fact]
    public void A_limit_that_is_not_a_limit_is_refused_when_it_is_written()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskLimit.Of(new Money(0m, Currencies.USDT)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskLimit.Of(new Money(-1m, Currencies.USDT)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskLimit.PercentOfEquity(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskLimit.PercentOfEquity(-5m));
    }
}
