using Bytex.Adapters.Kraken;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kraken;

// Why: Kraken disagrees with itself about how a pair is spelled, and the disagreement is silent in both directions.
// Measured against the live venue on 2026-09-25, whose public endpoints need no key:
//
//   /0/public/AssetPairs publishes, for bitcoin against the dollar:
//     the key          XXBTZUSD
//     altname          XBTUSD
//     wsname           XBT/USD
//     base / quote     XXBT / ZUSD
//
//   and of those five spellings:
//     XBTUSD           accepted by REST
//     XXBTZUSD         accepted by REST
//     BTC/USD          accepted by REST *and* by the socket - and published by the socket's own catalog
//     XBT/USD          REFUSED by REST ("EQuery:Unknown asset pair") and by the socket
//                      ("Currency pair not supported XBT/USD")
//     XBT, XXBT        not asset codes the socket knows at all
//
// So the venue's own `wsname` field - the one whose name says it is the socket's name - is a name nothing on the
// venue answers to. The same is true of dogecoin (XDG against DOGE) and of nothing else: applying those two
// substitutions to all 1451 wsnames reproduced the socket's list of 1451 symbols with zero mismatches.
//
// That decides the raw symbol, and it is not an alias: BTC is what the venue accepts and what the venue's own
// instrument channel publishes, and on the FUTURES platform bitcoin really is XBT in a contract's symbol and stays
// XBT there untouched. Two spellings on one venue, each taken from the platform that uses it.
public sealed class KrakenTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTC-USD.KRAKEN");

    private static KrakenHttp Http(LoopbackServer server) =>
        new(new KrakenDataClientConfig { BaseUrlHttp = server.HttpBase });

    private static Routes Catalog() => new Routes().On("GET", "/0/public/AssetPairs", KrakenPayloads.AssetPairs);

    private static async Task<KrakenInstrumentProvider> LoadedAsync(LoopbackServer server)
    {
        KrakenInstrumentProvider provider = new(Http(server));
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- what the venue calls a pair, and what the engine calls it -----

    [Theory]
    [InlineData("XBT/USD", "BTC/USD")]
    [InlineData("XDG/USD", "DOGE/USD")]
    [InlineData("ETH/XBT", "ETH/BTC")]
    [InlineData("XDG/XBT", "DOGE/BTC")]
    [InlineData("SOL/USD", "SOL/USD")]
    [InlineData("EUR/USD", "EUR/USD")]
    public void The_name_a_request_takes_is_the_catalogs_wsname_with_two_dead_codes_corrected(string wsName, string wire)
    {
        // Checked against all 1451 pairs the venue lists: substituting these two tokens into every wsname produced
        // exactly the socket's own list of symbols, with nothing left over on either side. The pairs that need no
        // substitution pass through untouched, which is the half that stops the rule being applied too widely.
        Assert.Equal(wire, KrakenVenue.ToWireSymbol(wsName));
    }

    [Fact]
    public void Only_the_two_codes_the_venue_has_stopped_answering_to_are_corrected()
    {
        // The rule is two entries and has to stay two entries. Anything else here would be this engine renaming a
        // currency, which is not what this is: these are the two the venue itself refuses under their old names.
        Assert.Equal(["XBT", "XDG"], KrakenVenue.LegacyAssetCodes.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("BTC", KrakenVenue.Current("XBT"));
        Assert.Equal("DOGE", KrakenVenue.Current("XDG"));

        // And an asset the venue has never renamed is returned as it is, not looked up and defaulted.
        Assert.Equal("SOL", KrakenVenue.Current("SOL"));
        Assert.Equal("USD", KrakenVenue.Current("USD"));
    }

    [Theory]
    [InlineData("BTC/USD", "BTC-USD.KRAKEN")]
    [InlineData("DOGE/USD", "DOGE-USD.KRAKEN")]
    [InlineData("ETH/BTC", "ETH-BTC.KRAKEN")]
    public void A_pair_and_its_instrument_id_map_to_each_other_without_a_lookup(string wire, string id)
    {
        // A dash where the wire name has a slash, and nothing else. The venue's 674 asset codes contain neither
        // character, so the conversion is exact in both directions - and the 1451 dashed ids it produces are all
        // distinct, checked. A mapping that needed the catalog would mean an id could not be turned into a request
        // before the catalog had loaded.
        Assert.Equal(id, KrakenVenue.ToInstrumentId(wire).ToString());
        Assert.Equal(wire, KrakenVenue.ToRawSymbol(InstrumentId.Parse(id)));
    }

    [Fact]
    public void A_slash_never_reaches_an_instrument_id()
    {
        // Why the separator is swapped at all. An id is written into a stored history file's name, a log line and a
        // URL, and a slash is a path separator in all three - so the wire name stays on the wire.
        Assert.DoesNotContain("/", KrakenVenue.ToInstrumentId("BTC/USD").Symbol.Value, StringComparison.Ordinal);
        Assert.Equal("BTC-USD", KrakenVenue.ToInstrumentId("BTC/USD").Symbol.Value);
    }

    [Fact]
    public void Spot_ids_are_not_futures_ids()
    {
        // The venue writes bitcoin as BTC on the platform that accepts BTC and as XBT in a futures contract's
        // symbol, so anything holding an id has to have got it from the right family - and neither spelling is
        // this engine's invention.
        Assert.Equal("BTC-USD.KRAKEN", KrakenVenue.ToInstrumentId("BTC/USD").ToString());
        Assert.Equal("PF_XBTUSD.KRAKEN", KrakenFuturesVenue.ToInstrumentId("PF_XBTUSD").ToString());
    }

    // ----- the instrument -----

    [Fact]
    public async Task A_pair_is_published_under_the_name_both_the_rest_api_and_the_socket_accept()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenInstrumentProvider provider = await LoadedAsync(server);

        CurrencyPair btc = Assert.IsType<CurrencyPair>(provider.Find(_btc));

        // The raw symbol is what goes on the wire, and it is the spelling the venue's legacy field does NOT carry.
        Assert.Equal("BTC/USD", btc.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Spot, btc.InstrumentClass);
        Assert.Equal("BTC", btc.BaseCurrency.Code);
        Assert.Equal("USD", btc.QuoteCurrency.Code);
        Assert.Equal("USD", btc.SettlementCurrency.Code);

        // tick_size is the price increment and pair_decimals its precision; lot_decimals is the SIZE precision and
        // the venue publishes no size increment at all, so the increment is derived from the decimals. Confirmed
        // against the socket's instrument channel, which publishes qty_increment 1e-8 for lot_decimals 8.
        Assert.Equal(new Price(0.1m, 1), btc.PriceIncrement);
        Assert.Equal(new Quantity(0.00000001m, 8), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.00005m, 8), btc.MinQuantity);
        Assert.Equal(new Money(0.5m, Currency.FromCode("USD")), btc.MinNotional);
    }

    [Fact]
    public async Task A_pair_whose_legacy_code_is_the_quote_is_corrected_on_that_side_too()
    {
        // ETH/XBT in the catalog. A rule applied only to the base would leave the quote as a currency the venue
        // does not answer to, and the instrument would be unsubscribable while looking perfectly well formed.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenInstrumentProvider provider = await LoadedAsync(server);

        CurrencyPair eth = Assert.IsType<CurrencyPair>(provider.Find(InstrumentId.Parse("ETH-BTC.KRAKEN")));

        Assert.Equal("ETH/BTC", eth.RawSymbol.Value);
        Assert.Equal("ETH", eth.BaseCurrency.Code);
        Assert.Equal("BTC", eth.QuoteCurrency.Code);
    }

    [Fact]
    public async Task A_pair_that_needs_no_correction_is_published_exactly_as_the_venue_names_it()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenInstrumentProvider provider = await LoadedAsync(server);

        CurrencyPair sol = Assert.IsType<CurrencyPair>(provider.Find(InstrumentId.Parse("SOL-USD.KRAKEN")));

        Assert.Equal("SOL/USD", sol.RawSymbol.Value);
        Assert.Equal(new Price(0.01m, 2), sol.PriceIncrement);
        Assert.Equal(new Quantity(0.06m, 8), sol.MinQuantity);
    }

    [Fact]
    public async Task Every_pair_the_venue_lists_but_cannot_take_an_order_on_is_left_out()
    {
        // The venue lists 1354 pairs as online, 80 as cancel-only and 17 as post-only. A cancel-only pair cannot
        // take any order and a post-only one cannot take a market order, so publishing them would put instruments
        // in a picker that refuse what a strategy sends.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenInstrumentProvider provider = await LoadedAsync(server);

        Assert.Equal(
            ["BTC-USD.KRAKEN", "DOGE-USD.KRAKEN", "ETH-BTC.KRAKEN", "SOL-USD.KRAKEN"],
            provider.GetAll().Select(i => i.Id.ToString()).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_pair_is_published_with_the_schedule_rate_because_the_venue_publishes_no_rate_of_its_own()
    {
        // Measured: the `fees` and `fees_maker` arrays the venue documents per pair are EMPTY on all 1451 pairs it
        // lists, and on the ?info=fees variant as well. There is nothing to read, so an instrument carries the
        // published tier-zero rate - and the declaration says the same, out of the same two constants, so the two
        // cannot drift.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenInstrumentProvider provider = await LoadedAsync(server);

        Instrument btc = provider.Find(_btc)!;
        Assert.Equal(KrakenVenue.DefaultMakerFee, btc.MakerFee);
        Assert.Equal(KrakenVenue.DefaultTakerFee, btc.TakerFee);

        VenueFamily spot = new KrakenPlugin().Describe().Families.Single(f => f.Name == "spot");
        Assert.Equal(btc.MakerFee, spot.DefaultFees.Maker);
        Assert.Equal(btc.TakerFee, spot.DefaultFees.Taker);
    }

    [Fact]
    public async Task Loading_one_pair_loads_one()
    {
        // This endpoint honours its filter today, which its sibling platform's does not - so the provider keeps
        // only what was asked for rather than relying on the venue to have filtered.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenInstrumentProvider provider = new(Http(server));

        await provider.LoadAsync(_btc, CancellationToken.None);

        Instrument loaded = Assert.Single(provider.GetAll());
        Assert.Equal(_btc, loaded.Id);
        Assert.Equal("BTC/USD", server.RequestsTo("/0/public/AssetPairs").Single().Query("pair"));
    }

    // ----- candles the venue keeps -----

    [Theory]
    [InlineData("1-MINUTE", 1)]
    [InlineData("5-MINUTE", 5)]
    [InlineData("15-MINUTE", 15)]
    [InlineData("30-MINUTE", 30)]
    [InlineData("1-HOUR", 60)]
    [InlineData("4-HOUR", 240)]
    [InlineData("1-DAY", 1440)]
    [InlineData("1-WEEK", 10080)]
    public void The_bar_lengths_this_platform_keeps_are_the_ones_it_answered_for(string spec, int minutes)
    {
        // Probed one by one against the live endpoint. Each of these returned rows; 3, 120 and 720 minutes were
        // refused with "EGeneral:Invalid arguments" rather than rounded to something near them.
        BarType barType = BarType.Parse($"BTC-USD.KRAKEN-{spec}-LAST-EXTERNAL");
        Assert.Equal(minutes, KrakenVenue.Interval(barType.Spec));
    }

    [Theory]
    [InlineData("3-MINUTE")]
    [InlineData("2-HOUR")]
    [InlineData("12-HOUR")]
    [InlineData("1-MONTH")]
    public void A_bar_length_this_platform_does_not_keep_is_refused_rather_than_rounded(string spec)
    {
        // The venue refuses these outright, so asking it for one would fail anyway. Refusing here names the lengths
        // it does keep, which is a sentence somebody can act on rather than "Invalid arguments".
        BarType barType = BarType.Parse($"BTC-USD.KRAKEN-{spec}-LAST-EXTERNAL");
        NotSupportedException refused = Assert.Throws<NotSupportedException>(() => KrakenVenue.Interval(barType.Spec));
        Assert.Contains("4 hours", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_this_platform_serves_is_a_hard_limit_and_not_a_paging_size()
    {
        // 720, and the number matters less than what kind of number it is. Measured: `since=0` on one-minute
        // candles returned the newest 720, and `since` set 2000 minutes back returned THE SAME newest 720. There is
        // no parameter that reaches further back, so this is the whole of what the endpoint will ever serve.
        Assert.Equal(720, KrakenVenue.CandlePage);
    }

    // ----- where it talks -----

    [Fact]
    public void The_private_socket_is_a_different_host_and_is_derived_from_the_public_one()
    {
        // The venue serves public market data and private data on two hosts and refuses each on the other's, in as
        // many words. Only one can be declared, so the private one is built from it - which means a host pointing
        // the adapter at a proxy moves both halves rather than one.
        KrakenDataClientConfig live = new();
        Assert.Equal("wss://ws.kraken.com", KrakenVenue.WsBase(live));
        Assert.Equal(KrakenVenue.PrivateWsHost, KrakenVenue.PrivateWsAddress(live).Host);
        Assert.Equal(KrakenVenue.WsVersion, KrakenVenue.PrivateWsAddress(live).AbsolutePath);
    }

    [Fact]
    public void A_loopback_socket_base_is_not_rewritten()
    {
        // A recording is one server serving both halves. Substituting the venue's private host into 127.0.0.1 would
        // point the execution client at a machine that is not there, and every test of it would hang rather than
        // fail.
        KrakenDataClientConfig recorded = new() { BaseUrlWs = "ws://127.0.0.1:8080" };
        Assert.Equal("127.0.0.1", KrakenVenue.PrivateWsAddress(recorded).Host);
    }

    [Fact]
    public void The_two_platforms_answer_on_different_hosts()
    {
        Assert.Equal("https://api.kraken.com", KrakenVenue.HttpBase(new KrakenDataClientConfig()));
        Assert.Equal(
            "https://futures.kraken.com",
            KrakenVenue.HttpBase(new KrakenDataClientConfig { ProductType = KrakenProductType.Futures }));
    }

    [Fact]
    public void The_factory_picks_the_client_the_configuration_asks_for()
    {
        using TestKernel kernel = new();
        KrakenDataClientFactory data = new();
        KrakenExecutionClientFactory execution = new();

        KrakenDataClient spotData = Assert.IsType<KrakenDataClient>(
            data.Create(new ClientId("KRAKEN"), new KrakenDataClientConfig(), kernel.Services));
        spotData.Dispose();

        KrakenFuturesDataClient futuresData = Assert.IsType<KrakenFuturesDataClient>(data.Create(
            new ClientId("KRAKEN"),
            new KrakenDataClientConfig { ProductType = KrakenProductType.Futures },
            kernel.Services));
        futuresData.Dispose();

        KrakenExecutionClient spotExec = Assert.IsType<KrakenExecutionClient>(execution.Create(
            new ClientId("KRAKEN"),
            new KrakenExecutionClientConfig { ApiKey = "k", ApiSecret = Secret },
            kernel.Services));
        spotExec.Dispose();

        KrakenFuturesExecutionClient futuresExec = Assert.IsType<KrakenFuturesExecutionClient>(execution.Create(
            new ClientId("KRAKEN"),
            new KrakenExecutionClientConfig { ProductType = KrakenProductType.Futures, ApiKey = "k", ApiSecret = Secret },
            kernel.Services));
        futuresExec.Dispose();
    }

    [Fact]
    public void A_client_handed_the_other_platforms_configuration_says_so()
    {
        // The clients answer for different hosts, different signing and different message shapes, so a spot
        // configuration in a futures client would sign one platform's request for the other and be refused for a
        // reason nobody could read.
        using TestKernel kernel = new();

        ArgumentException data = Assert.Throws<ArgumentException>(() => new KrakenFuturesDataClient(
            new ClientId("KRAKEN"), new KrakenDataClientConfig(), kernel.Services));
        Assert.Contains(nameof(KrakenDataClient), data.Message, StringComparison.Ordinal);

        ArgumentException execution = Assert.Throws<ArgumentException>(() => new KrakenExecutionClient(
            new ClientId("KRAKEN"),
            new KrakenExecutionClientConfig { ProductType = KrakenProductType.Futures, ApiKey = "k", ApiSecret = Secret },
            kernel.Services));
        Assert.Contains(nameof(KrakenFuturesExecutionClient), execution.Message, StringComparison.Ordinal);
    }

    // ----- the errors that are answers -----

    [Fact]
    public void Only_a_refusal_that_means_no_such_pair_is_treated_as_an_answer()
    {
        // The line this must not cross. Swallowing every refusal would turn a venue that is down, a key that is
        // wrong or an address that is blocked into "that pair does not exist", and a caller would tell a person
        // their symbol was bad when the venue was unreachable.
        Assert.Contains("EQuery:Unknown asset pair", KrakenVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.Contains("EQuery:Invalid asset pair", KrakenVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.DoesNotContain("EAPI:Invalid key", KrakenVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.DoesNotContain("EAPI:Rate limit exceeded", KrakenVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.DoesNotContain("EService:Unavailable", KrakenVenue.ErrorsThatMeanNoSuchInstrument);
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_the_pair_still_reaches_the_caller()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/0/public/AssetPairs", _ => StubResponse.Json(KrakenPayloads.Error("EService:Unavailable")))
            .Handle);

        KrakenInstrumentProvider provider = new(Http(server));

        await Assert.ThrowsAsync<KrakenApiException>(() => provider.LoadAsync(_btc, CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_the_venue_reports_with_an_http_200_is_still_a_failure()
    {
        // This platform answers a refused request with HTTP 200 and the reason in an error array, so a caller
        // checking the status learns nothing at all. Pinned because it is the shape that makes a silent wrong
        // answer possible.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/0/public/AssetPairs", _ => new StubResponse(200, KrakenPayloads.Error("EGeneral:Invalid arguments")))
            .Handle);

        using KrakenHttp http = Http(server);
        KrakenApiException refusal = await Assert.ThrowsAsync<KrakenApiException>(
            () => http.GetPublicAsync("/0/public/AssetPairs", null, CancellationToken.None));

        Assert.Equal("EGeneral:Invalid arguments", refusal.Code);
        Assert.Equal(200, refusal.HttpStatus);
    }

    // ----- signing -----

    /// <summary>A base64 private key of the shape the venue issues, so the decode in the signer has something to do.</summary>
    private const string Secret = "a2Vja2V5c2VjcmV0MTIzNDU2Nzg5MGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6MDEyMzQ1Njc4OQ==";

    [Fact]
    public void A_spot_signature_covers_the_path_the_nonce_and_the_body()
    {
        // The scheme has four inputs and every one of them has to be in the signature: a signer that dropped the
        // path would sign the same string for two different endpoints, and one that dropped the nonce would sign a
        // replayable request. So each input is changed on its own and the signature has to move.
        string baseline = KrakenHttp.SignSpot(Secret, "/0/private/Balance", "1700000000000", "nonce=1700000000000");

        Assert.NotEqual(baseline, KrakenHttp.SignSpot(Secret, "/0/private/AddOrder", "1700000000000", "nonce=1700000000000"));
        Assert.NotEqual(baseline, KrakenHttp.SignSpot(Secret, "/0/private/Balance", "1700000000001", "nonce=1700000000000"));
        Assert.NotEqual(baseline, KrakenHttp.SignSpot(Secret, "/0/private/Balance", "1700000000000", "nonce=1700000000001"));

        // And it is deterministic, which is what lets a refusal be blamed on the key rather than on the signer.
        Assert.Equal(baseline, KrakenHttp.SignSpot(Secret, "/0/private/Balance", "1700000000000", "nonce=1700000000000"));

        // HMAC-SHA-512 is 64 bytes, base64-encoded.
        Assert.Equal(64, Convert.FromBase64String(baseline).Length);
    }

    [Fact]
    public void A_futures_signature_drops_the_derivatives_prefix_from_the_path()
    {
        // The part of the futures scheme most likely to be got wrong: the request goes to
        // /derivatives/api/v3/sendorder and the signature covers /api/v3/sendorder. A signer that signed the path
        // as sent would produce a signature the venue rejects, and the venue answers every rejection with the same
        // single word - so there would be nothing to tell it from a wrong key.
        Assert.Equal(
            KrakenHttp.SignFutures(Secret, "/api/v3/sendorder", "1", "size=1"),
            KrakenHttp.SignFutures(Secret, "/derivatives/api/v3/sendorder", "1", "size=1"));

        // And the rest of the path is still in it.
        Assert.NotEqual(
            KrakenHttp.SignFutures(Secret, "/derivatives/api/v3/sendorder", "1", "size=1"),
            KrakenHttp.SignFutures(Secret, "/derivatives/api/v3/cancelorder", "1", "size=1"));
        Assert.Equal(64, Convert.FromBase64String(KrakenHttp.SignFutures(Secret, "/api/v3/accounts", "1", "")).Length);
    }

    [Fact]
    public void A_nonce_never_repeats_and_never_goes_backwards()
    {
        // The venue keeps the highest nonce it has seen for a key and refuses anything below it, so a paging loop
        // that produced two requests in one millisecond would have its second request refused - and the refusal
        // reads as a clock problem rather than as a counter problem.
        using KrakenHttp http = new(new KrakenDataClientConfig { ApiKey = "k", ApiSecret = Secret });

        long[] nonces = [.. Enumerable.Range(0, 200).Select(_ => long.Parse(http.NextNonce(), System.Globalization.CultureInfo.InvariantCulture))];

        Assert.Equal(nonces.Length, nonces.Distinct().Count());
        Assert.Equal(nonces.Order().ToArray(), nonces);
    }

    // ----- what a host reads about this venue -----

    [Fact]
    public void Neither_family_offers_a_test_environment_and_both_say_so()
    {
        // E9 takes "no testnet" as an answer and not silence, and the answer here is no for both - which was
        // measured rather than assumed. The futures platform's demo host, demo-futures.kraken.com, now answers a
        // permanent redirect to a marketing page, and every other spelling of a demo or sandbox host on either
        // platform fails to resolve. So this venue can be papered against live data and cannot be rehearsed
        // against a test venue, on either platform.
        VenueDescriptor kraken = new KrakenPlugin().Describe();

        foreach (VenueFamily family in kraken.Families)
        {
            Assert.DoesNotContain("demo", family.HttpBase, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sandbox", family.HttpBase, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("testnet", family.HttpBase, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(family.WsBase);
        }

        // And there is no configuration value that would select one, which is what would make the absence
        // ambiguous: a host offering a "testnet" toggle would have nothing to point it at.
        Assert.All(kraken.Families, f => Assert.Equal(["productType"], f.Config.Keys.ToArray()));
    }

    [Fact]
    public void The_key_is_two_parts_and_the_declaration_says_they_are_not_interchangeable_between_the_families()
    {
        // The structural fact about this venue. Both platforms take a key and a base64 private key, so the SHAPE is
        // one shape and is declared once - but a spot key is issued on one site and a futures key on the other, and
        // neither signs for the other because the schemes differ. A host that offers one pair of fields per venue
        // can hold only one of the two at a time.
        VenueDescriptor kraken = new KrakenPlugin().Describe();

        foreach (VenueFamily family in kraken.Families)
        {
            Assert.Equal(
                [KrakenVenue.EnvApiKey, KrakenVenue.EnvApiSecret],
                family.Key.Parts.Select(p => p.Variable).ToArray());

            Assert.Contains(family.Key.Parts, p => p.Required && p.Secret);
        }

        Assert.Equal("KRAKEN_API_KEY", KrakenVenue.EnvApiKey);
        Assert.Equal("KRAKEN_API_SECRET", KrakenVenue.EnvApiSecret);
    }

    [Fact]
    public void The_broker_mechanism_declared_is_a_field_on_the_order()
    {
        // The venue runs the programme it calls the API Partner Program and publishes the mechanism: AddOrder and
        // sendorder each take a `broker` parameter carrying the partner's own Kraken IIBAN. A field on the order
        // changes nothing about the order's identity, which is why this needs no reconciliation test of the kind a
        // client-order-id prefix does.
        VenueDescriptor kraken = new KrakenPlugin().Describe();

        Assert.Equal(BrokerTag.OrderField, kraken.BrokerTag);
        Assert.Equal(BrokerProgramme.Carried, kraken.BrokerProgramme);
    }

    [Fact]
    public void The_declaration_names_the_platform_each_family_answers_on()
    {
        VenueDescriptor kraken = new KrakenPlugin().Describe();
        VenueFamily spot = kraken.Families.Single(f => f.Name == "spot");
        VenueFamily futures = kraken.Families.Single(f => f.Name == "futures");

        Assert.Equal(KrakenVenue.DefaultHttpBase, spot.HttpBase);
        Assert.Equal(KrakenVenue.DefaultWsBase, spot.WsBase);
        Assert.Equal(KrakenFuturesVenue.DefaultHttpBase, futures.HttpBase);
        Assert.Equal(KrakenFuturesVenue.DefaultWsBase, futures.WsBase);

        // Spot pays no funding and its family says so; the futures family holds perpetuals and is charged it.
        Assert.False(spot.PaysFunding);
        Assert.True(futures.PaysFunding);
        Assert.False(spot.Capabilities.FundingHistory);
        Assert.True(futures.Capabilities.FundingHistory);

        // And no free dataset on either, which is a statement: neither platform publishes a downloadable archive
        // the way Binance does, so a host is not left learning that from a 404.
        Assert.Empty(spot.FreeDatasets);
        Assert.Empty(futures.FreeDatasets);
    }
}
