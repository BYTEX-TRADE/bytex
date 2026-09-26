using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Okx;

// Why: OKX is the first venue here whose three markets share one host, so nothing about which market a client is
// talking to can be read off an address - and the first whose instrument ids would actively mislead a reader who
// trusted their spelling. Everything below was measured against the live venue on 2026-09-25, across all 1,415 spot
// pairs, 492 perpetuals and 244 dated contracts, and three of the numbers disagree with what the venue publishes
// about itself.
public sealed class OkxVenueTests
{
    // ----- what the venue calls an instrument, and what the engine calls it -----

    [Theory]
    [InlineData("BTC-USDT")]
    [InlineData("BTC-USDT-SWAP")]
    [InlineData("BTC-USD-SWAP")]
    [InlineData("BTC-USD_UM-261030")]
    [InlineData("BTC-USD-261030")]
    public void An_instrument_id_is_the_venues_own_name_unchanged(string instId)
    {
        // No suffix added and none stripped, which is the opposite of what the other venues here do and is the right
        // answer for this one: Bybit's spot BTCUSDT and its linear BTCUSDT are the same string, so one of them has to
        // be renamed, and OKX already distinguishes all three of its markets in the id itself. Renaming would only
        // be a second spelling to translate in both directions.
        Assert.Equal(instId + ".OKX", OkxVenue.ToInstrumentId(instId).ToString());
        Assert.Equal(instId, OkxVenue.ToRawSymbol(InstrumentId.Parse(instId + ".OKX")));
    }

    [Fact]
    public void No_rule_about_spelling_could_tell_these_four_contracts_apart()
    {
        // The reason the class comes from the venue's own instType and never from the id. These four ids differ by a
        // word or by three characters in the middle of a name, and they are four different things: a spot pair, an
        // inverse perpetual, a linear perpetual and an inverse dated contract. A "-PERP means perpetual" rule, which
        // is what the other venues here use, reads all four as spot.
        string[] ids = ["BTC-USDT", "BTC-USD-SWAP", "BTC-USDT-SWAP", "BTC-USD-261030"];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (string id in ids)
        {
            Assert.DoesNotContain("-PERP", id, StringComparison.Ordinal);
        }
    }

    // ----- the markets -----

    [Theory]
    [InlineData(OkxInstrumentType.Spot, "SPOT")]
    [InlineData(OkxInstrumentType.Swap, "SWAP")]
    [InlineData(OkxInstrumentType.Futures, "FUTURES")]
    public void Each_market_is_named_the_way_the_venue_names_it(OkxInstrumentType type, string instType) =>
        Assert.Equal(instType, OkxVenue.InstType(type));

    [Fact]
    public void Spot_is_what_a_configuration_that_says_nothing_means()
    {
        // The default has to be the market a configuration written without the field meant, and this venue's
        // instType is required on almost every request - so a wrong default is a client that connects and lists the
        // wrong catalog rather than one that fails.
        Assert.Equal(OkxInstrumentType.Spot, new OkxDataClientConfig().InstrumentType);
        Assert.Equal(OkxInstrumentType.Spot, new OkxExecutionClientConfig().InstrumentType);
    }

    [Fact]
    public void Cross_margin_is_what_a_configuration_that_says_nothing_means()
    {
        // The venue demands a trade mode on every derivative order and has no default of its own, so something must
        // supply one. Cross is what a new OKX account is created with, so a configuration that says nothing trades
        // the way the venue's own interface would.
        Assert.Equal(OkxMarginMode.Cross, new OkxExecutionClientConfig().MarginMode);
        Assert.Equal("cross", OkxVenue.MarginMode(OkxMarginMode.Cross));
        Assert.Equal("isolated", OkxVenue.MarginMode(OkxMarginMode.Isolated));
    }

    // ----- hosts -----

    [Fact]
    public void One_host_and_one_socket_root_serve_all_three_markets()
    {
        // The structural fact that makes this venue different from every other one here, and the reason a family's
        // declaration cannot be checked by where a client talks: what selects a market is a parameter on the
        // request. Measured: the same address answered for SPOT, SWAP and FUTURES.
        foreach (OkxInstrumentType type in Enum.GetValues<OkxInstrumentType>())
        {
            OkxDataClientConfig config = new() { InstrumentType = type };
            Assert.Equal("https://www.okx.com", OkxVenue.HttpBase(config));
            Assert.Equal("wss://ws.okx.com:8443", OkxVenue.WsBase(config));
        }
    }

    [Fact]
    public void Candles_come_from_a_different_socket_path_from_everything_else()
    {
        // Measured, and the one thing about this venue's sockets that cannot be guessed: subscribing to candle1m on
        // the public path is refused with code 60018 "wrong URL or channel", and the same subscription on the
        // business path delivers. A client that opened one socket would have quotes and trades flowing and bars
        // silently absent.
        OkxDataClientConfig config = new();

        Assert.Equal("wss://ws.okx.com:8443/ws/v5/public", OkxVenue.WsPublic(config).ToString());
        Assert.Equal("wss://ws.okx.com:8443/ws/v5/business", OkxVenue.WsBusiness(config).ToString());
        Assert.Equal("wss://ws.okx.com:8443/ws/v5/private", OkxVenue.WsPrivate(config).ToString());
        Assert.NotEqual(OkxVenue.WsPublic(config), OkxVenue.WsBusiness(config));
    }

    [Fact]
    public void A_declared_base_written_back_into_a_configuration_changes_nothing()
    {
        // What makes a base worth declaring: it is the default of the setting that overrides it, so a host can put
        // it in a configuration file and get the same client. A root and not an endpoint, because three paths hang
        // off the socket root and declaring one of them would produce a URL with the path appended twice.
        OkxDataClientConfig written = new()
        {
            BaseUrlHttp = OkxVenue.DefaultHttpBase,
            BaseUrlWs = OkxVenue.DefaultWsBase,
        };

        Assert.Equal(OkxVenue.HttpBase(new OkxDataClientConfig()), OkxVenue.HttpBase(written));
        Assert.Equal(OkxVenue.WsPublic(new OkxDataClientConfig()), OkxVenue.WsPublic(written));
    }

    [Fact]
    public void The_demo_account_is_a_header_rather_than_an_address()
    {
        // Measured: a public endpoint answered identically with this header set, on the same host. What the header
        // does to a PRIVATE request cannot be measured without a key and is stated as unverified rather than
        // implied here.
        Assert.Equal("x-simulated-trading", OkxVenue.SimulatedTradingHeader);
        Assert.Equal("1", OkxVenue.SimulatedTradingOn);

        // And the address does not change with it, which is the whole point of the header existing.
        Assert.Equal(
            OkxVenue.HttpBase(new OkxDataClientConfig()),
            OkxVenue.HttpBase(new OkxDataClientConfig { DemoTrading = true }));
    }

    // ----- the page caps, none of which are the documented ones -----

    [Fact]
    public void The_page_caps_are_the_measured_ones_and_not_the_documented_ones()
    {
        // Asked for 500 candles on each of the venue's two candle endpoints, both returned exactly 300 with a
        // success code - one documents 300 and the other documents 100. Asked for 200 funding settlements, the venue
        // served 200, where it documents 100. Asked for 1000 trades it served 500, which is what it documents.
        //
        // A loop written to the documented 100 for history-candles makes three times the requests it needs; one
        // written to treat a 300-row answer as a short page stops at the first page.
        Assert.Equal(300, OkxVenue.CandlePage);
        Assert.Equal(200, OkxVenue.FundingPage);
        Assert.Equal(500, OkxVenue.TradePage);
    }

    [Fact]
    public void A_position_tier_request_may_name_five_families_and_no_more()
    {
        // Measured: five families are answered and six are refused outright with code 50025, "Parameter instFamily
        // count exceeds the limit 5". This is what sets the cost of publishing the venue's own margin for a whole
        // catalog, so it is a named number rather than a literal inside a loop.
        Assert.Equal(5, OkxVenue.TierFamilyPage);
        Assert.Equal("1", OkxVenue.FirstTier);
    }

    [Fact]
    public void The_public_rate_limit_is_the_measured_one()
    {
        // Measured rather than read: twenty requests fired at once were all answered and the twenty-first onwards
        // came back 429.
        Assert.Equal(20, OkxVenue.RequestsPerWindow);
        Assert.Equal(TimeSpan.FromSeconds(2), OkxVenue.RequestWindow);
    }

    // ----- bar lengths -----

    [Theory]
    [InlineData(BarAggregation.Second, 1, "1s")]
    [InlineData(BarAggregation.Minute, 1, "1m")]
    [InlineData(BarAggregation.Minute, 15, "15m")]
    [InlineData(BarAggregation.Hour, 1, "1H")]
    [InlineData(BarAggregation.Hour, 6, "6H")]
    [InlineData(BarAggregation.Hour, 12, "12H")]
    [InlineData(BarAggregation.Day, 1, "1D")]
    [InlineData(BarAggregation.Week, 1, "1W")]
    [InlineData(BarAggregation.Month, 1, "1M")]
    public void A_bar_length_is_spelled_the_way_the_venue_spells_it(BarAggregation aggregation, int step, string bar)
    {
        // Measured one length at a time against the live candle endpoint: every one of these was answered. The case
        // is the venue's own - lower for minutes and seconds, upper for hours and longer - and a wrong case is
        // refused rather than approximated.
        Assert.Equal(bar, OkxVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Fact]
    public void A_length_the_venue_refuses_is_refused_here_rather_than_sent()
    {
        // Eight hours, which KuCoin and Binance both keep and this venue does not: it answers "Parameter bar error"
        // with code 51000. Refusing here names the lengths that exist, where sending it would put a venue string in
        // front of whoever asked.
        NotSupportedException refused = Assert.Throws<NotSupportedException>(
            () => OkxVenue.Interval(new BarSpecification(8, BarAggregation.Hour, PriceType.Last)));

        Assert.Contains("8 hours", refused.Message, StringComparison.Ordinal);
    }

    // ----- client order ids -----

    [Theory]
    [InlineData("O20231114221320001001", true)]
    [InlineData("abc123", true)]
    [InlineData("O-20231114-221320-001-001-1", false)]
    [InlineData("has_underscore", false)]
    [InlineData("", false)]
    public void A_client_order_id_is_letters_and_digits_and_no_longer_than_the_venue_takes(string value, bool acceptable) =>
        Assert.Equal(acceptable, OkxVenue.IsAcceptableClientOrderId(value));

    [Fact]
    public void An_id_one_character_over_the_limit_is_refused()
    {
        // The boundary, named, because it is the one an engine-generated id sits close to: 32 characters of letters
        // and digits is accepted and 33 is not.
        Assert.Equal(32, OkxVenue.MaxClientOrderIdLength);
        Assert.True(OkxVenue.IsAcceptableClientOrderId(new string('a', 32)));
        Assert.False(OkxVenue.IsAcceptableClientOrderId(new string('a', 33)));
    }

    [Fact]
    public void The_engines_own_default_order_id_is_one_this_venue_would_refuse()
    {
        // Worth pinning rather than leaving to be discovered live. The engine's generator produces
        // O-20231114-221320-TESTER-EMACross-1 unless a strategy turns the hyphens off, and this venue takes letters
        // and digits only - so the refusal in the execution client is reached by a default configuration, and the
        // message it gives has to name the setting that fixes it.
        Core.Trading.ClientOrderIdGenerator hyphenated = new(new TraderId("TESTER-001"), new StrategyId("EMACross-007"), new Core.Timing.TestClock(TestKernel.Now));
        Core.Trading.ClientOrderIdGenerator plain = new(new TraderId("TESTER-001"), new StrategyId("EMACross-007"), new Core.Timing.TestClock(TestKernel.Now), useHyphens: false);

        Assert.False(OkxVenue.IsAcceptableClientOrderId(hyphenated.Generate().Value));
        Assert.True(OkxVenue.IsAcceptableClientOrderId(plain.Generate().Value));
    }

    // ----- sizes -----

    private static Instrument Perpetual(decimal contractValue) => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
        RawSymbol = new Symbol("BTC-USDT-SWAP"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 4,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.0001m, 4),
        Info = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OkxVenue.ContractValueInfo] = contractValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
    });

    private static Instrument Pair() => new CurrencyPair(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC-USDT.OKX"),
        RawSymbol = new Symbol("BTC-USDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 8,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.00000001m, 8),
    });

    [Fact]
    public void A_derivative_size_crosses_as_a_number_of_contracts_and_comes_back_as_base_currency()
    {
        // One BTC-USDT-SWAP contract is 0.01 BTC, so three bitcoin is 300 contracts. Everything above the adapter
        // counts in base currency, which is what makes one strategy work on this venue and on the others.
        Instrument perpetual = Perpetual(0.01m);

        Assert.Equal(300m, OkxVenue.ToContracts(perpetual, perpetual.MakeQuantity(3m)));
        Assert.Equal(perpetual.MakeQuantity(3m), OkxVenue.ToQuantity(perpetual, 300m));
        Assert.Equal(300m, OkxVenue.ToVenueSize(perpetual, perpetual.MakeQuantity(3m)));
        Assert.Equal(perpetual.MakeQuantity(3m), OkxVenue.FromVenueSize(perpetual, 300m));
    }

    [Fact]
    public void A_fraction_of_a_contract_is_not_rounded_away()
    {
        // This venue is not KuCoin: BTC-USDT-SWAP has a lot size of 0.01 CONTRACTS, so a hundredth of a 0.01 BTC
        // contract - 0.0001 BTC - is tradable. Rounding to a whole contract here would refuse the venue's own
        // smallest order, or silently trade a hundred times the size asked for.
        Instrument perpetual = Perpetual(0.01m);

        Assert.Equal(0.01m, OkxVenue.ToContracts(perpetual, perpetual.MakeQuantity(0.0001m)));
    }

    [Fact]
    public void A_spot_size_crosses_unchanged_because_the_venue_counts_it_in_base_currency_already()
    {
        Instrument pair = Pair();

        Assert.Equal(0.5m, OkxVenue.ToVenueSize(pair, pair.MakeQuantity(0.5m)));
        Assert.Equal(pair.MakeQuantity(0.5m), OkxVenue.FromVenueSize(pair, 0.5m));
    }

    [Fact]
    public void A_derivative_with_no_recorded_contract_value_refuses_to_size_an_order()
    {
        // An instrument that came from somewhere other than this venue's provider carries no contract value, and a
        // size worked out from a missing one would be an order of the wrong size rather than a failed request.
        Instrument stripped = new CryptoPerpetual(new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
            RawSymbol = new Symbol("BTC-USDT-SWAP"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 4,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.0001m, 4),
        });

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => OkxVenue.ToContracts(stripped, stripped.MakeQuantity(1m)));

        Assert.Contains("contract value", refused.Message, StringComparison.Ordinal);
    }

    // ----- keys -----

    [Fact]
    public void The_variable_names_are_the_documented_ones()
    {
        Assert.Equal("OKX_API_KEY", OkxVenue.EnvApiKey);
        Assert.Equal("OKX_API_SECRET", OkxVenue.EnvApiSecret);
        Assert.Equal("OKX_API_PASSPHRASE", OkxVenue.EnvApiPassphrase);
    }

    [Fact]
    public void A_key_is_three_parts_and_all_three_are_required()
    {
        // Unlike KuCoin's, which has an optional fourth part. The passphrase is chosen when the key is made and
        // cannot be recovered, so two thirds of one is nothing.
        OkxExecutionClientConfig two = new() { ApiKey = "k", ApiSecret = "s" };

        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => OkxVenue.Credentials(two));
        Assert.Contains("OKX_API_PASSPHRASE", missing.Message, StringComparison.Ordinal);
        Assert.Null(OkxVenue.OptionalCredentials(two));

        OkxCredentials complete = OkxVenue.Credentials(new OkxExecutionClientConfig { ApiKey = "k", ApiSecret = "s", ApiPassphrase = "p" });
        Assert.Equal("k", complete.Key);
    }

    [Fact]
    public void Credentials_never_print_the_secret_or_the_passphrase()
    {
        string printed = new OkxCredentials("key-value", "secret-value", "passphrase-value").ToString();

        Assert.DoesNotContain("secret-value", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("passphrase-value", printed, StringComparison.Ordinal);
    }
}
