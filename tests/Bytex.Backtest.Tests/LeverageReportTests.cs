using System.Text.Json;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: a LIVE run that asks for more leverage than the venue grants is refused, by LeverageGuard, naming both
// figures. A backtest is not refused - nothing calls that guard here - and it does not honour the request either.
// It CLAMPS: Instrument.InitialMarginRate takes the larger of 1/leverage and the instrument's own margin, so the
// margin is a floor and an instrument carrying 0.05 supports 20x however much was asked for.
//
// Measured on the live venue: Binance BTCUSDT-PERP without a credential comes back with marginInit 0.05, a null
// ceiling and marginSource venueWideDefault - the venue's public figure, which is a default for all 909 of its
// contracts rather than what any one of them requires. A strategy written for 50x is therefore measured at 20x:
// smaller positions, a different drawdown, and orders denied for margin that would have passed. The run completes,
// the numbers look like numbers, and nothing anywhere says the strategy that produced them is not the one that was
// written.
//
// The guard cannot catch it either, because a null ceiling means the venue published none, so there is nothing to
// refuse against. What closes it is the run saying what it did, which is what this file pins.
public sealed class LeverageReportTests
{
    /// <summary>
    /// The measured Binance case: the venue's public margin, which supports 20x, and no published ceiling.
    /// </summary>
    private static CryptoPerpetual Perp(decimal marginInit = 0.05m, MarginSource source = MarginSource.VenueWideDefault) =>
        new(new InstrumentSpec
        {
            Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), TestInstruments.Sim),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.001m, 3),
            MarginInit = marginInit,
            MarginMaint = marginInit / 2m,
            MakerFee = 0.0002m,
            TakerFee = 0.0005m,
            MarginSource = source,
        });

    private static BacktestResult Run(Instrument instrument, decimal leverage)
    {
        using SimHarness sim = SimHarness.For(instrument, new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(100_000m, Currencies.USDT)],
            DefaultLeverage = leverage,
        });

        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.1m))))
            .Quote(2000, 50_100.0m, 50_100.0m, size: 100m)
            .Run();

        return sim.Engine.GetResult();
    }

    [Fact]
    public void A_run_asked_for_fifty_and_says_it_used_twenty()
    {
        // The whole point, in one assertion: the figures a person compares.
        LeverageReportRow row = Assert.Single(Run(Perp(), leverage: 50m).Leverages);

        Assert.Equal(50m, row.Requested);
        Assert.Equal(20m, row.Applied);
        Assert.True(row.Capped, "a run that used 20 where 50 was asked for is capped, and the row has to say so");
    }

    [Fact]
    public void The_row_carries_what_capped_it_and_how_much_that_figure_is_worth()
    {
        // The margin alone would leave a reader with "20x, because 0.05" and no way to tell a measured requirement
        // from a placeholder. Both cap a run identically; only one is worth going to fetch the real figure for.
        LeverageReportRow row = Assert.Single(Run(Perp(), leverage: 50m).Leverages);

        Assert.Equal(0.05m, row.MarginInit);
        Assert.Equal(MarginSource.VenueWideDefault, row.MarginSource);

        // And the same cap from a figure the venue really published for this contract is a different claim, which
        // the row distinguishes without any other part of the result changing.
        LeverageReportRow measured = Assert.Single(Run(Perp(source: MarginSource.VenuePerContract), leverage: 50m).Leverages);

        Assert.Equal(row.Applied, measured.Applied);
        Assert.NotEqual(row.MarginSource, measured.MarginSource);
    }

    [Fact]
    public void A_leverage_the_instrument_leaves_room_for_is_reported_as_given()
    {
        // Not only the bad news. A row that says 10 and 10 is what lets a host show the applied leverage always,
        // rather than only when something went wrong - and it is how a reader tells "not capped" from "not reported".
        LeverageReportRow row = Assert.Single(Run(Perp(), leverage: 10m).Leverages);

        Assert.Equal(10m, row.Requested);
        Assert.Equal(10m, row.Applied);
        Assert.False(row.Capped);
    }

    [Fact]
    public void The_boundary_is_the_reciprocal_of_the_margin_and_is_not_called_a_cap()
    {
        // Exactly at the instrument's floor, nothing is lost: 1/0.05 is 20, and 20 asked for is 20 given. An
        // off-by-one here would report every run at its own limit as capped and teach people to ignore the field.
        LeverageReportRow row = Assert.Single(Run(Perp(), leverage: 20m).Leverages);

        Assert.Equal(20m, row.Applied);
        Assert.False(row.Capped);
    }

    [Fact]
    public void An_instrument_whose_venue_publishes_its_real_requirement_is_not_capped_at_all()
    {
        // The corrected figure, measured on KuCoin's XBTUSDT-PERP: 0.008 initial, which supports 125x. The same
        // strategy that was cut to 20x on a venue-wide default runs at the 50x it asked for here, and the row is
        // how a person sees that the difference was the FIGURE and not the strategy.
        LeverageReportRow row = Assert.Single(Run(Perp(marginInit: 0.008m, source: MarginSource.VenuePerContract), leverage: 50m).Leverages);

        Assert.Equal(50m, row.Applied);
        Assert.False(row.Capped);
    }

    [Fact]
    public void A_spot_run_that_borrows_nothing_reports_nothing()
    {
        // Leverage has nothing to say about a cash trade, and a row reading "1 and 1" beside every real one would
        // be noise that gets filtered out by eye - which is how the real rows stop being read.
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.1m))))
            .Quote(2000, 50_100.0m, 50_100.0m, size: 100m)
            .Run();

        Assert.Empty(sim.Engine.GetResult().Leverages);
    }

    [Fact]
    public void An_order_that_never_filled_is_still_reported()
    {
        // The case that decides orders rather than positions. The clamp binds when an order's margin is HELD, so a
        // run whose orders were all refused for margin is the one whose reader most needs this - and it has no
        // position to hang a row on. A rule keyed on positions would go silent at exactly that moment.
        using SimHarness sim = SimHarness.For(Perp(), new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(100_000m, Currencies.USDT)],
            DefaultLeverage = 50m,
        });

        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(0.1m), sim.Px(1_000.0m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        BacktestResult result = sim.Engine.GetResult();

        Assert.Empty(result.Positions);
        Assert.Equal(50m, Assert.Single(result.Leverages).Requested);
    }

    [Fact]
    public void The_report_reaches_the_file_a_host_reads_with_its_reason_intact()
    {
        // A host runs this engine as a child process and reads result.json; a field that stops at the type is a
        // field that does not exist. The margin source goes out as its name, so the row explains itself there too.
        using JsonDocument written = JsonDocument.Parse(ReportWriter.ToJson(Run(Perp(), leverage: 50m)));
        JsonElement row = written.RootElement.GetProperty("leverages")[0];

        Assert.Equal(50m, row.GetProperty("requested").GetDecimal());
        Assert.Equal(20m, row.GetProperty("applied").GetDecimal());
        Assert.True(row.GetProperty("capped").GetBoolean());
        Assert.Equal("venueWideDefault", row.GetProperty("marginSource").GetString());
    }
}
