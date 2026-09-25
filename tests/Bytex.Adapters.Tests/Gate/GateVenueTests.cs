using Bytex.Adapters.Gate;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Gate;

// Why: every fact this adapter rests on was MEASURED against the live venue, and a measurement nothing re-checks is
// a comment. Each test below pins one of them, and the ones that matter most are the ones that are silent when they
// are wrong: a timestamp in the wrong unit, a size in contracts read as base currency, a percentage read as a
// fraction, a candle row whose open and close are in slots this venue puts them in and no other does.
public sealed class GateVenueTests
{
    private static Instrument Perpetual(decimal multiplier = 0.0001m) => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC_USDT.GATE"),
        RawSymbol = new Symbol("BTC_USDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currency.FromCode("USDT", 8),
        BaseCurrency = Currency.FromCode("BTC", 8),
        SettlementCurrency = Currency.FromCode("USDT", 8),
        PricePrecision = 1,
        SizePrecision = 8,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(multiplier, 8),
        Info = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contractMultiplier"] = multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
    });

    // ----- the symbol, both ways -----

    [Theory]
    [InlineData("BTC_USDT")]
    [InlineData("ETH_USDT")]
    [InlineData("BTC_USDT_20261009")]
    [InlineData("EURUSD_USDT")]
    public void An_instrument_id_is_the_venues_own_name_and_survives_the_round_trip(string raw)
    {
        // The underscore is this venue's and nobody else's in this repository - Binance and Bybit write BTCUSDT and
        // KuCoin BTC-USDT - so the mapping being the identity is a FACT about Gate rather than a default, and it is
        // pinned in both directions because a host that strips it would ask about a pair the venue does not list.
        InstrumentId id = GateVenue.ToInstrumentId(raw);

        Assert.Equal(raw, id.Symbol.Value);
        Assert.Equal("GATE", id.Venue.Value);
        Assert.Equal(raw, GateVenue.ToRawSymbol(id));
        Assert.Equal(raw, GateVenue.ToRawSymbol(GateVenue.ToInstrumentId(GateVenue.ToRawSymbol(id))));
    }

    [Fact]
    public void Nothing_reads_what_an_instrument_is_off_how_its_name_is_spelled()
    {
        // BTC_USDT is a perpetual and BTC_USDT_20261009 is a dated contract, and the names differ by a date the
        // adapter never reads. The class comes from the endpoint the contract was loaded from and from the venue's
        // own `type` and `expire_time` fields, which is what the two providers' own tests prove.
        Assert.Equal(
            GateVenue.ToRawSymbol(GateVenue.ToInstrumentId("BTC_USDT")),
            GateVenue.ToRawSymbol(GateFuturesVenue.ToInstrumentId("BTC_USDT")));
    }

    // ----- the client order id, which this venue carries in a text field -----

    [Fact]
    public void A_client_order_id_travels_prefixed_and_comes_back_unprefixed()
    {
        // Gate has no client-order-id field: the id lives in the order's `text`, which the venue also writes its own
        // values into. The prefix is what tells this node's orders from everybody else's on the same account.
        string? text = GateVenue.ToOrderText(new ClientOrderId("O-20260925-001"));

        Assert.Equal("t-O-20260925-001", text);
        Assert.Equal(new ClientOrderId("O-20260925-001"), GateVenue.FromOrderText(text));
    }

    [Theory]
    [InlineData("web")]
    [InlineData("api")]
    [InlineData("liquidation")]
    [InlineData("")]
    [InlineData("t-")]
    public void An_order_the_venue_raised_itself_is_not_read_as_this_nodes(string text)
    {
        // The venue's own reserved values carry no prefix, and a bare prefix carries no id. Reading either as a
        // client order id would attach somebody else's fill - or a liquidation - to an order this node placed.
        Assert.Null(GateVenue.FromOrderText(text));
    }

    [Theory]
    [InlineData("0123456789012345678901234567")]
    [InlineData("a_b-c.d")]
    public void An_id_the_venue_would_accept_is_carried(string id)
    {
        Assert.NotNull(GateVenue.ToOrderText(new ClientOrderId(id)));
    }

    [Theory]
    [InlineData("01234567890123456789012345678")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    [InlineData("has/slash")]
    public void An_id_the_venue_would_refuse_is_refused_before_the_order_is_sent(string id)
    {
        // Twenty-nine characters, or a character outside the venue's set. Null here is what makes the execution
        // client reject the order with a sentence naming the rule, instead of the venue rejecting it with its own.
        Assert.Null(GateVenue.ToOrderText(new ClientOrderId(id)));
        Assert.Equal(28, GateVenue.MaxClientOrderIdLength);
    }

    // ----- the contract multiplier, which is silent when wrong -----

    [Theory]
    [InlineData(0.0001, 1, 0.0001)]
    [InlineData(0.0001, 10000, 1.0)]
    [InlineData(0.01, 250, 2.5)]
    [InlineData(100, 3, 300)]
    public void A_number_of_contracts_becomes_base_currency_and_back(decimal multiplier, long contracts, decimal expected)
    {
        // The conversion every quantity crossing this venue's derivative markets goes through. One BTC_USDT contract
        // is 0.0001 BTC, so ten thousand of them are one BTC - and a strategy sizing in base units is the same
        // strategy here as on Binance, which is the whole point of converting rather than exposing contracts.
        Instrument instrument = Perpetual(multiplier);

        Quantity quantity = GateFuturesVenue.ToQuantity(instrument, contracts);
        Assert.Equal(expected, quantity.Value);
        Assert.Equal(contracts, GateFuturesVenue.ToContracts(instrument, quantity));
    }

    [Fact]
    public void A_quantity_that_is_not_a_whole_number_of_contracts_is_refused_rather_than_rounded()
    {
        // Rounding silently would fill a different size than was asked for, so it is said out loud. The engine
        // rounds to the instrument's size increment - which IS one contract - so this can only be a hand-built
        // quantity, and a hand-built quantity is exactly the one worth complaining about.
        Instrument instrument = Perpetual(0.0001m);
        Quantity crooked = new(0.00015m, 8);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => GateFuturesVenue.ToContracts(instrument, crooked));
        Assert.Contains("whole ones", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_instrument_with_no_contract_size_cannot_be_sized_at_all()
    {
        // The failure mode this guards: an instrument that reached a derivative client from somewhere else - a spot
        // pair, a cached instrument from another venue - would otherwise be sized as though one contract were one
        // unit of base currency, which on BTC_USDT is ten thousand times too big.
        Instrument spot = new CurrencyPair(new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTC_USDT.GATE"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currency.FromCode("USDT", 8),
            BaseCurrency = Currency.FromCode("BTC", 8),
            PricePrecision = 1,
            SizePrecision = 6,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.000001m, 6),
        });

        Assert.Throws<InvalidOperationException>(() => GateFuturesVenue.Multiplier(spot));
    }

    // ----- margin, from the venue rather than hard-coded -----

    [Fact]
    public void The_initial_margin_rate_is_one_over_the_contracts_own_maximum_leverage()
    {
        // Measured on five contracts spanning the family: tier one of the venue's own risk-limit table publishes an
        // `initial_rate` that is exactly the reciprocal of the contract's `leverage_max`. BTC_USDT's 200x gives
        // 0.005, and the tier table below says 0.005 for tier one.
        Assert.Equal(0.005m, GateFuturesVenue.MarginInit(200m));
        Assert.Equal(0.01m, GateFuturesVenue.MarginInit(100m));
        Assert.Equal(0.1m, GateFuturesVenue.MarginInit(10m));

        // A contract that published no maximum leverage would otherwise divide by zero.
        Assert.Equal(0m, GateFuturesVenue.MarginInit(0m));
    }

    [Fact]
    public void The_margin_published_on_an_instrument_is_the_venues_tier_one_figure()
    {
        // The half that keeps the derivation honest against the venue's own table. Both numbers are read out of the
        // recorded tiers payload rather than written here, so a venue that changed its tiers would fail this.
        using System.Text.Json.JsonDocument tiers = System.Text.Json.JsonDocument.Parse(GatePayloads.RiskLimitTiers);
        System.Text.Json.JsonElement tierOne = tiers.RootElement[0];
        decimal initialRate = decimal.Parse(tierOne.GetProperty("initial_rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        decimal maintenanceRate = decimal.Parse(tierOne.GetProperty("maintenance_rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        using System.Text.Json.JsonDocument contract = System.Text.Json.JsonDocument.Parse(GatePayloads.FuturesContract);
        decimal leverageMax = decimal.Parse(contract.RootElement.GetProperty("leverage_max").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        decimal contractMaintenance = decimal.Parse(contract.RootElement.GetProperty("maintenance_rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(initialRate, GateFuturesVenue.MarginInit(leverageMax));
        Assert.Equal(maintenanceRate, contractMaintenance);

        // And neither is the 0.05/0.025 two older adapters in this repository hard-code for every contract.
        Assert.NotEqual(0.05m, initialRate);
        Assert.NotEqual(0.025m, maintenanceRate);
    }

    // ----- the bar lengths the venue really keeps -----

    [Theory]
    [InlineData(BarAggregation.Second, 10, "10s")]
    [InlineData(BarAggregation.Second, 30, "30s")]
    [InlineData(BarAggregation.Minute, 1, "1m")]
    [InlineData(BarAggregation.Minute, 3, "3m")]
    [InlineData(BarAggregation.Hour, 6, "6h")]
    [InlineData(BarAggregation.Day, 3, "3d")]
    [InlineData(BarAggregation.Week, 1, "7d")]
    public void A_bar_length_the_venue_keeps_is_spelled_the_venues_way(BarAggregation aggregation, int step, string expected)
    {
        // Five of these are not in the venue's documented list and all five were answered by the live endpoint with
        // rows at the right spacing, which is why the table was built by asking rather than by reading.
        Assert.Equal(expected, GateVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Theory]
    [InlineData(BarAggregation.Month, 1)]
    [InlineData(BarAggregation.Minute, 99)]
    [InlineData(BarAggregation.Second, 1)]
    public void A_bar_length_the_venue_does_not_keep_is_refused_here_rather_than_there(BarAggregation aggregation, int step)
    {
        // A month is deliberately absent even though the venue accepts "30d": it answers rows 31 days apart, so it
        // is neither thirty days nor a calendar month and there is nothing this engine could ask for that it would
        // answer correctly. One second is absent for the same reason - the venue answers it with empty candles.
        Assert.Throws<NotSupportedException>(() => GateVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    // ----- the page and window caps, which differ per market on one venue -----

    [Fact]
    public void Each_markets_candle_cap_is_the_one_the_venue_enforces()
    {
        // Measured by asking for one more than each: spot refuses limit 1001 and the derivative markets refuse 2001,
        // both with INVALID_PARAM_VALUE. The span is one less than the page because a from..to window is inclusive
        // at both ends, so N intervals is N+1 rows - the off-by-one that makes a paging loop repeat or skip a bar.
        Assert.Equal(1000, GateVenue.SpotCandlePage);
        Assert.Equal(999, GateVenue.SpotCandleSpan);
        Assert.Equal(2000, GateFuturesVenue.CandlePage);
        Assert.Equal(1999, GateFuturesVenue.CandleSpan);
    }

    [Fact]
    public void Funding_history_is_bounded_by_a_window_rather_than_by_a_row_count()
    {
        // The measurement a limit parameter hides. The endpoint accepts limit=1000 and answers thirty days whatever
        // the limit says: 90 rows for eight-hourly funding, 180 for four-hourly, 720 for hourly. And it will not
        // reach back further than 180 days at all.
        Assert.Equal(TimeSpan.FromDays(30), GateVenue.FundingWindow);
        Assert.Equal(TimeSpan.FromDays(180), GateVenue.FundingHistoryReach);
        Assert.Equal(1000, GateVenue.FundingPage);
    }

    // ----- hosts, per family and per market -----

    [Fact]
    public void Spot_and_the_derivative_markets_answer_on_different_hosts()
    {
        // The general host serves all three markets and the derivatives host serves only the two derivative ones -
        // it answers 404 for a spot path - so the derivatives host is the honest default for them and is what tells
        // the families apart for anything reading the declaration.
        Assert.Equal("https://api.gateio.ws", GateVenue.HttpBase(new GateDataClientConfig()));
        Assert.Equal("https://fx-api.gateio.ws", GateVenue.HttpBase(new GateDataClientConfig { ProductType = GateProductType.Futures }));
        Assert.Equal("https://fx-api.gateio.ws", GateVenue.HttpBase(new GateDataClientConfig { ProductType = GateProductType.Delivery }));
    }

    [Fact]
    public void Each_markets_socket_is_its_own_address()
    {
        // The settlement currency is in the socket's PATH on this venue, so there is one address per settled market
        // rather than one for the venue. All three were confirmed by handshake.
        Assert.Equal("wss://api.gateio.ws/ws/v4/", GateVenue.WsBase(new GateDataClientConfig()));
        Assert.Equal("wss://fx-ws.gateio.ws/v4/ws/usdt", GateVenue.WsBase(new GateDataClientConfig { ProductType = GateProductType.Futures }));
        Assert.Equal("wss://fx-ws.gateio.ws/v4/ws/delivery/usdt", GateVenue.WsBase(new GateDataClientConfig { ProductType = GateProductType.Delivery }));
    }

    [Fact]
    public void A_configured_address_replaces_the_default_on_every_market()
    {
        // What makes a declared base worth declaring: it is the default of the setting that overrides it, and the
        // setting has to work on all three families or the one it does not work on cannot be pointed at a recording.
        foreach (GateProductType product in Enum.GetValues<GateProductType>())
        {
            GateDataClientConfig config = new()
            {
                ProductType = product,
                BaseUrlHttp = "http://127.0.0.1:1",
                BaseUrlWs = "ws://127.0.0.1:1",
            };

            Assert.Equal("http://127.0.0.1:1", GateVenue.HttpBase(config));
            Assert.Equal("ws://127.0.0.1:1", GateVenue.WsBase(config));
        }
    }

    // ----- the key -----

    [Fact]
    public void The_key_is_two_parts_and_the_declaration_says_so()
    {
        // Worth a test of its own because the venues either side of this one in the 0.7 batch all have a third part.
        // A host that offered a passphrase field here would be asking for something that does not exist, and one
        // that expected the adapter to read a third variable would find none.
        VenueDescriptor gate = new GatePlugin().Describe();

        foreach (VenueFamily family in gate.Families)
        {
            Assert.Equal(2, family.Key.Parts.Count);
            Assert.Equal([GateVenue.EnvApiKey, GateVenue.EnvApiSecret], family.Key.Parts.Select(p => p.Variable).ToArray());
            Assert.All(family.Key.Parts, p => Assert.True(p.Required));
            Assert.Single(family.Key.Parts, p => p.Secret);
        }
    }

    [Fact]
    public void Credentials_come_from_the_configuration_or_the_environment()
    {
        GateCredentials configured = GateVenue.Credentials(
            new GateExecutionClientConfig { ApiKey = "publickey", ApiSecret = "NEVER-PRINT-THIS" });

        Assert.Equal("publickey", configured.Key);
        Assert.Equal("NEVER-PRINT-THIS", configured.Secret);

        // And a secret never reaches a log line or an exception text through the record's own printing - which a
        // record gets for free and prints EVERY field in, so a Gate credential has to override it.
        Assert.DoesNotContain("NEVER-PRINT-THIS", configured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_key_is_a_sentence_rather_than_a_null_reference()
    {
        Assert.Throws<InvalidOperationException>(() => GateVenue.Credentials(new GateExecutionClientConfig()));
        Assert.Null(GateVenue.OptionalCredentials(new GateExecutionClientConfig { ApiKey = "k" }));
    }

    // ----- the declaration, against the facts above -----

    [Fact]
    public void The_declaration_names_the_three_markets_this_adapter_offers()
    {
        VenueDescriptor gate = new GatePlugin().Describe();

        Assert.Equal(["spot", "futures", "delivery"], gate.Families.Select(f => f.Name).ToArray());
        Assert.Equal(InstrumentClass.Spot, gate.FamilyFor(InstrumentClass.Spot)!.InstrumentClasses[0]);
        Assert.Equal("futures", gate.FamilyFor(InstrumentClass.Swap)!.Name);
        Assert.Equal("delivery", gate.FamilyFor(InstrumentClass.Future)!.Name);
    }

    [Fact]
    public void Only_the_perpetual_family_is_charged_funding_and_only_it_can_fetch_any()
    {
        VenueDescriptor gate = new GatePlugin().Describe();

        Assert.False(gate.Families.Single(f => f.Name == "spot").PaysFunding);
        Assert.True(gate.Families.Single(f => f.Name == "futures").PaysFunding);

        // A dated contract settles at expiry rather than being held to spot by a payment, and the venue publishes no
        // funding fields on one at all - its ticker channel sends the rate as an empty string.
        Assert.False(gate.Families.Single(f => f.Name == "delivery").PaysFunding);
        Assert.True(gate.Families.Single(f => f.Name == "futures").Capabilities.FundingHistory);
        Assert.False(gate.Families.Single(f => f.Name == "delivery").Capabilities.FundingHistory);
    }

    [Fact]
    public void Only_the_dated_family_cannot_amend_an_order()
    {
        // The one capability that differs between this venue's own markets, and the reason the delivery clients are
        // separate classes: spot amends with PATCH, perpetual futures with PUT, and the delivery surface has no
        // amend endpoint of any kind.
        VenueDescriptor gate = new GatePlugin().Describe();

        Assert.True(gate.Families.Single(f => f.Name == "spot").Capabilities.AmendOrders);
        Assert.True(gate.Families.Single(f => f.Name == "futures").Capabilities.AmendOrders);
        Assert.False(gate.Families.Single(f => f.Name == "delivery").Capabilities.AmendOrders);
    }

    [Fact]
    public void The_broker_row_says_the_mechanism_is_undisclosed_rather_than_that_there_is_no_programme()
    {
        // The distinction that cost this repository a false statement about another venue. Gate runs an API broker
        // programme; it names an additional channel id on the programme page and publishes neither the header's
        // spelling nor its value format in the API reference, so nothing can be carried before somebody applies.
        VenueDescriptor gate = new GatePlugin().Describe();

        Assert.Equal(BrokerTag.None, gate.BrokerTag);
        Assert.Equal(BrokerProgramme.MechanismUndisclosed, gate.BrokerProgramme);
        Assert.NotEqual(BrokerProgramme.None, gate.BrokerProgramme);
    }

    [Fact]
    public void A_configured_broker_id_is_a_no_op_rather_than_a_refusal()
    {
        // The owner's ruling: nothing above an adapter has to know which venues have a programme, so setting an id
        // on a venue that carries none is ignored rather than rejected.
        GateExecutionClientConfig config = new() { ApiKey = "k", ApiSecret = "s", BrokerId = "whatever" };

        Assert.Equal("whatever", config.BrokerId);
        Assert.Equal(BrokerTag.None, new GatePlugin().Describe().BrokerTag);
    }

    // ----- errors -----

    [Fact]
    public async Task A_refusal_carries_the_venues_label_and_message()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/currency_pairs", _ => new StubResponse(400, GatePayloads.Error("INVALID_PARAM_VALUE", "bad thing")))
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { BaseUrlHttp = server.HttpBase });
        GateApiException refused = await Assert.ThrowsAsync<GateApiException>(() => http.GetPublicAsync("/spot/currency_pairs", null, CancellationToken.None));

        Assert.Equal("INVALID_PARAM_VALUE", refused.Label);
        Assert.Equal("bad thing", refused.Msg);
        Assert.Equal(400, refused.HttpStatus);
    }

    [Fact]
    public async Task A_delivery_refusal_that_names_its_reason_detail_is_still_read()
    {
        // Measured: the delivery endpoints put their sentence in `detail` where spot and futures put it in
        // `message`, for the same refusal. A client reading only `message` would report an empty reason.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/delivery/usdt/contracts", _ => new StubResponse(400, """{"label":"INVALID_PARAM_VALUE","detail":"Invalid request parameter `from` value: 1"}"""))
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Delivery, BaseUrlHttp = server.HttpBase });
        GateApiException refused = await Assert.ThrowsAsync<GateApiException>(() => http.GetPublicAsync("/delivery/usdt/contracts", null, CancellationToken.None));

        Assert.Equal("INVALID_PARAM_VALUE", refused.Label);
        Assert.Contains("`from`", refused.Msg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_reported_rather_than_thrown_over()
    {
        // The CDN in front of this venue answers a bad gateway with HTML, which is what the dead futures testnet
        // host does today. A client that could not report a 502 would be worse than one that reports it unlabelled.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/currency_pairs", _ => new StubResponse(502, "<html><head><title>502 Bad Gateway</title></head></html>", "text/html"))
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { BaseUrlHttp = server.HttpBase });
        GateApiException refused = await Assert.ThrowsAsync<GateApiException>(() => http.GetPublicAsync("/spot/currency_pairs", null, CancellationToken.None));

        Assert.Equal(string.Empty, refused.Label);
        Assert.Equal(502, refused.HttpStatus);
    }

    // ----- signing -----

    [Fact]
    public async Task A_signed_request_carries_the_three_headers_the_venue_wants()
    {
        // Two of the five lines Gate signs are easy to get wrong in a way that shows only as a rejected signature:
        // the body's hash is the hash of the EMPTY STRING when there is no body, and the timestamp is in SECONDS
        // where most venues take milliseconds. The timestamp is checked for magnitude here, because a millisecond
        // value would be a thousand times too large and the venue would answer that the request had expired.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/accounts", "[]")
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ApiKey = "k", ApiSecret = "s", BaseUrlHttp = server.HttpBase }, requireCredentials: true);
        await http.GetSignedAsync("/spot/accounts", null, CancellationToken.None);

        RecordedRequest sent = Assert.Single(server.RequestsTo("/api/v4/spot/accounts"));
        Assert.Equal("k", sent.Header("KEY"));
        Assert.NotNull(sent.Header("SIGN"));

        // Hex HMAC-SHA512 is 128 characters; base64 or SHA-256 would be a different length.
        Assert.Equal(128, sent.Header("SIGN")!.Length);

        long timestamp = long.Parse(sent.Header("Timestamp")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(timestamp, 1_600_000_000L, 9_999_999_999L);
    }

    [Fact]
    public async Task The_path_that_is_signed_is_the_path_that_is_sent_prefix_and_query_included()
    {
        // The signature covers the path WITH its /api/v4 prefix and the query string exactly as sent, so the two
        // cannot be built separately. This asks the stub what arrived and checks the query survived the trip.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/orders", "[]")
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ApiKey = "k", ApiSecret = "s", BaseUrlHttp = server.HttpBase }, requireCredentials: true);
        await http.GetSignedAsync(
            "/spot/orders",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["currency_pair"] = "BTC_USDT", ["status"] = "open" },
            CancellationToken.None);

        RecordedRequest sent = Assert.Single(server.RequestsTo("/api/v4/spot/orders"));
        Assert.Equal("BTC_USDT", sent.Query("currency_pair"));
        Assert.Equal("open", sent.Query("status"));
        Assert.Equal("/api/v4", GateVenue.ApiPrefix);
    }
}
