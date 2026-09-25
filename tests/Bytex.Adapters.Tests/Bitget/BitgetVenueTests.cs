using Bytex.Adapters.Bitget;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests.Bitget;

// Why: everything in this file was measured against the live venue on 2026-09-25, and every one of the facts pinned
// here is one that would otherwise be got wrong by reading the documentation. The venue has two candle endpoints with
// different caps and different retention, three spellings of a bar length across two REST endpoints and one socket, a
// refusal message that lists the granularities it accepts and leaves three of them out, a page size that is reduced
// SILENTLY when a caller asks for more, and a demo market that is not where the documentation says it is.
//
// A constant that says "the venue does X" and no test that says so is a comment. These are the tests.
public sealed class BitgetVenueTests
{
    // ----- the declaration -----

    [Fact]
    public void The_venue_declares_the_three_families_it_really_holds()
    {
        VenueDescriptor venue = new BitgetPlugin().Describe();

        Assert.Equal(new Venue("BITGET"), venue.Venue);
        Assert.Equal(["spot", "usdt-futures", "usdc-futures"], venue.Families.Select(f => f.Name));

        // The venue's coin-margined product type is deliberately not a family: its contract list answers with an
        // empty array, so a host offered it would find nothing in it.
        Assert.DoesNotContain("coin-futures", venue.Families.Select(f => f.Name));
    }

    [Fact]
    public void A_key_is_three_parts_on_every_family()
    {
        // Two would leave a user with a key that looks complete and cannot sign anything: the passphrase is chosen
        // when the key is made and cannot be recovered afterwards.
        foreach (VenueFamily family in new BitgetPlugin().Describe().Families)
        {
            Assert.Equal(
                [BitgetVenue.EnvApiKey, BitgetVenue.EnvApiPassphrase, BitgetVenue.EnvApiSecret],
                family.Key.Parts.Select(p => p.Variable).Order(StringComparer.Ordinal));

            Assert.All(family.Key.Parts, p => Assert.True(p.Required));
        }
    }

    [Fact]
    public void The_variable_names_are_the_documented_ones()
    {
        Assert.Equal("BITGET_API_KEY", BitgetVenue.EnvApiKey);
        Assert.Equal("BITGET_API_SECRET", BitgetVenue.EnvApiSecret);
        Assert.Equal("BITGET_API_PASSPHRASE", BitgetVenue.EnvApiPassphrase);
    }

    [Fact]
    public void Only_the_perpetual_families_are_charged_funding()
    {
        VenueDescriptor venue = new BitgetPlugin().Describe();

        Assert.False(venue.Families.Single(f => f.Name == "spot").PaysFunding);
        Assert.True(venue.Families.Single(f => f.Name == "usdt-futures").PaysFunding);
        Assert.True(venue.Families.Single(f => f.Name == "usdc-futures").PaysFunding);
    }

    [Fact]
    public void Every_family_answers_on_the_same_host_and_the_same_socket()
    {
        // The fact that makes this venue different from the other three that ship: nothing about the address changes
        // with the family, so nothing can tell them apart by where a client talks. What does tell them apart is the
        // product type, which is why it is declared as the configuration that selects a family and asserted below.
        foreach (VenueFamily family in new BitgetPlugin().Describe().Families)
        {
            Assert.Equal("https://api.bitget.com", family.HttpBase);
            Assert.Equal("wss://ws.bitget.com", family.WsBase);
        }
    }

    [Theory]
    [InlineData(BitgetProductType.Spot, BitgetTradingMode.Live, "SPOT")]
    [InlineData(BitgetProductType.UsdtFutures, BitgetTradingMode.Live, "USDT-FUTURES")]
    [InlineData(BitgetProductType.UsdcFutures, BitgetTradingMode.Live, "USDC-FUTURES")]
    [InlineData(BitgetProductType.UsdtFutures, BitgetTradingMode.Demo, "SUSDT-FUTURES")]
    [InlineData(BitgetProductType.UsdcFutures, BitgetTradingMode.Demo, "SUSDC-FUTURES")]
    public void The_market_a_client_names_is_the_venues_own_spelling(BitgetProductType type, BitgetTradingMode mode, string expected)
    {
        Assert.Equal(expected, BitgetVenue.Market(new BitgetDataClientConfig { ProductType = type, TradingMode = mode }));
    }

    // ----- demo, which is not where it is written down -----

    [Fact]
    public void Demo_is_the_same_hosts_with_an_S_in_front_of_the_product_type()
    {
        // Measured. The demo markets answer on api.bitget.com and ws.bitget.com exactly as the live ones do, and what
        // selects them is one letter in front of the product type. The separate socket host several sources name -
        // wspap.bitget.com - accepts a connection and then refuses every demo subscription on it.
        BitgetDataClientConfig demo = new() { ProductType = BitgetProductType.UsdtFutures, TradingMode = BitgetTradingMode.Demo };

        Assert.Equal("SUSDT-FUTURES", BitgetVenue.Market(demo));
        Assert.Equal("https://api.bitget.com", BitgetVenue.HttpBase(demo));
        Assert.Equal("wss://ws.bitget.com/v2/ws/public", BitgetVenue.WsPublic(demo));
    }

    [Fact]
    public void There_is_no_demo_spot_market_to_point_a_client_at()
    {
        // The venue lists no S-prefixed spot pair - asked for SBTCSUSDT its spot catalog answers 40034 "Parameter
        // SBTCSUSDT does not exist" - so a demo spot client would connect and subscribe to nothing. Refused when the
        // client is built, which makes it a configuration error rather than a node that starts and stays silent.
        using TestKernel kernel = new();

        ArgumentException refused = Assert.Throws<ArgumentException>(() => new BitgetDataClient(
            new ClientId("BITGET"),
            new BitgetDataClientConfig { ProductType = BitgetProductType.Spot, TradingMode = BitgetTradingMode.Demo },
            kernel.Services));

        Assert.Contains("no demo spot market", refused.Message, StringComparison.Ordinal);
    }

    // ----- symbols, in both directions -----

    [Theory]

    // Spot keeps its own name: the venue spells a pair BASE+QUOTE with no separator, which was true of all 3169 it
    // listed, and that is what an instrument id wants.
    [InlineData(BitgetProductType.Spot, "BTCUSDT", "BTCUSDT.BITGET")]
    [InlineData(BitgetProductType.Spot, "1000BONKUSDT", "1000BONKUSDT.BITGET")]

    // A USDT perpetual gains the engine's suffix and nothing else.
    [InlineData(BitgetProductType.UsdtFutures, "BTCUSDT", "BTCUSDT-PERP.BITGET")]
    [InlineData(BitgetProductType.UsdtFutures, "1000000MOGUSDT", "1000000MOGUSDT-PERP.BITGET")]

    // A USDC perpetual gains its quote currency as well, because the venue leaves it out of the symbol. Without that
    // the id would be BTCPERP-PERP, which says nothing about what the position is margined in.
    [InlineData(BitgetProductType.UsdcFutures, "BTCPERP", "BTCUSDC-PERP.BITGET")]
    [InlineData(BitgetProductType.UsdcFutures, "1000BONKPERP", "1000BONKUSDC-PERP.BITGET")]
    public void A_venue_symbol_and_an_instrument_id_map_both_ways(BitgetProductType type, string raw, string id)
    {
        InstrumentId instrumentId = InstrumentId.Parse(id);

        Assert.Equal(instrumentId, BitgetVenue.ToInstrumentId(raw, type));
        Assert.Equal(raw, BitgetVenue.ToRawSymbol(instrumentId, type));
    }

    [Theory]
    [InlineData(BitgetProductType.UsdtFutures, "SBTCSUSDT", "SBTCSUSDT-PERP.BITGET")]
    [InlineData(BitgetProductType.UsdcFutures, "SBTCSPERP", "SBTCSUSDC-PERP.BITGET")]
    public void A_demo_symbol_maps_both_ways_too(BitgetProductType type, string raw, string id)
    {
        // The demo markets prefix their currencies as well as their product types, so a demo USDC contract is
        // SBTCSPERP and its quote currency is SUSDC. Measured on the three demo contracts the venue lists.
        InstrumentId instrumentId = InstrumentId.Parse(id);

        Assert.Equal(instrumentId, BitgetVenue.ToInstrumentId(raw, type, BitgetTradingMode.Demo));
        Assert.Equal(raw, BitgetVenue.ToRawSymbol(instrumentId, type, BitgetTradingMode.Demo));
    }

    [Fact]
    public void The_two_perpetual_families_cannot_collide_on_one_id()
    {
        // BTC against USDT and BTC against USDC are different contracts on different collateral, and the venue's own
        // spelling would not have distinguished them if the quote were dropped.
        Assert.NotEqual(
            BitgetVenue.ToInstrumentId("BTCUSDT", BitgetProductType.UsdtFutures),
            BitgetVenue.ToInstrumentId("BTCPERP", BitgetProductType.UsdcFutures));

        // And neither collides with the spot pair of the same name.
        Assert.NotEqual(
            BitgetVenue.ToInstrumentId("BTCUSDT", BitgetProductType.Spot),
            BitgetVenue.ToInstrumentId("BTCUSDT", BitgetProductType.UsdtFutures));
    }

    // ----- three spellings of a bar length -----

    private static BarSpecification Spec(string barType) => BarType.Parse("BTCUSDT.BITGET-" + barType + "-LAST-EXTERNAL").Spec;

    [Theory]
    [InlineData("1-MINUTE", "1min", "1m", "1m")]
    [InlineData("30-MINUTE", "30min", "30m", "30m")]
    [InlineData("1-HOUR", "1h", "1H", "1H")]
    [InlineData("4-HOUR", "4h", "4H", "4H")]
    [InlineData("1-DAY", "1day", "1D", "1D")]
    [InlineData("3-DAY", "3day", "3D", "3D")]
    [InlineData("1-WEEK", "1week", "1W", "1W")]
    [InlineData("1-MONTH", "1M", "1M", "1M")]
    public void One_bar_length_is_spelled_three_ways_on_one_venue(string barType, string spotRest, string futuresRest, string stream)
    {
        // The trap, measured on all three. The spot REST endpoint wants 1min and 1day; the derivative REST endpoint
        // wants 1m and 1D; the socket wants candle1m and candle1D on BOTH markets. A client that reused the spot REST
        // spelling on the socket subscribes to nothing - candle1min is refused with code 30016 - and a history fetch
        // that reused the socket's spelling on spot REST is refused with 400171.
        BarSpecification spec = Spec(barType);

        Assert.Equal(spotRest, BitgetVenue.RestGranularity(spec, BitgetProductType.Spot));
        Assert.Equal(futuresRest, BitgetVenue.RestGranularity(spec, BitgetProductType.UsdtFutures));
        Assert.Equal(stream, BitgetVenue.StreamGranularity(spec));
    }

    [Fact]
    public void The_lengths_the_two_rest_endpoints_keep_are_not_the_same()
    {
        // Two hours is served by the derivative endpoint and refused by the spot one, measured on both. The venue's
        // own refusal message names neither, which is why this is asserted rather than read.
        BarSpecification twoHours = Spec("2-HOUR");

        Assert.Equal("2H", BitgetVenue.RestGranularity(twoHours, BitgetProductType.UsdtFutures));
        Assert.Throws<NotSupportedException>(() => BitgetVenue.RestGranularity(twoHours, BitgetProductType.Spot));
    }

    [Fact]
    public void A_length_only_the_stream_carries_is_offered_there_and_refused_over_rest()
    {
        // Eight hours: accepted as candle8H by the socket on both markets, refused by both candle endpoints. A
        // subscription for it works, and a caller asking for its history is told rather than given something else.
        BarSpecification eightHours = Spec("8-HOUR");

        Assert.Equal("8H", BitgetVenue.StreamGranularity(eightHours));
        Assert.Throws<NotSupportedException>(() => BitgetVenue.RestGranularity(eightHours, BitgetProductType.Spot));
        Assert.Throws<NotSupportedException>(() => BitgetVenue.RestGranularity(eightHours, BitgetProductType.UsdtFutures));
    }

    [Fact]
    public void A_length_the_venue_does_not_keep_is_refused_rather_than_rounded()
    {
        BarSpecification twoMinutes = Spec("2-MINUTE");

        Assert.Throws<NotSupportedException>(() => BitgetVenue.StreamGranularity(twoMinutes));
        Assert.Throws<NotSupportedException>(() => BitgetVenue.RestGranularity(twoMinutes, BitgetProductType.Spot));
    }

    // ----- the caps, which are two different numbers on two endpoints -----

    [Fact]
    public void The_history_candle_page_is_the_smaller_of_the_venues_two_caps()
    {
        // Measured: market/candles serves 1000 and refuses 1200 with 40053 "limit should be between (0, 1000]", while
        // market/history-candles serves 200 and refuses 201 with 40053 "limit should be between 1~200". The smaller
        // one is what matters, because it is the endpoint that holds the venue's whole history: market/candles
        // answered a window six months old with an EMPTY LIST and a success code.
        Assert.Equal(200, BitgetVenue.HistoryCandlePage);
        Assert.Equal(1000, BitgetVenue.RecentCandlePage);
        Assert.True(BitgetVenue.HistoryCandlePage < BitgetVenue.RecentCandlePage);
    }

    [Fact]
    public void The_funding_page_is_the_size_the_venue_really_serves_and_not_the_one_it_accepts()
    {
        // Measured: pageSize 100 gives 100, and pageSize 200 and 500 give 100 as well with code 00000 and no warning.
        // A loop that decided a short page meant the end of history would have stopped after one page.
        Assert.Equal(100, BitgetVenue.FundingPage);
    }

    [Fact]
    public void A_ping_on_this_venue_is_not_json()
    {
        // Measured: the venue answers the literal four characters with the literal four characters. A client that
        // sent an envelope would be dropped for silence, and one that parsed the answer would log a warning every
        // twenty seconds for the life of the node.
        Assert.Equal("ping", BitgetVenue.PingMessage);
        Assert.Equal("pong", BitgetVenue.PongMessage);
    }

    [Fact]
    public void The_codes_that_mean_no_such_instrument_are_the_two_the_venue_really_sends()
    {
        // Measured on every endpoint this adapter asks about one instrument. 40034 is a name the venue has never had
        // and 40309 is one it has retired; both are refusals of a well-formed question.
        Assert.Equal(["40034", "40309"], BitgetVenue.ErrorsThatMeanNoSuchInstrument.Order(StringComparer.Ordinal));

        // And nothing else is: a key refusal or a rate limit must not read as "that instrument does not exist", or a
        // caller tells a person their symbol was wrong when the venue was unreachable.
        Assert.DoesNotContain(BitgetVenue.ErrorNoApiKey, BitgetVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.DoesNotContain(BitgetVenue.ErrorApiKeyUnknown, BitgetVenue.ErrorsThatMeanNoSuchInstrument);
    }

    [Fact]
    public void The_broker_id_travels_in_the_header_the_programme_issues()
    {
        // The venue runs a broker programme and publishes its mechanism: a channel API code in a header on the
        // request. It is outside the signature, so carrying one leaves the request the venue authenticates unchanged.
        Assert.Equal("X-CHANNEL-API-CODE", BitgetVenue.BrokerIdHeader);
        Assert.Equal(BrokerTag.RequestHeader, new BitgetPlugin().Describe().BrokerTag);
        Assert.Equal(BrokerProgramme.Carried, new BitgetPlugin().Describe().BrokerProgramme);
    }

    [Fact]
    public void Both_perpetual_families_hold_swaps_and_the_bigger_one_is_declared_first()
    {
        // The one thing about this declaration a reader should not take for granted. FamilyFor answers "which family
        // handles a swap here" with the first that claims the class, and this venue really does hold swaps two ways -
        // 805 contracts margined in USDT and 49 in USDC - so the order is a decision and not an accident.
        VenueDescriptor venue = new BitgetPlugin().Describe();
        VenueFamily[] swaps = [.. venue.Families.Where(f => f.InstrumentClasses.Contains(InstrumentClass.Swap))];

        Assert.Equal(["usdt-futures", "usdc-futures"], swaps.Select(f => f.Name));
        Assert.Equal("usdt-futures", venue.FamilyFor(InstrumentClass.Swap)?.Name);
        Assert.Equal("spot", venue.FamilyFor(InstrumentClass.Spot)?.Name);

        // And no dated future in either of them: the venue's only delivery contracts are coin-margined, in a product
        // type whose contract list is empty.
        Assert.Null(venue.FamilyFor(InstrumentClass.Future));
    }
}
