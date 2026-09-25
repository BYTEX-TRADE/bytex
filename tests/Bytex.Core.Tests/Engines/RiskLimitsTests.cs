using System.Text.Json;
using Bytex.Core.Engines;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Timing;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: a host sets a node's limits from three places - the node's JSON configuration, the control channel while it
// runs, and a command line - and all three have to mean the same thing. So a limit is written and read as the text a
// person would type, the whole set travels as one value a host can send back, and the engine takes a new set without
// forgetting what the period has already lost.
public class RiskLimitsTests
{
    [Theory]
    [InlineData("1000 USDT")]
    [InlineData("1000.50 USDT")]
    [InlineData("0.5 BTC")]
    public void An_amount_is_written_back_as_the_money_it_is(string text)
    {
        RiskLimit limit = RiskLimit.Parse(text);

        Assert.Equal(Money.Parse(text).ToString(), limit.ToString());
        Assert.Equal(limit, RiskLimit.Parse(limit.ToString()));
    }

    [Theory]
    [InlineData("2%", "2%")]
    [InlineData(" 2.5 % ", "2.5%")]
    [InlineData("50%", "50%")]
    public void A_share_of_equity_is_written_back_as_a_percentage(string text, string expected)
    {
        RiskLimit limit = RiskLimit.Parse(text);

        Assert.Equal(expected, limit.ToString());
        Assert.Equal(limit, RiskLimit.Parse(limit.ToString()));
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("USDT")]
    [InlineData("%")]
    [InlineData("-5%")]
    [InlineData("0%")]
    [InlineData("0 USDT")]
    [InlineData("-1000 USDT")]
    public void What_is_not_a_limit_is_refused_rather_than_read_as_something_else(string text)
    {
        Assert.False(RiskLimit.TryParse(text, out RiskLimit? limit));
        Assert.Null(limit);
    }

    [Fact]
    public void An_amount_and_a_share_of_equity_are_told_apart()
    {
        RiskLimit amount = RiskLimit.Parse("1000 USDT");
        RiskLimit share = RiskLimit.Parse("2%");
        Money equity = new(50_000m, Currencies.USDT);

        Assert.Equal(new Money(1_000m, Currencies.USDT), amount.In(Currencies.USDT, equity));
        Assert.Null(amount.In(Currencies.BTC, equity));
        Assert.Equal(new Money(1_000m, Currencies.USDT), share.In(Currencies.USDT, equity));
        Assert.Null(share.In(Currencies.USDT, null));
    }

    [Fact]
    public void The_limits_of_a_node_survive_the_round_trip_through_its_configuration()
    {
        // What a host writes in a node's JSON, as the documentation shows it.
        const string json = """
            {
              "limits": {
                "maxLossPerPeriod": "1000 USDT",
                "lossPeriod": "06:00:00",
                "maxExposure": "50%",
                "maxOpenPositionsPerInstrument": 1,
                "maxOpenPositions": 5,
                "maxWorkingOrdersPerInstrument": 10,
                "maxWorkingOrders": 50
              }
            }
            """;

        RiskEngineConfig config = JsonSerializer.Deserialize<RiskEngineConfig>(json, BytexJson.Options)!;

        Assert.Equal(new Money(1_000m, Currencies.USDT), config.Limits.MaxLossPerPeriod!.Amount);
        Assert.Equal(TimeSpan.FromHours(6), config.Limits.LossPeriod);
        Assert.Equal(50m, config.Limits.MaxExposure!.Percent);
        Assert.Equal(1, config.Limits.MaxOpenPositionsPerInstrument);
        Assert.Equal(5, config.Limits.MaxOpenPositions);
        Assert.Equal(10, config.Limits.MaxWorkingOrdersPerInstrument);
        Assert.Equal(50, config.Limits.MaxWorkingOrders);

        // And back out again, because a host reads a node's limits before it changes one of them.
        RiskLimits again = JsonSerializer.Deserialize<RiskLimits>(JsonSerializer.Serialize(config.Limits, BytexJson.Options), BytexJson.Options)!;
        Assert.Equal(config.Limits, again);
    }

    [Fact]
    public void A_kernel_carries_the_limits_its_configuration_names()
    {
        RiskLimits limits = new() { MaxWorkingOrders = 3, MaxLossPerPeriod = RiskLimit.Parse("1000 USDT") };

        using Core.Kernel.Kernel kernel = new(new KernelConfig { RiskEngine = new RiskEngineConfig { Limits = limits } }, new TestClock(TestOrders.T0));

        Assert.Equal(limits, kernel.RiskEngine.Limits);
    }

    [Fact]
    public void Nothing_is_enforced_until_something_is_set()
    {
        RiskLimits limits = new();

        Assert.True(limits.IsEmpty);
        Assert.False((limits with { MaxWorkingOrders = 1 }).IsEmpty);
        Assert.False((limits with { MaxLossPerPeriod = RiskLimit.Parse("1 USDT") }).IsEmpty);
        Assert.True((limits with { LossPeriod = TimeSpan.FromHours(1) }).IsEmpty, "a window with no limit in it enforces nothing");
    }

    [Fact]
    public void An_engine_takes_new_limits_while_it_runs()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 1_000_000m, 0m))));
        h.AddAccepted(TestOrders.Limit("O-RESTING", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));
        Assert.Single(h.Forwarded);

        h.Engine.SetLimits(new RiskLimits { MaxWorkingOrders = 1 });
        h.Submit(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        Assert.Single(h.Forwarded);
        Assert.StartsWith("ORDER_CAP", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
        Assert.Equal(1, h.Engine.Limits.MaxWorkingOrders);
    }

    [Fact]
    public void A_limit_set_during_a_period_is_measured_against_the_whole_of_it()
    {
        // The account lost 1,200 with no limit in force. A limit set after that is about this period, and this period
        // has lost the 1,200: a host that sets a limit at noon does not get a fresh day out of it.
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 1_000_000m, 0m))));
        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));
        Lose(h, 1_200m);

        h.Engine.SetLimits(new RiskLimits { MaxLossPerPeriod = RiskLimit.Parse("1000 USDT") });
        h.Submit(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "0.010", "50000.00"));

        Assert.StartsWith("LOSS_LIMIT", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_engine_enforces_what_the_node_was_built_with_again()
    {
        // A reset puts a component back to how it was created, so the limits a host had set over the channel go with
        // it: what the node was configured with is what it enforces again.
        RiskHarness h = new(new RiskEngineConfig { Limits = new RiskLimits { MaxWorkingOrders = 5 } });
        h.Engine.SetLimits(new RiskLimits { MaxWorkingOrders = 1 });
        Assert.Equal(1, h.Engine.Limits.MaxWorkingOrders);

        h.Engine.Reset();

        Assert.Equal(5, h.Engine.Limits.MaxWorkingOrders);
    }

    /// <summary>A position opened and closed at a worse price, which is the only way a realised loss comes about.</summary>
    private static void Lose(RiskHarness h, decimal amount)
    {
        Instrument instrument = TestInstruments.BtcUsdt();
        MarketOrder opening = TestOrders.Market("O-OPEN", instrument.Id, OrderSide.Buy, "1.000");
        MarketOrder closing = TestOrders.Market("O-CLOSE", instrument.Id, OrderSide.Sell, "1.000");
        Position position = new(instrument, TestEvents.Filled(opening, "T-OPEN", "1.000", "50000.00", positionId: new PositionId("P-1")));
        position.Apply(TestEvents.Filled(closing, "T-CLOSE", "1.000", (50_000m - amount).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), positionId: new PositionId("P-1")));
        h.Cache.AddPosition(position);
    }
}
