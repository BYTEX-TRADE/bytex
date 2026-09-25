using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why (E16): a venue's families are separate APIs, so loading an instrument means first choosing which client to
// build - and that choice has to be made before anything can be loaded. Hosts were making it by reading the symbol's
// spelling, which is a convention and not a fact: "-PERP" is what Binance and Bybit happen to call a perpetual,
// KuCoin's futures market calls the same thing XBTUSDTM, and a venue naming one some other way would be read as spot
// and loaded through the wrong API.
//
// So it is asked instead. Loading one instrument needs no key, so each family is asked for that single id and the
// family that answers owns it - three unauthenticated requests at worst on a three-family venue, once.
//
// This is the last of the five readings the declaration exists to remove. The other four - key shapes, node
// configuration, fee defaults, which venue publishes a book archive - became fields; this one could not, because no
// field can tell you which family holds an id the venue has never been asked about.
public sealed class VenueFamilyResolverTests
{
    private static readonly InstrumentId _spot = InstrumentId.Parse("BTC-USDT.KUCOIN");
    private static readonly InstrumentId _perp = InstrumentId.Parse("XBTUSDT-PERP.KUCOIN");

    /// <summary>
    /// A resolver over the real KuCoin declaration, with each family's real provider pointed at a stub venue. KuCoin
    /// is the venue this is built for: two families, and the only one where the id's spelling genuinely differs
    /// between them.
    /// </summary>
    private static VenueFamilyResolver Resolver(LoopbackServer server, List<string>? asked = null)
    {
        VenueDescriptor venue = new KucoinPlugin().Describe();
        return new VenueFamilyResolver(venue, family =>
        {
            asked?.Add(family.Name);
            KucoinDataClientConfig config = new()
            {
                ProductType = family.Name == "futures" ? KucoinProductType.Futures : KucoinProductType.Spot,
                BaseUrlHttp = server.HttpBase,
            };

            KucoinHttp http = new(config);
            return family.Name == "futures"
                ? new KucoinFuturesInstrumentProvider(http)
                : new KucoinInstrumentProvider(http);
        });
    }

    private static Routes Venue() => new Routes()
        .On("GET", "/api/v2/symbols/BTC-USDT", KucoinPayloads.Envelope("""
            {"symbol":"BTC-USDT","name":"BTC-USDT","baseCurrency":"BTC","quoteCurrency":"USDT","feeCurrency":"USDT",
             "market":"USDS","baseMinSize":"0.00001","quoteMinSize":"0.1","baseMaxSize":"10000000000",
             "quoteMaxSize":"99999999","baseIncrement":"0.00000001","quoteIncrement":"0.000001",
             "priceIncrement":"0.1","minFunds":"0.1","isMarginEnabled":true,"enableTrading":true,
             "feeCategory":1,"makerFeeCoefficient":"1.00","takerFeeCoefficient":"1.00","st":false}
            """))
        .On("GET", "/api/v1/contracts/XBTUSDTM", KucoinPayloads.Envelope("""
            {"symbol":"XBTUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USDT",
             "settleCurrency":"USDT","multiplier":0.001,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,
             "maxPrice":1000000.0,"makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":null}
            """));

    /// <summary>What the venue says about a symbol it does not list, on each family's own route.</summary>
    private static Routes WithNotFound(Routes routes) => routes
        .On("GET", "/api/v2/symbols/NOTACOIN-USDT", KucoinPayloads.Error("900001", "Trading pair NOTACOIN-USDT does not exist."))
        .On("GET", "/api/v1/contracts/NOTACOINM", KucoinPayloads.Error("404000", "The contract information you requested does not exist."));

    [Fact]
    public async Task A_spot_pair_resolves_to_the_spot_family()
    {
        await using LoopbackServer server = new(Venue().Handle);

        VenueFamilyMatch? match = await Resolver(server).ResolveAsync(_spot);
        Assert.NotNull(match);

        Assert.Equal("spot", match!.Family.Name);
        Assert.Equal(_spot, match!.Instrument.Id);
        Assert.Equal(InstrumentClass.Spot, match!.Instrument.InstrumentClass);
    }

    [Fact]
    public async Task A_perpetual_resolves_to_the_futures_family_without_anything_reading_its_name()
    {
        // The case the spelling was being read for. Nothing here looks at "-PERP", at "M", or at any part of the
        // symbol: the futures family is the one that answered.
        await using LoopbackServer server = new(Venue().Handle);

        VenueFamilyMatch? match = await Resolver(server).ResolveAsync(_perp);
        Assert.NotNull(match);

        Assert.Equal("futures", match!.Family.Name);
        Assert.Equal(_perp, match!.Instrument.Id);
        Assert.Equal(InstrumentClass.Swap, match!.Instrument.InstrumentClass);

        // And the instrument comes back as THAT family describes it - the contract size already in base currency,
        // which is the thing a caller would otherwise have had to know to ask the right family for.
        Assert.Equal(0.001m, match!.Instrument.SizeIncrement.Value);
    }

    [Fact]
    public async Task Asking_stops_at_the_family_that_answers()
    {
        // Spot is declared first and holds the pair, so the futures API is never asked. It matters because each
        // family asked is a request to a venue, and a host resolving a list of instruments pays for every one.
        List<string> asked = [];
        await using LoopbackServer server = new(Venue().Handle);

        await Resolver(server, asked).ResolveAsync(_spot);

        Assert.Equal(["spot"], asked);
    }

    [Fact]
    public async Task Every_family_is_asked_before_the_answer_is_no()
    {
        List<string> asked = [];
        await using LoopbackServer server = new(WithNotFound(Venue()).Handle);

        VenueFamilyMatch? match = await Resolver(server, asked)
            .ResolveAsync(InstrumentId.Parse("NOTACOIN-USDT.KUCOIN"));

        Assert.Null(match);
        Assert.Equal(["spot", "futures"], asked);
    }

    [Fact]
    public async Task A_family_that_could_not_be_asked_does_not_count_as_a_no()
    {
        // The distinction that matters for what a caller then tells a person. A venue briefly unreachable must not
        // read as an instrument that does not exist - so a family that failed to answer is passed over, and "no
        // family has it" is only returned when every family really was asked and really said no.
        //
        // Here spot is broken and futures holds the instrument: the answer is the futures family, not a failure.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v2/symbols/XBTUSDT-PERP", _ => new StubResponse(500, "gateway is having a moment"))
            .On("GET", "/api/v1/contracts/XBTUSDTM", KucoinPayloads.Envelope("""
                {"symbol":"XBTUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USDT",
                 "settleCurrency":"USDT","multiplier":0.001,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,
                 "maxPrice":1000000.0,"makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":null}
                """))
            .Handle);

        VenueFamilyMatch? match = await Resolver(server).ResolveAsync(_perp);
        Assert.NotNull(match);
        Assert.Equal("futures", match!.Family.Name);
    }

    [Fact]
    public async Task An_instrument_of_another_venue_is_not_asked_about_at_all()
    {
        List<string> asked = [];
        await using LoopbackServer server = new(Venue().Handle);

        Assert.Null(await Resolver(server, asked).ResolveAsync(InstrumentId.Parse("BTCUSDT.BINANCE")));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task A_family_that_cannot_fetch_one_instrument_is_skipped_rather_than_asked_and_failed()
    {
        // What the capability is for. A family that cannot load a single instrument by name has nothing to
        // contribute here, and asking it would spend a request to learn what its declaration already says.
        List<string> asked = [];
        await using LoopbackServer server = new(Venue().Handle);

        VenueDescriptor venue = new KucoinPlugin().Describe();
        VenueDescriptor crippled = venue with
        {
            Families = [.. venue.Families.Select(f => f with
            {
                Capabilities = f.Capabilities with { LoadOneInstrument = f.Name != "spot" },
            })],
        };

        VenueFamilyResolver resolver = new(crippled, family =>
        {
            asked.Add(family.Name);
            KucoinHttp http = new(new KucoinDataClientConfig
            {
                ProductType = family.Name == "futures" ? KucoinProductType.Futures : KucoinProductType.Spot,
                BaseUrlHttp = server.HttpBase,
            });

            return family.Name == "futures"
                ? new KucoinFuturesInstrumentProvider(http)
                : (IInstrumentProvider)new KucoinInstrumentProvider(http);
        });

        await resolver.ResolveAsync(_perp);

        Assert.Equal(["futures"], asked);
    }

    [Fact]
    public void A_class_already_known_needs_no_request()
    {
        // When the class is in hand - from an instrument already loaded, or a person choosing "perpetuals" rather
        // than a symbol - the declaration answers on its own. It is not a substitute for asking when all that is
        // held is an id, which is the case the resolver exists for.
        VenueFamilyResolver resolver = new(new KucoinPlugin().Describe(), _ => throw new InvalidOperationException("no request should be made"));

        Assert.Equal("spot", resolver.ForClass(InstrumentClass.Spot)?.Name);
        Assert.Equal("futures", resolver.ForClass(InstrumentClass.Swap)?.Name);

        // And a class this venue does not offer is null rather than a guess: KuCoin's only dated contracts are
        // inverse, so its linear family has no dated futures at all.
        Assert.Null(resolver.ForClass(InstrumentClass.Future));
    }
}
