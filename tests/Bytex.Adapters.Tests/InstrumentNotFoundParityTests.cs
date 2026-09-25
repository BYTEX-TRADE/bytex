using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why: every venue is asked the same question - "load this instrument" - about a symbol it does not list, and each
// answered differently. Measured live on 2026-09-25, PER FAMILY, which is the part that matters:
//
//   BINANCE  spot          {"code":-1121,"msg":"Invalid symbol."}        throws
//   BINANCE  usdm-futures  HTTP 200 and ALL 909 CONTRACTS                ignores the symbol filter
//   BYBIT    spot          success, empty list                           adds nothing
//   BYBIT    linear        {"retCode":10001,"retMsg":"params error..."}   throws
//   KUCOIN   spot          {"code":"900001",...}                          throws
//   KUCOIN   futures       {"code":"404000",...}                          throws
//
// Six combinations, four behaviours, and BOTH multi-family venues disagree with themselves. A host cannot write one
// piece of code against that, so it writes six, or it writes one and breaks on the families it did not try - which
// is what happened: a person typing BTC where KuCoin's futures market says XBT got an HTTP 500 carrying the raw
// string "KuCoin error 404000", and the only way to stop it was for the caller to hold that number itself. Nothing
// above an adapter should know a venue's error codes; that is what adapters are for.
//
// Binance's futures family is the worst of the four and was not a crash at all. It ignores the symbol filter and
// answers with its entire contract list, so "load this one instrument" loaded 909 - for a known symbol, an unknown
// one, or anything else - and asking about a symbol that does not exist filled the provider with hundreds that do
// while still not finding the one requested. Nothing threw, so nothing noticed.
//
// Two rules, and between them every family behaves the same:
//   1. LoadAsync adds only the instrument that was ASKED FOR, whatever the venue chooses to return.
//   2. A refusal that means "not listed" leaves the provider empty instead of throwing.
//
// A caller looks the instrument up afterwards and finds nothing, and that reads identically whoever answered.
//
// This file had one row per venue when it was written, which is exactly how the Binance futures and Bybit linear
// families got missed: one venue, one row, one family actually exercised. It is keyed per FAMILY now.
public sealed class InstrumentNotFoundParityTests
{
/// <summary>Every venue-and-family combination that ships a catalog.</summary>
    public static TheoryData<string> Venues()
    {
        TheoryData<string> data = new();
        foreach (string family in Families)
        {
            data.Add(family);
        }

        return data;
    }

    /// <summary>
    /// Keyed per family, not per venue. Both multi-family venues answer this question differently in each of their
    /// families, so a per-venue table exercises one of them and declares the other covered.
    /// </summary>
    internal static readonly string[] Families =
    [
        "Binance.spot",
        "Binance.usdm-futures",
        "Bitget.spot",
        "Bitget.usdt-futures",
        "Bitget.usdc-futures",
        "Bybit.spot",
        "Bybit.linear",
        "Kucoin.spot",
        "Kucoin.futures",

        // Three families of one venue, and - unlike both of the multi-family venues above - all three answer this
        // question identically: HTTP 200, code 51001, "Instrument ID ... doesn't exist.", measured on each. That is
        // still three rows and not one, because a per-venue row is exactly how the Binance futures and Bybit linear
        // families came to be declared covered by their siblings' answers.
        "Okx.spot",
        "Okx.swap",
        "Okx.futures",

        // And the fifth venue disagrees with itself as well, in a way neither of the other two does: its spot
        // platform refuses the question with a named token, and its futures platform IGNORES the symbol filter and
        // answers with every contract it lists. That is Binance's USD-margined defect on a second venue.
        "Kraken.spot",
        "Kraken.futures",
    ];

private static (string Method, string Path, int Status, string Body) NotFound(string family) => family switch
    {
        // HTTP 400 with the code in the body: this family honours the filter and refuses.
        "Binance.spot" => ("GET", "/api/v3/exchangeInfo", 400, """{"code":-1121,"msg":"Invalid symbol."}"""),

        // No refusal at all - the filter is ignored and the whole contract list comes back. Two real contracts
        // stand for the 909 the live venue sends, and neither is the one being asked about.
        "Binance.usdm-futures" => ("GET", "/fapi/v1/exchangeInfo", 200, """
            {"timezone":"UTC","serverTime":1700000000000,"futuresType":"U_MARGINED","symbols":[
              {"symbol":"BTCUSDT","pair":"BTCUSDT","contractType":"PERPETUAL","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT","marginAsset":"USDT","pricePrecision":2,"quantityPrecision":3,
               "filters":[{"filterType":"PRICE_FILTER","minPrice":"0.10","maxPrice":"1000000","tickSize":"0.10"},{"filterType":"LOT_SIZE","minQty":"0.001","maxQty":"1000","stepSize":"0.001"}]},
              {"symbol":"ETHUSDT","pair":"ETHUSDT","contractType":"PERPETUAL","status":"TRADING","baseAsset":"ETH","quoteAsset":"USDT","marginAsset":"USDT","pricePrecision":2,"quantityPrecision":3,
               "filters":[{"filterType":"PRICE_FILTER","minPrice":"0.01","maxPrice":"100000","tickSize":"0.01"},{"filterType":"LOT_SIZE","minQty":"0.001","maxQty":"10000","stepSize":"0.001"}]}
            ]}
            """),

        // Success with an empty list - this family never refuses the question.
        "Bybit.spot" => ("GET", "/v5/market/instruments-info", 200, """{"retCode":0,"retMsg":"OK","result":{"category":"spot","list":[]},"retExtInfo":{},"time":1790319460076}"""),

        // And the other family of the same venue refuses it, with the venue's parameter-error code.
        "Bybit.linear" => ("GET", "/v5/market/instruments-info", 200, """{"retCode":10001,"retMsg":"params error: symbol invalid","result":{"category":"","list":[],"nextPageCursor":""},"retExtInfo":{},"time":1790320625451}"""),

        // HTTP 400 with the venue's own code in the body, measured live on all three families: 40034 "Parameter
        // NOTACOINUSDT does not exist". This venue is the only one of the four whose families agree with each other
        // about it, and the only one that answers the same way on every endpoint that takes a symbol.
        "Bitget.spot" => ("GET", BitgetInstrumentProvider.SpotSymbolsPath, 400, BitgetPayloads.Error("40034", "Parameter NOTACOINUSDT does not exist")),
        "Bitget.usdt-futures" => ("GET", BitgetInstrumentProvider.ContractsPath, 400, BitgetPayloads.Error("40034", "Parameter NOTACOINUSDT does not exist")),
        "Bitget.usdc-futures" => ("GET", BitgetInstrumentProvider.ContractsPath, 400, BitgetPayloads.Error("40034", "Parameter NOTACOINPERP does not exist")),

        // HTTP 200 with the refusal in the body, which is why a caller checking the status learns nothing.
        "Kucoin.spot" => ("GET", "/api/v2/symbols/NOTACOIN-USDT", 200, """{"msg":"Trading pair NOTACOIN-USDT does not exist.","code":"900001"}"""),
        "Kucoin.futures" => ("GET", "/api/v1/contracts/NOTACOINUSDTM", 200, """{"msg":"The contract information you requested does not exist.","code":"404000"}"""),

        // One endpoint and one answer for all three OKX markets, with HTTP 200 - so a caller checking the status
        // learns nothing here either. Measured for each market in turn, and measured again for an id that exists in
        // a DIFFERENT market: asked for the spot pair BTC-USDT under instType=SWAP, the venue gives this same
        // refusal rather than the pair or its whole catalog.
        "Okx.spot" or "Okx.swap" or "Okx.futures" => ("GET", "/api/v5/public/instruments", 200, """{"code":"51001","data":[],"msg":"Instrument ID, Instrument ID code, or Spread ID doesn't exist."}"""),

        // HTTP 200 with the refusal in an error ARRAY, which is this platform's shape for every failure.
        "Kraken.spot" => ("GET", "/0/public/AssetPairs", 200, KrakenPayloads.UnknownPair),

        // No refusal at all: the catalog ignores its symbol filter and answers with the whole list. Measured -
        // ?symbol=PF_XBTUSD returned all 300 contracts - and there is no per-symbol endpoint to ask instead, so
        // "load this one instrument" is a request for the venue unless the adapter keeps only the one it asked for.
        "Kraken.futures" => ("GET", "/derivatives/api/v3/instruments", 200, KrakenPayloads.Instruments),

        _ => throw new InvalidOperationException(
            $"{family} ships a catalog and there is no recorded answer here for an instrument it does not list. Add "
            + "what the LIVE family really says - its sibling's answer is not it, as both of these venues prove."),
    };

private static InstrumentId Unknown(string family) => InstrumentId.Parse(family switch
    {
        "Binance.spot" => "NOTACOINUSDT.BINANCE",
        "Binance.usdm-futures" => "NOTACOINUSDT-PERP.BINANCE",
        "Bybit.spot" => "NOTACOINUSDT.BYBIT",
        "Bybit.linear" => "NOTACOINUSDT-PERP.BYBIT",
        "Bitget.spot" => "NOTACOINUSDT.BITGET",
        "Bitget.usdt-futures" => "NOTACOINUSDT-PERP.BITGET",
        "Bitget.usdc-futures" => "NOTACOINUSDC-PERP.BITGET",
        "Kucoin.spot" => "NOTACOIN-USDT.KUCOIN",
        "Kucoin.futures" => "NOTACOINUSDT-PERP.KUCOIN",

        // The venue's own spellings, unchanged: this adapter adds no suffix and strips none, because OKX already
        // distinguishes its three markets in the id itself.
        "Okx.spot" => "NOTACOIN-USDT.OKX",
        "Okx.swap" => "NOTACOIN-USDT-SWAP.OKX",
        "Okx.futures" => "NOTACOIN-USD_UM-261030.OKX",

        // A dash where the wire name has a slash, as every Kraken spot id does; and on futures the venue's own
        // contract symbol, prefix and all.
        "Kraken.spot" => "NOTACOIN-USD.KRAKEN",
        "Kraken.futures" => "PF_NOTACOINUSD.KRAKEN",

        _ => throw new InvalidOperationException(family),
    });

private static InstrumentProviderBase Provider(string family, LoopbackServer server) => family switch
    {
        "Binance.spot" => new BinanceInstrumentProvider(
            new BinanceHttp(new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot, BaseUrlHttp = server.HttpBase }),
            BinanceAccountType.Spot),
        "Binance.usdm-futures" => new BinanceInstrumentProvider(
            new BinanceHttp(new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = server.HttpBase }),
            BinanceAccountType.UsdMFutures),
        "Bybit.spot" => new BybitInstrumentProvider(
            new BybitHttp(new BybitDataClientConfig { ProductType = BybitProductType.Spot, BaseUrlHttp = server.HttpBase }),
            BybitProductType.Spot),
        "Bybit.linear" => new BybitInstrumentProvider(
            new BybitHttp(new BybitDataClientConfig { ProductType = BybitProductType.Linear, BaseUrlHttp = server.HttpBase }),
            BybitProductType.Linear),
        "Bitget.spot" => new BitgetInstrumentProvider(
            new BitgetHttp(new BitgetDataClientConfig { ProductType = BitgetProductType.Spot, BaseUrlHttp = server.HttpBase })),
        "Bitget.usdt-futures" => new BitgetInstrumentProvider(
            new BitgetHttp(new BitgetDataClientConfig { ProductType = BitgetProductType.UsdtFutures, BaseUrlHttp = server.HttpBase })),
        "Bitget.usdc-futures" => new BitgetInstrumentProvider(
            new BitgetHttp(new BitgetDataClientConfig { ProductType = BitgetProductType.UsdcFutures, BaseUrlHttp = server.HttpBase })),
        "Kucoin.spot" => new KucoinInstrumentProvider(
            new KucoinHttp(new KucoinDataClientConfig { BaseUrlHttp = server.HttpBase })),
        "Kucoin.futures" => new KucoinFuturesInstrumentProvider(
            new KucoinHttp(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures, BaseUrlHttp = server.HttpBase })),
        "Okx.spot" => OkxProvider(server, OkxInstrumentType.Spot),
        "Okx.swap" => OkxProvider(server, OkxInstrumentType.Swap),
        "Okx.futures" => OkxProvider(server, OkxInstrumentType.Futures),
        "Kraken.spot" => new KrakenInstrumentProvider(
            new KrakenHttp(new KrakenDataClientConfig { BaseUrlHttp = server.HttpBase })),
        "Kraken.futures" => new KrakenFuturesInstrumentProvider(
            new KrakenHttp(new KrakenDataClientConfig { ProductType = KrakenProductType.Futures, BaseUrlHttp = server.HttpBase })),
        _ => throw new InvalidOperationException(family),
    };

    /// <summary>One provider per OKX market, all three from the same class: on this venue the market is a parameter.</summary>
    private static InstrumentProviderBase OkxProvider(LoopbackServer server, OkxInstrumentType type) =>
        new OkxInstrumentProvider(
            new OkxHttp(new OkxDataClientConfig { InstrumentType = type, BaseUrlHttp = server.HttpBase }),
            type);

    [Theory]
    [MemberData(nameof(Venues))]
public async Task Asking_a_family_for_an_instrument_it_does_not_list_leaves_the_provider_empty(string family)
    {
        (string method, string path, int status, string body) = NotFound(family);

        await using LoopbackServer server = new(new Routes()
            .On(method, path, _ => new StubResponse(status, body))
            .Handle);

        InstrumentProviderBase provider = Provider(family, server);

        // No throw: the family answered the question, and the answer was no.
        await provider.LoadAsync(Unknown(family), CancellationToken.None);

        Assert.Null(provider.Find(Unknown(family)));

        // And nothing else either. This is what Binance's futures family failed: it answers an unknown symbol with
        // its whole contract list, so the provider filled with instruments nobody asked about while the one that
        // was asked about was still missing.
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public async Task Loading_one_instrument_loads_one_even_when_the_family_answers_with_all_of_them()
    {
        // The same defect from the other side, and the part that is not about not-found at all: Binance's USD-margined
        // futures ignores the symbol filter outright. Asked for BTCUSDT it returned all 909 contracts - measured -
        // so loading a single instrument loaded the venue, every time, for every caller.
        (_, string path, int status, string body) = NotFound("Binance.usdm-futures");

        await using LoopbackServer server = new(new Routes()
            .On("GET", path, _ => new StubResponse(status, body))
            .Handle);

        InstrumentProviderBase provider = Provider("Binance.usdm-futures", server);
        InstrumentId asked = InstrumentId.Parse("BTCUSDT-PERP.BINANCE");

        await provider.LoadAsync(asked, CancellationToken.None);

        Instrument loaded = Assert.Single(provider.GetAll());
        Assert.Equal(asked, loaded.Id);
    }

    [Fact]
    public void A_venue_failure_that_is_not_about_the_symbol_is_still_a_failure()
    {
        // The line this must not cross. Swallowing every refusal would turn a venue that is down, a key that is
        // wrong or an address that is blocked into "that instrument does not exist" - a caller would then tell a
        // person their symbol was bad when the venue was unreachable. Only the not-found codes are answers.
        Assert.DoesNotContain("400002", KucoinVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.DoesNotContain("429000", KucoinVenue.ErrorsThatMeanNoSuchInstrument);
        Assert.Equal(["404000", "900001"], KucoinVenue.ErrorsThatMeanNoSuchInstrument.Order(StringComparer.Ordinal).ToArray());

        // And the two sets are disjoint: a code cannot be both "your key version is wrong" and "no such symbol".
        Assert.Empty(KucoinVenue.ErrorsThatMeanNoSuchInstrument.Intersect(KucoinVenue.ErrorsThatAreNotTheKeyVersion, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(429, """{"code":-1003,"msg":"Too many requests."}""")]
    [InlineData(401, """{"code":-2015,"msg":"Invalid API-key."}""")]
    public async Task A_binance_failure_that_is_not_an_unknown_symbol_still_reaches_the_caller(int status, string body)
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v3/exchangeInfo", _ => new StubResponse(status, body))
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot, BaseUrlHttp = server.HttpBase });
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot);

        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.LoadAsync(InstrumentId.Parse("BTCUSDT.BINANCE"), CancellationToken.None));
    }

    [Fact]
    public void Every_venue_with_an_instrument_provider_is_in_this_table()
    {
        // The half that catches the next venue, and the half that failed: this table had a row per VENUE, so
        // Binance's futures family and Bybit's linear family were declared covered by their siblings' answers. They
        // behave differently from them, and one of the two was silently loading the whole venue.
        Assert.Equal(Families.Length, Venues().Cast<object>().Count());

        // Every family of every declared venue is named here. The declaration already says which families exist, so
        // this reads them from it rather than from a list somebody has to remember to extend.
        foreach ((string venue, VenueDescriptor descriptor) in Declared())
        {
            foreach (VenueFamily family in descriptor.Families)
            {
                string key = $"{venue}.{family.Name}";
                Assert.True(
                    Families.Contains(key, StringComparer.Ordinal),
                    $"{key} is a family this venue declares and there is no recorded answer here for an instrument "
                    + "it does not list. Its sibling family's answer is not evidence: on both multi-family venues "
                    + "that ship today, the two families answer this differently.");
            }
        }

        // And nothing here describes a family that no longer exists.
        foreach (string key in Families)
        {
            string venue = key[..key.IndexOf('.', StringComparison.Ordinal)];
            string name = key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..];
            Assert.Contains(
                name,
                Declared().Single(d => d.Venue == venue).Descriptor.Families.Select(f => f.Name));
        }
    }

    /// <summary>The venues that declare themselves, which is where the list of families comes from.</summary>
    private static IReadOnlyList<(string Venue, VenueDescriptor Descriptor)> Declared()
    {
        List<(string, VenueDescriptor)> found = [];
        foreach (string venue in Repo.ShippedVenues())
        {
            Type? plugin = System.Reflection.Assembly.Load("Bytex.Adapters." + venue).GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IVenuePlugin).IsAssignableFrom(t));

            if (plugin is not null)
            {
                found.Add((venue, ((IVenuePlugin)Activator.CreateInstance(plugin)!).Describe()));
            }
        }

        return found;
    }
}
