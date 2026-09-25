using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests;

// Why: a venue asked for more leverage than it grants does not fail. It grants what it will and trades on. So a
// strategy written, backtested and papered at 50x goes live at whatever the venue allowed - sized differently,
// liquidating at a different price - and nothing anywhere says the number changed.
//
// That is the same shape as a configuration field nothing reads: the request was accepted and quietly not honoured.
// It is worse here, because what is silently altered is the size of every position, and a result computed at the
// wrong leverage looks exactly like a result.
//
// So a run refuses to start, naming the venue's own figure and the one asked for. The half that makes this possible
// at all is that an instrument now carries the ceiling its venue publishes; where a venue publishes none, the
// ceiling is null, null means "the venue did not say" rather than "unlimited", and nothing is refused on a guess.
public sealed class LeverageGuardTests
{
    private static Instrument Capped(decimal? maxLeverage) => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT-PERP.SIM"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.0066m,
        MarginMaint = 0.0033m,
        MaxLeverage = maxLeverage,
    });

    [Fact]
    public void A_leverage_the_venue_grants_is_allowed()
    {
        LeverageGuard.EnsureGranted(20m, [Capped(150m)], "SIM");
        LeverageGuard.EnsureGranted(150m, [Capped(150m)], "SIM");
    }

    [Fact]
    public void A_leverage_above_what_the_venue_grants_is_refused_naming_both_figures()
    {
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => LeverageGuard.EnsureGranted(200m, [Capped(150m)], "SIM"));

        // Both numbers and the venue, because "leverage too high" is not something a person can act on.
        Assert.Contains("SIM", refused.Message, StringComparison.Ordinal);
        Assert.Contains("150", refused.Message, StringComparison.Ordinal);
        Assert.Contains("200", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_venue_that_publishes_no_ceiling_is_not_second_guessed()
    {
        // Null is "the venue did not say", which is the opposite of unlimited - but refusing on it would ground
        // every run on a venue that keeps its brackets behind a key, which is a capability lost to a guess.
        LeverageGuard.EnsureGranted(125m, [Capped(null)], "SIM");
    }

    [Fact]
    public void No_configured_leverage_means_nothing_to_check()
    {
        // Null leverage leaves the venue's own setting alone, so there is nothing being asked for and nothing to
        // refuse. Every configuration written before the field existed means this.
        LeverageGuard.EnsureGranted(null, [Capped(1m)], "SIM");
    }

    [Fact]
    public void One_instrument_that_grants_less_refuses_the_whole_run()
    {
        // A client trading several instruments applies one leverage to all of them, so the tightest ceiling decides.
        // Starting anyway would trade the rest at the asked-for figure and this one at less, which is the silent
        // mixture the guard exists to prevent.
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => LeverageGuard.EnsureGranted(100m, [Capped(150m), Capped(50m)], "SIM"));

        Assert.Contains("50", refused.Message, StringComparison.Ordinal);
    }

    // ----- and it is reached on a real venue -----

    [Fact]
    public async Task Bybit_refuses_to_connect_at_a_leverage_it_does_not_grant()
    {
        // The fixture's own leverageFilter grants 100x. Asking for 150x used to be sent, granted at 100x by the
        // venue, and traded - so the run now refuses instead, before the socket opens.
        await using BybitExecRig rig = new(BybitProductType.Linear, leverage: 150m);
        rig.Routes
            .On("POST", BybitVenue.LeveragePath, BybitPayloads.Envelope("{}"))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.LinearRiskLimits);

        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => rig.ConnectAsync("""{"list":[]}"""));

        Assert.Contains("100", refused.Message, StringComparison.Ordinal);

        // And nothing was sent: the refusal comes before the venue is asked to set anything.
        Assert.Empty(rig.Server.RequestsTo(BybitVenue.LeveragePath));
    }

    [Fact]
    public async Task Bybit_still_connects_at_a_leverage_it_does_grant()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear, leverage: 25m);
        rig.Routes
            .On("POST", BybitVenue.LeveragePath, BybitPayloads.Envelope("{}"))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.LinearRiskLimits);

        await rig.ConnectAsync("""{"list":[]}""");

        Assert.Single(rig.Server.RequestsTo(BybitVenue.LeveragePath));
    }

    // ----- and no venue may apply a leverage without checking it -----

    [Fact]
    public void Every_adapter_that_applies_a_leverage_checks_that_the_venue_grants_it()
    {
        // The half that keeps this true for venues nobody has written yet. An adapter reading the configured
        // leverage is an adapter that will send it, and sending it without this check is how the silent lowering
        // happens - so the rule is enforced by reading the source rather than by remembering.
        foreach (string venue in Repo.ShippedVenues())
        {
            string[] files = [.. Repo.SourceFiles(venue)];
            bool applies = files.Any(f => File.ReadAllText(f).Contains("_config.Leverage", StringComparison.Ordinal));
            if (!applies)
            {
                continue;
            }

            bool checks = files.Any(f => File.ReadAllText(f).Contains(nameof(LeverageGuard), StringComparison.Ordinal));

            Assert.True(
                checks,
                $"{venue} reads the configured leverage and never calls {nameof(LeverageGuard)}.{nameof(LeverageGuard.EnsureGranted)}. "
                + "A venue asked for more than it grants does not refuse - it grants less and trades on, so the "
                + "strategy runs at a size it was never tested at with nothing saying so.");
        }
    }
}
