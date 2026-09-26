using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Okx;

// Why: this provider loads three markets from ONE endpoint, and the only thing that says which market a record
// belongs to is the venue's own instType. That makes it the place where the rule "never read a class out of a
// symbol's spelling" either holds or quietly does not, and this venue is the one where breaking it would be
// invisible: BTC-USD-SWAP is a perpetual and BTC-USD-261030 is a dated contract, BTC-USD_UM-261030 is linear and
// BTC-USD-261030 is inverse, and the difference between the last two is three characters in the middle of a name.
//
// The second thing pinned here is the margin. Two adapters in this engine hard-code an initial and maintenance rate
// of 5 and 2.5 percent, which is a defect being removed - and this venue shows why: its BTC perpetual starts at 1 and
// 0.4 percent while its linear BTC futures start at 5 and 2, on the same coin, on the same day. One number cannot be
// right for both.
//
// Every record below is the live venue's, field for field, recorded on 2026-09-25.
public sealed class OkxInstrumentProviderTests
{
    private const string InstrumentsPath = "/api/v5/public/instruments";
    private const string TiersPath = "/api/v5/public/position-tiers";

    /// <summary>
    /// The catalog endpoint serving whichever market is asked for, and the tier endpoint beside it. Keyed on the
    /// venue's own instType so that a provider asking for the wrong market gets the wrong catalog - which is what
    /// makes these tests able to say the instType is really being sent.
    /// </summary>
    private static Routes Catalog() => new Routes()
        .On("GET", InstrumentsPath, r => StubResponse.Json(r.Query("instType") switch
        {
            "SPOT" => OkxPayloads.SpotInstruments,
            "SWAP" => OkxPayloads.SwapInstruments,
            "FUTURES" => OkxPayloads.FuturesInstruments,
            _ => OkxPayloads.Error("51000", "Parameter instType error"),
        }))
        .On("GET", TiersPath, r => StubResponse.Json(r.Query("instType") switch
        {
            "SWAP" => OkxPayloads.SwapTiers,
            "FUTURES" => OkxPayloads.FuturesTiers,
            _ => OkxPayloads.Error("51000", "Parameter instType error"),
        }));

    private static OkxHttp Http(LoopbackServer server, OkxInstrumentType type) =>
        new(new OkxDataClientConfig { InstrumentType = type, BaseUrlHttp = server.HttpBase });

    private static async Task<OkxInstrumentProvider> LoadedAsync(LoopbackServer server, OkxInstrumentType type)
    {
        OkxInstrumentProvider provider = new(Http(server, type), type);
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- the class comes from the venue, not from the name -----

    [Theory]
    [InlineData(OkxInstrumentType.Spot, "BTC-USDT.OKX", InstrumentClass.Spot)]
    [InlineData(OkxInstrumentType.Swap, "BTC-USDT-SWAP.OKX", InstrumentClass.Swap)]
    [InlineData(OkxInstrumentType.Futures, "BTC-USD_UM-261030.OKX", InstrumentClass.Future)]
    public async Task Each_market_publishes_the_class_the_venue_says_its_records_are(OkxInstrumentType type, string id, InstrumentClass expected)
    {
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, type);

        Instrument instrument = Assert.IsAssignableFrom<Instrument>(provider.Find(InstrumentId.Parse(id)));
        Assert.Equal(expected, instrument.InstrumentClass);

        // And the request really named that market, which is the only thing that selects it here.
        Assert.Equal(OkxVenue.InstType(type), server.RequestsTo(InstrumentsPath)[0].Query("instType"));
    }

    [Fact]
    public async Task A_perpetual_and_a_dated_contract_with_almost_the_same_name_are_different_classes()
    {
        // The pair of ids that would defeat any rule about spelling. Both start "BTC-USD"; one is a perpetual and
        // one delivers, and nothing but the venue's instType distinguishes them.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider swaps = await LoadedAsync(server, OkxInstrumentType.Swap);
        OkxInstrumentProvider futures = await LoadedAsync(server, OkxInstrumentType.Futures);

        Assert.All(swaps.GetAll(), i => Assert.Equal(InstrumentClass.Swap, i.InstrumentClass));
        Assert.All(futures.GetAll(), i => Assert.Equal(InstrumentClass.Future, i.InstrumentClass));
    }

    [Fact]
    public async Task A_dated_contract_carries_the_dates_the_venue_publishes()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Futures);

        CryptoFuture contract = Assert.IsType<CryptoFuture>(provider.Find(InstrumentId.Parse("BTC-USD_UM-261030.OKX")));

        // expTime is the field that is an EMPTY STRING on a perpetual rather than absent, which is why it is only
        // read for this class - and 1793347200000 ms is 2026-10-30, which is what the id says.
        Assert.Equal(UnixNanos.FromMilliseconds(1_793_347_200_000L), contract.Expiration);
        Assert.Equal(UnixNanos.FromMilliseconds(1_787_904_600_714L), contract.Activation);
        Assert.Equal("BTC", contract.Underlying.Code);
    }

    // ----- inverse contracts -----

    [Theory]
    [InlineData(OkxInstrumentType.Swap, "BTC-USD-SWAP.OKX")]
    [InlineData(OkxInstrumentType.Futures, "BTC-USD-261030.OKX")]
    public async Task An_inverse_contract_is_left_out_of_both_derivative_markets(OkxInstrumentType type, string id)
    {
        // For the reason KuCoin's are left out: an inverse contract is quoted in USD and settles in the base
        // currency, so a quantity of one cannot be expressed in base units without a price - and every quantity
        // crossing this boundary is in base units. Fifteen of the venue's 492 perpetuals and twelve of its 244 dated
        // contracts are inverse.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, type);

        Assert.Null(provider.Find(InstrumentId.Parse(id)));
        Assert.NotEmpty(provider.GetAll());
    }

    [Fact]
    public async Task An_inverse_contract_is_told_apart_by_the_venues_field_and_not_by_its_name()
    {
        // BTC-USD-SWAP is inverse and BTC-USDT-SWAP is linear; the difference in the id is one letter. The provider
        // reads ctType, and this test would still pass if the venue renamed either of them.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Swap);

        Assert.Null(provider.Find(InstrumentId.Parse("BTC-USD-SWAP.OKX")));
        Assert.NotNull(provider.Find(InstrumentId.Parse("BTC-USDT-SWAP.OKX")));
    }

    // ----- sizes -----

    [Fact]
    public async Task A_perpetual_is_published_with_its_size_in_base_currency_rather_than_in_contracts()
    {
        // The whole of what this adapter hides. One BTC-USDT-SWAP contract is 0.01 BTC and the venue trades them in
        // hundredths, so the smallest tradable quantity is 0.0001 BTC - published exactly as the other venues
        // publish theirs, so a strategy sizing in base units is the same strategy everywhere.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Swap);

        CryptoPerpetual btc = Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("BTC-USDT-SWAP.OKX")));

        Assert.Equal("BTC-USDT-SWAP", btc.RawSymbol.Value);
        Assert.Equal("BTC", btc.BaseCurrency.Code);
        Assert.Equal("USDT", btc.QuoteCurrency.Code);
        Assert.Equal("USDT", btc.SettlementCurrency.Code);
        Assert.Equal(new Quantity(0.0001m, 4), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.0001m, 4), btc.MinQuantity);
        Assert.Equal(new Price(0.1m, 1), btc.PriceIncrement);

        // One, because a quantity is in base currency by the time anything above the adapter sees it, so notional is
        // quantity times price here as it is on every other venue.
        Assert.Equal(new Quantity(1m, 0), btc.Multiplier);

        // And the venue's contract value is on the instrument, where the adapter reads it and nothing else has to.
        Assert.Equal(0.01m, OkxVenue.ContractValue(btc));
    }

    [Fact]
    public async Task A_linear_dated_contract_takes_its_quote_currency_from_the_index_and_not_from_its_id()
    {
        // BTC-USD_UM-261030 settles in USD, is sized in BTC, and has an EMPTY quoteCcy. Its index is BTC-USD, so the
        // quote is USD - which no rule about taking the id apart gets right, because the id's middle segment is
        // "USD_UM".
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Futures);

        CryptoFuture contract = Assert.IsType<CryptoFuture>(provider.Find(InstrumentId.Parse("BTC-USD_UM-261030.OKX")));

        Assert.Equal("USD", contract.QuoteCurrency.Code);
        Assert.Equal("USD", contract.SettlementCurrency.Code);
        Assert.Equal("BTC", contract.BaseCurrency!.Code);
        Assert.Equal("BTC-USD", contract.Info!["underlying"]);
    }

    [Fact]
    public async Task A_spot_pair_is_published_with_the_venues_own_lot_size()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Spot);

        CurrencyPair btc = Assert.IsType<CurrencyPair>(provider.Find(InstrumentId.Parse("BTC-USDT.OKX")));

        Assert.Equal(new Quantity(0.00000001m, 8), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.00001m, 8), btc.MinQuantity);
        Assert.Equal(new Price(0.1m, 1), btc.PriceIncrement);
        Assert.Equal("USDT", btc.SettlementCurrency.Code);
    }

    [Fact]
    public async Task A_pair_the_venue_is_not_trading_is_left_out()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Spot);

        Assert.Null(provider.Find(InstrumentId.Parse("OLD-USDT.OKX")));
        Assert.Equal(2, provider.GetAll().Count);
    }

    // ----- margin, from the venue -----

    [Fact]
    public async Task A_perpetuals_margin_is_the_venues_first_tier_and_not_a_number_somebody_chose()
    {
        // Recorded live: BTC-USDT-SWAP's first tier is 1 percent initial and 0.4 percent maintenance, at 100x. The
        // hard-coded 5 and 2.5 percent two adapters in this engine ship with would be five times and six times too
        // much - which is a position that could not be opened and a liquidation price nowhere near the venue's.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Swap);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC-USDT-SWAP.OKX"))!;

        Assert.Equal(0.01m, btc.MarginInit);
        Assert.Equal(0.004m, btc.MarginMaint);
        Assert.Equal("100", btc.Info!["maxLeverage"]);
    }

    [Fact]
    public async Task A_dated_contracts_margin_is_five_times_its_perpetual_siblings_on_the_same_coin()
    {
        // The measurement that makes a single hard-coded number impossible: BTC's linear dated contract requires 5
        // percent initial and 2 percent maintenance at 20x, where its perpetual requires 1 and 0.4 at 100x. Both
        // recorded from the venue on the same day.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider futures = await LoadedAsync(server, OkxInstrumentType.Futures);

        Instrument contract = futures.Find(InstrumentId.Parse("BTC-USD_UM-261030.OKX"))!;

        Assert.Equal(0.05m, contract.MarginInit);
        Assert.Equal(0.02m, contract.MarginMaint);
        Assert.Equal("20", contract.Info!["maxLeverage"]);
    }

    [Fact]
    public async Task A_tier_request_names_the_families_and_asks_for_the_first_tier_only()
    {
        // The venue refuses this question by instType alone and refuses it by instId for a swap - both measured - so
        // the families are named. The tier is named too, because BTC-USDT's perpetual has 99 of them and an
        // instrument publishes the one a position being opened falls into.
        await using LoopbackServer server = new(Catalog().Handle);
        await LoadedAsync(server, OkxInstrumentType.Swap);

        RecordedRequest tiers = Assert.Single(server.RequestsTo(TiersPath));

        Assert.Equal("SWAP", tiers.Query("instType"));
        Assert.Equal("1", tiers.Query("tier"));
        Assert.Equal("cross", tiers.Query("tdMode"));
        Assert.Equal("BTC-USDT,ETH-USDT", tiers.Query("instFamily"));
    }

    [Fact]
    public async Task A_spot_pair_posts_no_margin_and_asks_for_no_tiers()
    {
        // A cash spot trade posts nothing, so zero is the right answer. The venue DOES publish margin tiers for the
        // same pair under instType=MARGIN - 10 percent initial - but that is its margin-trading product and this
        // family trades cash, so publishing those rates would say a spot purchase needs a tenth down.
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Spot);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC-USDT.OKX"))!;

        Assert.Equal(0m, btc.MarginInit);
        Assert.Equal(0m, btc.MarginMaint);
        Assert.Empty(server.RequestsTo(TiersPath));
    }

    [Fact]
    public async Task A_family_the_tier_endpoint_does_not_know_does_not_stop_the_catalog()
    {
        // One contract whose family has no tiers must not cost a whole catalog. The instruments are still published
        // and carry no margin, which is visible - where a guessed one would not be.
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, OkxPayloads.SwapInstruments)
            .On("GET", TiersPath, _ => StubResponse.Json(OkxPayloads.UnknownTierFamily))
            .Handle);

        OkxInstrumentProvider provider = await LoadedAsync(server, OkxInstrumentType.Swap);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC-USDT-SWAP.OKX"))!;
        Assert.Equal(0m, btc.MarginInit);

        // And the instrument's own published maximum leverage still stands in, so nothing is left with no answer.
        Assert.Equal("100", btc.Info!["maxLeverage"]);
    }

    [Fact]
    public async Task Tier_requests_are_batched_at_the_limit_the_venue_enforces()
    {
        // Six families in one request are refused with code 50025; five are answered. A catalog of six therefore
        // costs two requests, and a loop that sent all six would publish no margin at all for any of them.
        string[] families = ["A-USDT", "B-USDT", "C-USDT", "D-USDT", "E-USDT", "F-USDT"];
        string records = string.Join(',', families.Select((f, i) =>
            $$"""
            {"instType":"SWAP","instId":"{{f}}-SWAP","instFamily":"{{f}}","uly":"{{f}}","ctType":"linear","ctVal":"1","ctValCcy":"{{f[..1]}}","ctMult":"1","settleCcy":"USDT","quoteCcy":"","tickSz":"0.1","lotSz":"1","minSz":"1","maxLmtSz":"1000","lever":"10","state":"live","expTime":""}
            """));

        List<string> asked = [];
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => StubResponse.Json(OkxPayloads.Envelope("[" + records + "]")))
            .On("GET", TiersPath, r =>
            {
                asked.Add(r.Query("instFamily")!);
                return StubResponse.Json(OkxPayloads.Envelope("[]"));
            })
            .Handle);

        await LoadedAsync(server, OkxInstrumentType.Swap);

        Assert.Equal(2, asked.Count);
        Assert.Equal(OkxVenue.TierFamilyPage, asked[0].Split(',').Length);
        Assert.Single(asked[1].Split(','));
    }

    // ----- one instrument -----

    [Fact]
    public async Task Loading_one_instrument_asks_for_that_one_and_loads_that_one()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, r => StubResponse.Json(r.Query("instId") == "BTC-USDT-SWAP"
                ? OkxPayloads.SwapInstruments
                : OkxPayloads.NoSuchInstrument))
            .On("GET", TiersPath, OkxPayloads.SwapTiers)
            .Handle);

        OkxInstrumentProvider provider = new(Http(server, OkxInstrumentType.Swap), OkxInstrumentType.Swap);
        InstrumentId asked = InstrumentId.Parse("BTC-USDT-SWAP.OKX");

        await provider.LoadAsync(asked, CancellationToken.None);

        // Exactly one, even though the fixture the venue answered with carries three records. The venue does honour
        // its own instId filter - measured - so this is a guard rather than a filter, and it is what a caller asking
        // for one instrument relies on.
        Instrument loaded = Assert.Single(provider.GetAll());
        Assert.Equal(asked, loaded.Id);
        Assert.Equal(0.01m, loaded.MarginInit);
        Assert.Equal("BTC-USDT-SWAP", server.RequestsTo(InstrumentsPath)[0].Query("instId"));
    }

    [Theory]
    [InlineData(OkxInstrumentType.Spot, "NOTACOIN-USDT.OKX")]
    [InlineData(OkxInstrumentType.Swap, "NOTACOIN-USDT-SWAP.OKX")]
    [InlineData(OkxInstrumentType.Futures, "NOTACOIN-USD_UM-261030.OKX")]
    public async Task An_instrument_the_venue_does_not_list_leaves_the_provider_empty(OkxInstrumentType type, string id)
    {
        // The venue answers this with HTTP 200, code 51001 and an empty array - the same answer for all three
        // markets, which is not how the other multi-family venues here behave. A caller looks the instrument up
        // afterwards and finds nothing, which reads identically whoever answered.
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => StubResponse.Json(OkxPayloads.NoSuchInstrument))
            .Handle);

        OkxInstrumentProvider provider = new(Http(server, type), type);

        await provider.LoadAsync(InstrumentId.Parse(id), CancellationToken.None);

        Assert.Null(provider.Find(InstrumentId.Parse(id)));
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public async Task An_id_that_belongs_to_a_different_market_is_not_found_in_this_one()
    {
        // Measured: asked for the spot pair BTC-USDT under instType=SWAP, the venue refuses with the same
        // not-listed code rather than returning the pair. So a client pointed at the wrong market finds nothing,
        // which is the answer that lets something above it try the next family.
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, r => StubResponse.Json(r.Query("instType") == "SPOT"
                ? OkxPayloads.SpotInstruments
                : OkxPayloads.NoSuchInstrument))
            .Handle);

        OkxInstrumentProvider swaps = new(Http(server, OkxInstrumentType.Swap), OkxInstrumentType.Swap);

        await swaps.LoadAsync(InstrumentId.Parse("BTC-USDT.OKX"), CancellationToken.None);

        Assert.Empty(swaps.GetAll());
    }

    [Fact]
    public async Task A_failure_that_is_not_about_the_instrument_still_reaches_the_caller()
    {
        // The line this must not cross. Swallowing every refusal would turn a venue that is down or a key that is
        // wrong into "that instrument does not exist", and a caller would then tell somebody their symbol was bad
        // when the venue was unreachable. Only 51001 is an answer.
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => new StubResponse(429, OkxPayloads.Error("50011", "Requests too frequent")))
            .Handle);

        OkxInstrumentProvider provider = new(Http(server, OkxInstrumentType.Spot), OkxInstrumentType.Spot);

        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.LoadAsync(InstrumentId.Parse("BTC-USDT.OKX"), CancellationToken.None));
    }

    // ----- filters -----

    [Fact]
    public async Task A_quote_filter_keeps_only_the_instruments_settled_in_that_currency()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        OkxInstrumentProvider provider = new(Http(server, OkxInstrumentType.Spot), OkxInstrumentType.Spot);

        await provider.LoadAllAsync(
            CancellationToken.None,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["quote"] = "BTC" });

        Assert.Empty(provider.GetAll());
    }
}
