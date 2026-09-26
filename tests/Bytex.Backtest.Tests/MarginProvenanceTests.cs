using System.Text.Json;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: margin decides how much of an account a leveraged position ties up and the price at which the venue closes
// it, and until the venues were read two of the three supplied that figure from a constant in the adapter - 0.05 for
// every contract, where Bybit's own risk limits give 0.0066 on BTCUSDT. Every result produced in that time is a real
// result computed against an invented requirement, and nothing in it says so.
//
// That is a problem about STORED results, which is why it is not answered by having fixed the input. Somebody opens a
// saved report beside a fresh run of the same strategy, over the same period, on the same venue, and they disagree.
// From there a corrected input and a broken engine look identical, and the report holding the figure cannot tell them
// apart, because 0.05 read from a venue and 0.05 chosen by an adapter are the same number.
//
// So a run records where the margin of what it traded came from. A run made against an old catalog says "unrecorded",
// which is the honest answer and the one that explains the disagreement.
public sealed class MarginProvenanceTests
{
    private static CryptoPerpetual Perp(MarginSource source) => new(new InstrumentSpec
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
        MarginInit = 0.05m,
        MarginMaint = 0.025m,
        MakerFee = 0.0002m,
        TakerFee = 0.0005m,
        MarginSource = source,
    });

    private static SimOptions Margined => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(100_000m, Currencies.USDT)],
        DefaultLeverage = 20m,
    };

    private static BacktestResult RunHolding(Instrument instrument)
    {
        using SimHarness sim = SimHarness.For(instrument, Margined);
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.1m))))
            .Quote(2000, 50_100.0m, 50_100.0m, size: 100m)
            .Run();

        return sim.Engine.GetResult();
    }

    [Fact]
    public void A_run_says_where_the_margin_of_what_it_traded_came_from()
    {
        Assert.Equal([MarginSource.VenuePerContract], RunHolding(Perp(MarginSource.VenuePerContract)).MarginSources);
    }

    [Fact]
    public void A_run_on_a_catalog_written_before_this_existed_says_so_instead_of_claiming_a_venue()
    {
        // The case every existing installation is in, and the reason the default member means "nobody recorded it".
        // An instrument built by hand - here, and in a host, and in every catalog entry written before the marker -
        // carries no provenance, and the result has to say that rather than picking the most reassuring answer.
        Assert.Equal([MarginSource.Unrecorded], RunHolding(TestInstruments.Perp()).MarginSources);
    }

    [Fact]
    public void A_run_that_held_nothing_claims_nothing()
    {
        // Margin is a property of holding something. A run that never opened a position has no margin question to
        // answer, and an empty list says that - where naming the instrument it was given would imply the figure
        // mattered to numbers it never touched.
        using SimHarness sim = SimHarness.For(Perp(MarginSource.VenuePerContract), Margined);
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m).Run();

        Assert.Empty(sim.Engine.GetResult().MarginSources);
    }

    [Fact]
    public void Two_venues_disagreeing_about_their_own_figures_are_both_reported()
    {
        // A multi-venue run is where a single answer would be a lie. One venue publishing per contract and another
        // substituting a default is exactly the mixture a reader needs to see, so the result carries the set of what
        // was used rather than a verdict over it.
        BacktestResult result = RunHolding(Perp(MarginSource.VenueWideDefault));

        Assert.Equal([MarginSource.VenueWideDefault], result.MarginSources);
        Assert.NotEqual(result.MarginSources, RunHolding(Perp(MarginSource.VenuePerContract)).MarginSources);
    }

    [Fact]
    public void The_report_writes_it_as_the_name_a_host_reads()
    {
        // A host runs this engine as a child process and reads result.json, so the vocabulary is the contract. The
        // every-field guard in ReportsAndSerializationTests already fails if the field is missing from the file;
        // this is about what is IN it - a name rather than an ordinal, so that reordering the enum cannot silently
        // change what an old report says.
        using JsonDocument written = JsonDocument.Parse(ReportWriter.ToJson(RunHolding(Perp(MarginSource.VenuePerContract))));
        JsonElement sources = written.RootElement.GetProperty("marginSources");

        Assert.Equal(JsonValueKind.String, sources[0].ValueKind);
        Assert.Equal("venuePerContract", sources[0].GetString());
    }
}
