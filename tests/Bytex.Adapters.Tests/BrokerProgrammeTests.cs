using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Orders;

namespace Bytex.Adapters.Tests;

// Why: an exchange broker programme pays a host a rebate on the trades it routes, with no custody and no access to
// anybody's keys, so the only question is which venues pay and what stands in the way on the ones that do not. That
// question was being answered by BrokerTag, which does not answer it.
//
// BrokerTag says how an adapter carries an id. Absent a mechanism it says None, and None was read as "this venue has
// no programme" - so KuCoin was declared, in an adapter and in a test and in the release checklist, as a venue with
// nothing to claim. It runs two broker tiers with rebate management. Its mechanism is a signed partner credential on
// every REST request: an id, a broker name, and a signature over the timestamp, the id and the API key made with a
// second secret the programme issues. Three values and a key, where a prefix or a header needs one string - which is
// why nothing here carries it, and why looking at BrokerTag was never going to reveal that.
//
// The distinction is not pedantic. The three "no" answers cost completely different things: a venue with no
// programme is nothing to pursue, a venue whose mechanism is published and uncarried is adapter work of a known
// size, and a venue that publishes its mechanism only to approved applicants cannot be built before somebody
// applies. Collapsing them into one enum member is how a rebate goes unclaimed while the declaration looks complete.
public sealed class BrokerProgrammeTests
{
    private const string Broker = "bytex-";

    /// <summary>
    /// Every shipped venue, and what it pays. Failing here when a venue is added is the point: a new adapter cannot
    /// default into "no programme", which is the one answer that stops anybody looking.
    /// </summary>
    private static readonly (string Venue, BrokerProgramme Programme)[] _programmes =
    [
        ("Binance", BrokerProgramme.Carried),
        ("Bybit", BrokerProgramme.Carried),
        ("Kucoin", BrokerProgramme.NotCarried),

        // Gate runs an API broker programme and discloses its mechanism only to approved applicants: the programme
        // page names an additional channel id and the API reference never mentions it, so there is nothing to carry
        // until somebody applies. That is a different "no" from KuCoin's, whose mechanism IS published.
        ("Gate", BrokerProgramme.MechanismUndisclosed),
    ];

    private static VenueDescriptor Describe(string venue) => venue switch
    {
        "Binance" => new BinancePlugin().Describe(),
        "Bybit" => new BybitPlugin().Describe(),
        "Kucoin" => new KucoinPlugin().Describe(),
        "Gate" => new GatePlugin().Describe(),
        _ => throw new ArgumentOutOfRangeException(nameof(venue), venue, "not a declared venue"),
    };

    [Fact]
    public void Every_venue_says_whether_it_pays_a_rebate_and_not_only_how_an_id_would_be_carried()
    {
        foreach ((string venue, BrokerProgramme expected) in _programmes)
        {
            Assert.Equal(expected, Describe(venue).BrokerProgramme);
        }
    }

    [Fact]
    public void A_venue_declaring_a_mechanism_is_a_venue_declaring_it_carries_one()
    {
        // The two fields cannot disagree. A venue that names a mechanism but claims not to carry a programme, or
        // claims to carry one while nothing tags an order, is a declaration that will be believed and is wrong.
        foreach ((string venue, _) in _programmes)
        {
            VenueDescriptor declared = Describe(venue);
            Assert.Equal(
                declared.BrokerProgramme == BrokerProgramme.Carried,
                declared.BrokerTag != BrokerTag.None);
        }
    }

    [Fact]
    public void A_venue_that_runs_a_programme_nothing_carries_is_a_rebate_going_unclaimed_not_a_venue_with_none()
    {
        // The correction this file exists for, pinned so it cannot quietly revert to the reading it had.
        VenueDescriptor kucoin = new KucoinPlugin().Describe();

        Assert.Equal(BrokerProgramme.NotCarried, kucoin.BrokerProgramme);
        Assert.NotEqual(BrokerProgramme.None, kucoin.BrokerProgramme);
    }

    [Fact]
    public void No_venue_shipped_so_far_claims_to_have_no_programme_at_all()
    {
        // Not a rule - an observation worth failing on. None of the three venues shipped has turned out to lack a
        // programme, so the next adapter declaring None is far more likely not to have looked than to have found a
        // venue that genuinely runs nothing.
        Assert.DoesNotContain(BrokerProgramme.None, _programmes.Select(p => Describe(p.Venue).BrokerProgramme));
    }

    // ----- and the id is reachable from where a host sets it -----

    [Fact]
    public async Task A_broker_id_reaches_the_venue_when_it_is_configured_the_way_a_host_configures_one()
    {
        // The half that decides whether any of this earns anything. Every other test builds the configuration in
        // code, and a host does not: it hands a trading node a client entry whose config is JSON, deserialised into
        // the type the factory declares. An id that works in code is still unreachable if it does not arrive under
        // the name a host would write - and nothing above an adapter would report that, because an untagged order
        // is a perfectly good order. It just pays nobody.
        await using BinanceExecRig rig = new(BinanceAccountType.Spot, (http, ws) => $$"""
            {
              "accountType": "spot",
              "apiKey": "{{BinanceExecRig.ApiKey}}",
              "apiSecret": "{{BinanceExecRig.ApiSecret}}",
              "baseUrlHttp": "{{http}}",
              "baseUrlWs": "{{ws}}",
              "recvWindowMs": 7000,
              "brokerId": "{{Broker}}"
            }
            """);

        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m));
        await rig.SubmitAsync(order);

        Assert.Equal(
            Broker + order.ClientOrderId.Value,
            rig.Server.RequestsTo("/api/v3/order").Last().Query("newClientOrderId"));
    }

    [Fact]
    public async Task A_leverage_reaches_the_venue_from_the_same_configuration()
    {
        // The other field added beside it, and the same risk: a strategy meant to run at 3x that silently runs at 1x
        // because the configuration key was never reachable is a position sized wrongly, not a failed request.
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures, (http, ws) => $$"""
            {
              "accountType": "usdMFutures",
              "apiKey": "{{BinanceExecRig.ApiKey}}",
              "apiSecret": "{{BinanceExecRig.ApiSecret}}",
              "baseUrlHttp": "{{http}}",
              "baseUrlWs": "{{ws}}",
              "recvWindowMs": 7000,
              "leverage": 3
            }
            """);

        rig.Routes.On("POST", "/fapi/v1/leverage", """{"symbol":"BTCUSDT","leverage":3}""");
        await rig.ConnectAsync("""{"assets":[{"asset":"USDT","availableBalance":"100000","walletBalance":"100000"}]}""");

        RecordedRequest applied = Assert.Single(rig.Server.RequestsTo("/fapi/v1/leverage"));
        Assert.Equal("3", applied.Query("leverage"));
        Assert.Equal("BTCUSDT", applied.Query("symbol"));
    }
}
