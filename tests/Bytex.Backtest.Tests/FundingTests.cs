using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: R8.15. A perpetual costs money to hold: every funding time, one side pays the other, and over a long backtest
// that is the difference between a strategy that works and one that only looked as though it did. The data type has
// been in the engine since 0.2 and the simulator never read it, so every perpetual backtest was free to hold. What is
// pinned here is the payment: which side pays, on what notional, in what currency, and that nothing happens when
// there is nothing to fund.
public sealed class FundingTests
{
    private const decimal Rate = 0.0001m;

    /// <summary>A venue that charges nothing to trade: what is being measured here is the funding payment.</summary>
    private static SimOptions NoFees => new() { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) };

    [Fact]
    public void A_long_pays_a_positive_rate()
    {
        // One contract at 50 000 with a rate of 0.01 %: 5 USDT out of the account.
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, Rate)
            .Run();

        FundingPayment payment = Assert.Single(sim.Exchange.FundingPayments);
        Assert.Equal(new Money(-5m, Currencies.USDT), payment.Amount);
        Assert.Equal(Rate, payment.Rate);
        Assert.Equal(1m, payment.SignedQuantity);
        Assert.Equal(100_000m - 5m, sim.Balance(Currencies.USDT));
    }

    [Fact]
    public void A_short_takes_a_positive_rate()
    {
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, Rate)
            .Run();

        Assert.Equal(new Money(5m, Currencies.USDT), Assert.Single(sim.Exchange.FundingPayments).Amount);
        Assert.Equal(100_000m + 5m, sim.Balance(Currencies.USDT));
    }

    [Fact]
    public void A_negative_rate_pays_the_long()
    {
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, -Rate)
            .Run();

        Assert.Equal(new Money(5m, Currencies.USDT), Assert.Single(sim.Exchange.FundingPayments).Amount);
    }

    [Fact]
    public void Funding_is_charged_on_what_the_position_is_worth_now_not_what_it_cost()
    {
        // Bought at 50 000, funded at 60 000: the payment is on 60 000, as a venue charges it.
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .Quote(2000, 60_000.0m, 60_000.0m)
            .Funding(2500, Rate)
            .Run();

        FundingPayment payment = Assert.Single(sim.Exchange.FundingPayments);
        Assert.Equal(new Money(-12m, Currencies.USDT), payment.Amount);
        Assert.Equal(sim.Px(60_000.0m), payment.Price);
    }

    [Fact]
    public void Every_funding_time_charges_again()
    {
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, Rate)
            .Funding(3500, Rate)
            .Funding(4500, Rate)
            .Run();

        Assert.Equal(3, sim.Exchange.FundingPayments.Count);
        Assert.Equal(100_000m - 15m, sim.Balance(Currencies.USDT));
    }

    [Fact]
    public void A_flat_account_pays_no_funding()
    {
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .Funding(1500, Rate)
            .At(2000, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2500, 50_000.0m, 50_000.0m)
            .At(3000, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(3500, 50_000.0m, 50_000.0m)
            .Funding(4000, Rate)
            .Run();

        // A rate before the position was opened and one after it was closed: a venue publishes them either way and
        // neither is anybody's to pay.
        Assert.Empty(sim.Exchange.FundingPayments);
        Assert.Equal(100_000m, sim.Balance(Currencies.USDT));
    }

    [Fact]
    public void A_rate_of_zero_is_not_a_payment()
    {
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, 0m)
            .Run();

        Assert.Empty(sim.Exchange.FundingPayments);
    }

    [Fact]
    public void An_inverse_perpetual_is_funded_in_the_currency_it_settles_in()
    {
        // 10 000 USD of contracts at 50 000 is 0.2 BTC; a rate of 0.01 % on it is 0.00002 BTC, and the account pays it
        // in bitcoin because that is what the contract settles in.
        using SimHarness sim = SimHarness.For(
            TestInstruments.InversePerp(),
            new SimOptions
            {
                AccountType = AccountType.Margin,
                StartingBalances = [new Money(1m, Currencies.BTC)],
                FeeModel = new FixedFeeModel(Money.Zero(Currencies.BTC)),
            });
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 10_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(10_000m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 10_000m)
            .Funding(2500, Rate)
            .Run();

        FundingPayment payment = Assert.Single(sim.Exchange.FundingPayments);
        Assert.Equal(Currencies.BTC, payment.Amount.Currency);
        Assert.Equal(-0.00002m, payment.Amount.Amount);
    }

    [Fact]
    public void A_run_with_no_rates_in_it_does_not_claim_it_charged_funding()
    {
        // The venue could have charged it - a margin account holding a perpetual - and nobody published a rate, so
        // nothing was charged. A product telling a user their result accounts for funding would be wrong.
        using SimHarness without = SimHarness.Perp(NoFees);
        without.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(without.Id, OrderSide.Buy, without.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        BacktestResult quiet = without.Engine.GetResult();
        Assert.Contains(SimulationCapabilities.Funding, quiet.Simulation);
        Assert.DoesNotContain(SimulationCapabilities.Funding, quiet.Applied);

        using SimHarness charged = SimHarness.Perp(NoFees);
        charged.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(charged.Id, OrderSide.Buy, charged.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, Rate)
            .Run();

        Assert.Contains(SimulationCapabilities.Funding, charged.Engine.GetResult().Applied);
    }

    [Fact]
    public void The_report_carries_every_payment()
    {
        // Funding that only moved a balance is funding nobody can account for: the report lists each payment with the
        // rate, the position and the price it was charged on.
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, Rate)
            .Funding(3500, -Rate)
            .Run();

        BacktestResult result = sim.Engine.GetResult();
        Assert.Equal(2, result.Funding.Count);
        Assert.Equal(-5m, result.Funding[0].Amount);
        Assert.Equal(5m, result.Funding[1].Amount);
        Assert.Equal(sim.Id, result.Funding[0].InstrumentId);
        Assert.Equal(Currencies.USDT, result.Funding[0].Currency);
        Assert.Contains("Rate", ReportWriter.ToCsv(result.Funding), StringComparison.Ordinal);
    }

    [Fact]
    public void A_spot_instrument_has_no_funding_to_pay()
    {
        // On a margin account, so that what refuses the payment is the instrument being spot rather than the account
        // being a cash one. A venue can publish a rate against anything; only a perpetual has funding.
        using SimHarness sim = SimHarness.For(
            TestInstruments.Spot(),
            new SimOptions
            {
                AccountType = AccountType.Margin,
                StartingBalances = [new Money(100_000m, Currencies.USDT)],
                FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            });
        sim.Quote(1000, 100.00m, 100.00m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.00m)
            .Funding(2500, Rate)
            .Run();

        Assert.Empty(sim.Exchange.FundingPayments);
        Assert.Equal(100_000m, sim.Balance(Currencies.USDT));
    }

    [Fact]
    public void The_account_is_told_what_funding_did_to_it()
    {
        // A balance that moved with no event behind it is a balance nobody can explain: the payment raises an account
        // state like every other movement, so a monitor and a report see it when it happens.
        using SimHarness sim = SimHarness.Perp(NoFees);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Funding(2500, Rate)
            .Run();

        AccountState state = sim.Events.OfType<AccountState>().Last();
        Assert.Equal(100_000m - 5m, state.Balances.Single(b => b.Currency == Currencies.USDT).Total.Amount);
    }
}
