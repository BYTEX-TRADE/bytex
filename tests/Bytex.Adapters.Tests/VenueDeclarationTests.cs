using System.Reflection;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Serialization;

namespace Bytex.Adapters.Tests;

// Why: everything above an adapter was hard-coding what its venue is. Which markets it covers, what a key for it is
// made of, which setting selects its futures rather than its spot, where it answers, what it charges before an
// instrument is loaded - all of it was being read out of this source by hand, per venue, per host, and one of it was
// being inferred from how a symbol is spelled. Six venues arrive in 0.7. Read-the-source-per-venue does not survive
// six, and the failure it produces is the quiet one: a node that starts, connects and never receives anything.
//
// So an adapter declares its venue, and this file is what makes a declaration worth trusting. Every fact in it is
// checked against the thing it describes - the hosts against what the adapter really talks to, the config values
// against what really selects that family, the instrument classes against what the provider really returns, the key
// against every variable the adapter really reads. A declaration that drifts from its adapter fails here, which is
// the whole point: an undeclared venue is obvious the moment somebody tries to use it, but a WRONG declaration is
// believed, and a host acting on it is broken in a way that looks like the venue's fault.
public sealed class VenueDeclarationTests
{
    /// <summary>
    /// The venues that declare themselves, found by asking the shipped adapters rather than by a list here: a list
    /// in a test is right until the next adapter lands without knowing the list exists.
    /// </summary>
    private static IReadOnlyList<(string Venue, VenueDescriptor Descriptor)> Declarations()
    {
        List<(string, VenueDescriptor)> found = [];
        foreach (string venue in Repo.ShippedVenues())
        {
            Type? plugin = Assembly.Load("Bytex.Adapters." + venue).GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IVenuePlugin).IsAssignableFrom(t));

            if (plugin is not null)
            {
                found.Add((venue, ((IVenuePlugin)Activator.CreateInstance(plugin)!).Describe()));
            }
        }

        return found;
    }

    public static TheoryData<string, string> Families()
    {
        TheoryData<string, string> data = new();
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            foreach (VenueFamily family in descriptor.Families)
            {
                data.Add(venue, family.Name);
            }
        }

        return data;
    }

    private static VenueFamily Family(string venue, string name) =>
        Declarations().Single(d => d.Venue == venue).Descriptor.Families.Single(f => f.Name == name);

    // ----- the bridge -----
    //
    // Below this line the test knows venue specifics, because something has to and a test is the right place: it is
    // where the declaration is compared with the adapter. Nothing in src above an adapter may know any of it, which
    // is what the declaration exists to make possible and what these tests exist to keep true.

    /// <summary>
    /// A data-client configuration for one family, built the way a host would build one: by applying the settings the
    /// family declares, by name, with no knowledge of what they mean. If a declared name is not a field or a declared
    /// value is not one the field accepts, this is where it is found out.
    /// </summary>
    private static object Configure(string venue, VenueFamily family, string? httpBase = null, string? wsBase = null)
    {
        Type type = Assembly.Load("Bytex.Adapters." + venue)
            .GetType($"Bytex.Adapters.{venue}.{venue}DataClientConfig", throwOnError: true)!;
        object config = Activator.CreateInstance(type)!;

        foreach ((string name, string value) in family.Config)
        {
            PropertyInfo? property = type.GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                property is not null,
                $"{venue}'s {family.Name} family says to set \"{name}\", and a {type.Name} has no such field. A host "
                + "applying this declaration would set nothing and get whatever the default family is.");

            Type target = Nullable.GetUnderlyingType(property!.PropertyType) ?? property.PropertyType;
            object parsed;
            try
            {
                parsed = target.IsEnum ? Enum.Parse(target, value) : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException)
            {
                Assert.Fail(
                    $"{venue}'s {family.Name} family says to set \"{name}\" to \"{value}\", which a {target.Name} "
                    + $"does not accept{(target.IsEnum ? ". It accepts " + string.Join(", ", Enum.GetNames(target)) : string.Empty)}.");
                throw;
            }

            property.SetValue(config, parsed);
        }

        if (httpBase is not null)
        {
            type.GetProperty("BaseUrlHttp")!.SetValue(config, httpBase);
        }

        if (wsBase is not null)
        {
            type.GetProperty("BaseUrlWs")!.SetValue(config, wsBase);
        }

        return config;
    }

    /// <summary>
    /// Where a configured client really talks, asked of the adapter's own resolver - and, where the address is not
    /// what distinguishes one family from another, which market it really asks for.
    /// <para>
    /// The third element exists for Bitget and would have been wrong to leave out. On the three venues that shipped
    /// first, a family is selected by somewhere to go: Binance's two families answer on different hosts and Bybit's
    /// socket is opened on a different path. Bitget's three answer on one host and one socket path, and what selects
    /// a family is a string the adapter puts in every request - the instrument type a subscription names and the
    /// product type a REST call carries. Without asking for that, nothing here could tell whether a declared setting
    /// landed on the family it claimed, which is exactly the failure this file exists to catch: a node that starts,
    /// connects and never receives anything.
    /// </para>
    /// <para>
    /// It is null where a venue really has nothing of the kind, which is a statement and not a gap: those venues are
    /// distinguished by their addresses, and a selector invented for them would be this test's idea rather than the
    /// adapter's.
    /// </para>
    /// </summary>
    private static (string Http, string? Ws, string? Market) Talks(string venue, object config) => venue switch
    {
        "Binance" => (BinanceVenue.HttpBase((IBinanceSettings)config), BinanceVenue.WsBase((IBinanceSettings)config), null),
        "Bitget" => (BitgetVenue.HttpBase((IBitgetSettings)config), BitgetVenue.WsPublic((IBitgetSettings)config), BitgetVenue.Market((IBitgetSettings)config)),
        "Bybit" => (BybitVenue.HttpBase((IBybitSettings)config), BybitVenue.WsPublic((IBybitSettings)config), null),

        // KuCoin has no socket base to resolve: the venue answers a REST call with the address, per connection.
        "Kucoin" => (KucoinVenue.HttpBase((IKucoinSettings)config), null, null),

        // OKX has one host and one socket root for all three markets, which is why the answer here is the same for
        // every family it declares. What selects a family is an instType parameter the adapter puts on each request
        // and does not resolve through a settings reader the way Bitget's product type is, so there is no third
        // string to ask it for either. The test below that asks "do the declared settings really pick this family"
        // therefore cannot be answered by an address or a market on this venue - it is answered by the catalog.
        "Okx" => (OkxVenue.HttpBase((IOkxSettings)config), OkxVenue.WsBase((IOkxSettings)config), null),

        // Kraken's spot family declares its PUBLIC socket base, which is what a data client opens: the venue serves
        // public and private data on two different hosts and refuses each on the other's, so the private host is
        // derived from this one rather than declared beside it.
        "Kraken" => (KrakenVenue.HttpBase((IKrakenSettings)config), KrakenVenue.WsBase((IKrakenSettings)config), null),

        // Gate's three families differ in BOTH: spot answers on the general host and its own socket, and the two
        // derivative markets on the derivatives host with a socket path per settled market. The address alone tells
        // them apart, so there is no market selector to ask this venue for.
        "Gate" => (GateVenue.HttpBase((IGateSettings)config), GateVenue.WsBase((IGateSettings)config), null),

        // One host for everything and a fixed socket, which is why both are strings here where KuCoin's socket is
        // a null. The declared bases are ROOTS - /info, /exchange and /ws hang off them - so this compares roots.
        // This venue declares one family, so there is nothing for a market selector to tell apart.
        "Hyperliquid" => (HyperliquidVenue.HttpBase((IHyperliquidSettings)config), HyperliquidVenue.WsBase((IHyperliquidSettings)config), null),

        _ => throw new InvalidOperationException(
            $"{venue} declares itself and this test does not know how to ask it where it talks. Add it here - the "
            + "declaration is only worth having if something checks it against the adapter."),
    };

    /// <summary>The venue's own catalog response for one family, so the classes it returns can be counted.</summary>
    private static Routes CatalogRoutes(string venue, string family) => (venue, family) switch
    {
        ("Binance", "spot") => new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo),
        ("Binance", "usdm-futures") => new Routes().On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo),

        // Its own recorded catalog, on its own host's path. The family declares a swap class and a future class and
        // the fixture holds both, taken from the venue's own contractType field rather than from the symbols -
        // which on this family are BTCUSD_PERP and BTCUSD_261225, and would both have read as dated.
        ("Binance", "coinm-futures") => new Routes().On("GET", "/dapi/v1/exchangeInfo", BinancePayloads.CoinMExchangeInfo),
        ("Bybit", "spot") => new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.SpotInstruments),
        ("Bybit", "linear") => new Routes().On(
            "GET",
            "/v5/market/instruments-info",
            r => StubResponse.Json(r.Query("cursor") is null ? BybitPayloads.LinearInstrumentsPage1 : BybitPayloads.LinearInstrumentsPage2)),

        // Two rows in one page, and both classes come out of the venue's contractType: the dated one is spelled
        // BTCUSDZ26, with no dash anywhere, so a rule reading the class off the name would report two perpetuals.
        ("Bybit", "inverse") => new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.InverseInstruments)
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits),

        // One class, and no risk-limit route beside it on purpose: this venue refuses that endpoint for the option
        // category, and the provider is written not to ask.
        ("Bybit", "option") => new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments),
        ("Kucoin", "spot") => new Routes().On("GET", "/api/v2/symbols", KucoinPayloads.Symbols),
        ("Kucoin", "futures") => new Routes().On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts),

        // One route for all three OKX families, answering by the instType the request carries. That is deliberate:
        // on this venue the instType IS what selects the family, so a route that ignored it would let a
        // misconfigured provider pass the class comparison below by reading somebody else's catalog.
        ("Okx", "spot") or ("Okx", "swap") or ("Okx", "futures") => new Routes()
            .On("GET", "/api/v5/public/instruments", r => StubResponse.Json(r.Query("instType") switch
            {
                "SPOT" => OkxPayloads.SpotInstruments,
                "SWAP" => OkxPayloads.SwapInstruments,
                "FUTURES" => OkxPayloads.FuturesInstruments,
                _ => OkxPayloads.Error("51000", "Parameter instType error"),
            }))
            .On("GET", "/api/v5/public/position-tiers", r => StubResponse.Json(
                r.Query("instType") == "FUTURES" ? OkxPayloads.FuturesTiers : OkxPayloads.SwapTiers)),

        ("Kraken", "spot") => new Routes().On("GET", "/0/public/AssetPairs", KrakenPayloads.AssetPairs),

        // Two routes, because a contract carries the uid of its fee schedule rather than its rates, and the
        // provider fetches both.
        ("Kraken", "futures") => new Routes()
            .On("GET", "/derivatives/api/v3/instruments", KrakenPayloads.Instruments)
            .On("GET", "/derivatives/api/v3/feeschedules", KrakenPayloads.FeeSchedules),

        ("Bitget", "spot") => new Routes().On("GET", BitgetInstrumentProvider.SpotSymbolsPath, BitgetPayloads.SpotSymbols),

        // Both perpetual families answer on one path and are told apart by the product type, and each contract's
        // margin rates are a second request away - which is why the tier route is here as well.
        ("Bitget", "usdt-futures") => new Routes()
            .On("GET", BitgetInstrumentProvider.ContractsPath, BitgetPayloads.UsdtContracts)
            .On("GET", BitgetInstrumentProvider.PositionTiersPath, BitgetPayloads.BtcUsdtPositionTiers),
        ("Bitget", "usdc-futures") => new Routes()
            .On("GET", BitgetInstrumentProvider.ContractsPath, BitgetPayloads.UsdcContracts)
            .On("GET", BitgetInstrumentProvider.PositionTiersPath, BitgetPayloads.BtcPerpPositionTiers),

        ("Gate", "spot") => new Routes().On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs),
        ("Gate", "futures") => new Routes().On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts),
        ("Gate", "delivery") => new Routes().On("GET", "/api/v4/delivery/usdt/contracts", GatePayloads.DeliveryContracts),

        // A POST to one path, with the read named in the BODY. This venue has no path per resource, so there is
        // nothing narrower to route on - which is the shape the whole adapter is built around.
        ("Hyperliquid", "perpetuals") => new Routes().On("POST", HyperliquidVenue.InfoPath, HyperliquidPayloads.Meta),

        _ => throw new InvalidOperationException(
            $"{venue}'s {family} family declares the instrument classes it returns and there is no catalog fixture "
            + "here to check the claim against. Add one: a class list nothing verifies is a guess in a table."),
    };

    /// <summary>The instrument classes a family's provider really returns, from that venue's own catalog shape.</summary>
    private static async Task<IReadOnlyList<InstrumentClass>> ProducedAsync(string venue, VenueFamily family)
    {
        await using LoopbackServer server = new(CatalogRoutes(venue, family.Name).Handle);
        object config = Configure(venue, family, httpBase: server.HttpBase);
        IReadOnlyList<Instrument> instruments;

        switch (venue)
        {
            case "Binance":
            {
                BinanceDataClientConfig c = (BinanceDataClientConfig)config;
                using BinanceHttp http = new(c);
                BinanceInstrumentProvider provider = new(http, c.AccountType);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Bybit":
            {
                BybitDataClientConfig c = (BybitDataClientConfig)config;
                using BybitHttp http = new(c);
                BybitInstrumentProvider provider = new(http, c.ProductType);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Bitget":
            {
                BitgetDataClientConfig c = (BitgetDataClientConfig)config;
                using BitgetHttp http = new(c);
                BitgetInstrumentProvider provider = new(http);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Kucoin":
            {
                KucoinDataClientConfig c = (KucoinDataClientConfig)config;
                using KucoinHttp http = new(c);
                InstrumentProviderBase provider = c.ProductType == KucoinProductType.Futures
                    ? new KucoinFuturesInstrumentProvider(http)
                    : new KucoinInstrumentProvider(http);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Okx":
            {
                OkxDataClientConfig c = (OkxDataClientConfig)config;
                using OkxHttp http = new(c);

                // The instrument type comes off the CONFIGURATION rather than from the family name, which is the
                // whole point on this venue: it is the declared setting that has to select the market, and if it
                // did not, the catalog route above would answer for a different one and the classes would not
                // match.
                OkxInstrumentProvider provider = new(http, c.InstrumentType);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Kraken":
            {
                KrakenDataClientConfig c = (KrakenDataClientConfig)config;
                using KrakenHttp http = new(c);
                InstrumentProviderBase provider = c.ProductType == KrakenProductType.Futures
                    ? new KrakenFuturesInstrumentProvider(http)
                    : new KrakenInstrumentProvider(http);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Gate":
            {
                GateDataClientConfig c = (GateDataClientConfig)config;
                using GateHttp http = new(c);
                InstrumentProviderBase provider = c.ProductType switch
                {
                    GateProductType.Futures => new GateFuturesInstrumentProvider(http),
                    GateProductType.Delivery => new GateDeliveryInstrumentProvider(http),
                    _ => new GateInstrumentProvider(http),
                };

                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            case "Hyperliquid":
            {
                HyperliquidDataClientConfig c = (HyperliquidDataClientConfig)config;
                using HyperliquidHttp http = new(c);
                HyperliquidInstrumentProvider provider = new(http);
                await provider.LoadAllAsync(CancellationToken.None);
                instruments = provider.GetAll();
                break;
            }

            default:
                throw new InvalidOperationException($"no instrument provider wired up here for {venue}");
        }

        Assert.NotEmpty(instruments);
        return [.. instruments.Select(i => i.InstrumentClass).Distinct().Order()];
    }

    // ----- the facts, each against the thing it describes -----

    [Fact]
    public void A_venue_that_declares_itself_declares_at_least_one_family()
    {
        IReadOnlyList<(string Venue, VenueDescriptor Descriptor)> declarations = Declarations();
        Assert.NotEmpty(declarations);

        foreach ((string venue, VenueDescriptor descriptor) in declarations)
        {
            Assert.NotEmpty(descriptor.Families);
            Assert.Equal(
                descriptor.Families.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count(),
                descriptor.Families.Count);

            Assert.False(
                string.IsNullOrWhiteSpace(descriptor.DisplayName),
                $"{venue} declares no name a person could read.");
        }
    }

    [Fact]
    public void A_class_two_families_hold_is_answered_by_the_first_of_them_on_purpose()
    {
        // FamilyFor answers "which family handles a swap on this venue" by taking the first that lists the class, so
        // two families listing one class is a question whose answer depends on declaration order. That used to be
        // forbidden outright, and it was the right rule while every venue that shipped held each class once.
        //
        // Bitget does not. It holds perpetual swaps two ways - 805 contracts margined in USDT and 49 in USDC, in two
        // product types with differently spelled symbols - and both are real markets a host can trade. Forbidding it
        // would have meant declaring one of them and leaving the other undeclared, which is the worse failure of the
        // two this file guards against: an undeclared market is one no host can find, while an order-dependent
        // answer is at least an answer.
        //
        // So the rule is now that order-dependence has to be deliberate. A class claimed twice must be answered by
        // the FIRST family that claims it, and the families claiming it must be distinguishable by the configuration
        // they declare - otherwise a host reading the declaration could not select between them at all and the
        // ordering really would be arbitrary.
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            foreach (InstrumentClass instrumentClass in Enum.GetValues<InstrumentClass>())
            {
                VenueFamily[] claiming = [.. descriptor.Families.Where(f => f.InstrumentClasses.Contains(instrumentClass))];
                if (claiming.Length == 0)
                {
                    continue;
                }

                Assert.Equal(claiming[0], descriptor.FamilyFor(instrumentClass));

                Assert.Equal(
                    claiming.Length,
                    claiming.Select(f => string.Join(";", f.Config.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value)))
                        .Distinct(StringComparer.Ordinal)
                        .Count());
            }

            foreach (VenueFamily family in descriptor.Families)
            {
                Assert.NotEmpty(family.InstrumentClasses);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_talks_to_the_hosts_it_declares(string venue, string name)
    {
        // The declaration says where a family answers. This asks the adapter, with nothing configured, where it
        // really goes. A host reading the declaration to show a user what a node talks to, or to decide whether a
        // venue is reachable from where it runs, is reading these two strings.
        VenueFamily family = Family(venue, name);
        (string http, string? ws, _) = Talks(venue, Configure(venue, family));

        Assert.Equal(family.HttpBase, http);

        if (family.WsBase is null)
        {
            Assert.Null(ws);
            return;
        }

        Assert.NotNull(ws);
        Assert.StartsWith(family.WsBase, ws, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_declared_base_written_back_into_the_configuration_changes_nothing(string venue, string name)
    {
        // What makes a base worth declaring: it is the DEFAULT of the setting that overrides it, so a host can write
        // it into a configuration file, or show it in a field a user may edit, and get the same node it had before.
        // The declaration that fails this is the one that states an endpoint instead of a root - Bybit's public
        // socket is a root plus /v5/public/<category>, and declaring the full path produced a URL that, written
        // back, had the category appended to it a second time. It read perfectly and connected nowhere.
        VenueFamily family = Family(venue, name);
        (string defaultHttp, string? defaultWs, string? defaultMarket) = Talks(venue, Configure(venue, family));
        (string writtenBack, string? writtenBackWs, string? writtenBackMarket) = Talks(venue, Configure(venue, family, family.HttpBase, family.WsBase));

        Assert.Equal(defaultHttp, writtenBack);
        Assert.Equal(defaultWs, writtenBackWs);
        Assert.Equal(defaultMarket, writtenBackMarket);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task The_config_a_family_declares_is_what_selects_that_family(string venue, string name)
    {
        // Naming the field without the value leaves a host guessing the spelling of a venue's own enum. Applying the
        // declared settings has to land on this family and no other.
        //
        // On the venues that shipped first, where that lands is visible in the ADDRESS: Binance's two families
        // differ by host and Bybit's by the path its socket is opened on. Two venues here are not like that, and
        // for different reasons, so this asks in three steps and stops at the first that answers.
        //
        // Bitget's three families answer on one host and one socket path, and what selects one is a market string -
        // the instrument type a subscription names and the product type a REST call carries - which its adapter
        // resolves from the declared settings. That string is the third element Talks returns, and for this venue
        // it IS the address: two families that ask for different markets are told apart, and nothing further needs
        // asking.
        //
        // OKX has neither. One host and one socket root serve its spot, swap and futures markets, and the instType
        // that selects one is put on each request rather than resolved from the settings, so there is no third
        // string to compare. "They talk to the same place and ask for the same market" is a fact about that venue
        // rather than a declaration nobody checked, and demanding different addresses would be demanding this venue
        // be built like the others.
        //
        // So where all three are the same, the proof moves to the catalog: the declared settings are applied, the
        // provider is built from them, and the instrument CLASSES that come back have to be this family's. That is
        // a stronger check than the address for such a venue - a family whose settings selected the wrong market
        // would read the wrong catalog and fail it - and it is why the fixture behind it answers by instType.
        VenueFamily family = Family(venue, name);
        VenueDescriptor descriptor = Declarations().Single(d => d.Venue == venue).Descriptor;
        (string http, string? ws, string? market) = Talks(venue, Configure(venue, family));

        foreach (VenueFamily other in descriptor.Families.Where(f => f.Name != name))
        {
            (string otherHttp, string? otherWs, string? otherMarket) = Talks(venue, Configure(venue, other));
            if (http != otherHttp || ws != otherWs || market != otherMarket)
            {
                continue;
            }

            Assert.False(
                family.Config.Count == 0 || family.Config.SequenceEqual(other.Config),
                $"{venue}'s {name} and {other.Name} ask for exactly the same market in exactly the same place AND "
                + "declare the same settings, so nothing anywhere could tell which family a host had configured.");

            IReadOnlyList<InstrumentClass> produced = await ProducedAsync(venue, family);
            Assert.NotEqual(other.InstrumentClasses.Distinct().Order().ToArray(), produced.ToArray());
            Assert.Equal(family.InstrumentClasses.Distinct().Order().ToArray(), produced.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Declared_instrument_classes_are_the_ones_the_provider_really_returns(string venue, string name)
    {
        // E15, and the one fact that was being inferred from how a symbol is spelled: a "-PERP" suffix is a
        // convention of the venues that exist today, and a venue naming a perpetual some other way would be read as
        // spot by anything trusting it. So the classes are declared, and the declaration is compared with what the
        // provider produces out of the venue's own contract data.
        //
        // Both directions matter. An undeclared class means a host is handed instruments it was told would not come.
        // A declared class nothing produces is the worse one: it is believed, offered to a user, and empty.
        VenueFamily family = Family(venue, name);
        IReadOnlyList<InstrumentClass> produced = await ProducedAsync(venue, family);

        Assert.Equal(family.InstrumentClasses.Distinct().Order().ToArray(), produced.ToArray());
    }

    [Fact]
    public void Every_host_an_adapter_talks_to_is_one_its_venue_declares()
    {
        // The half that catches a family nobody declared. An adapter that grows a third host - a second product, a
        // regional domain - has somewhere it can be pointed that the declaration does not mention, and a host
        // onboarding from the declaration would never find it.
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            HashSet<string> declared = [.. descriptor.Families.SelectMany(f =>
                new[] { f.HttpBase, f.WsBase }.Concat(f.FreeDatasets.Select(d => d.Address)).OfType<string>())];

            foreach (string file in Repo.SourceFiles(venue))
            {
                foreach (System.Text.RegularExpressions.Match match in
                    System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\"(https|wss)://[^\"]*\""))
                {
                    string host = match.Value.Trim('"');
                    Assert.True(
                        declared.Contains(host),
                        $"{Path.GetFileName(file)} talks to {host} and {venue} does not declare it. Declare the "
                        + "family it belongs to, or nothing above the adapter can know that market exists.");
                }
            }
        }
    }

    [Fact]
    public void A_family_that_declares_no_socket_base_has_no_socket_host_to_declare()
    {
        // Null means "the venue hands the address out per connection", which is a fact about the venue. It must not
        // come to mean "nobody filled this in", so a family declaring null is checked against an adapter that really
        // has no socket host anywhere in it.
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            if (descriptor.Families.Any(f => f.WsBase is not null))
            {
                continue;
            }

            foreach (string file in Repo.SourceFiles(venue))
            {
                Assert.DoesNotContain("wss://", File.ReadAllText(file), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_environment_variable_an_adapter_reads_is_a_declared_part_of_its_key()
    {
        // A key a host cannot assemble is a venue a host cannot use. KuCoin's key version was the one this found:
        // three parts were declared and the adapter reads a fourth, which defaults - so a key with the wrong version
        // looks complete, signs, and is rejected, and a host that never offers the field cannot fix it for a user.
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            Type venueClass = Assembly.Load("Bytex.Adapters." + venue)
                .GetType($"Bytex.Adapters.{venue}.{venue}Venue", throwOnError: true)!;

            string[] read = venueClass.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Env", StringComparison.Ordinal))
                .Select(f => (string)f.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            foreach (VenueFamily family in descriptor.Families)
            {
                Assert.Equal(read, family.Key.Parts.Select(p => p.Variable).Order(StringComparer.Ordinal).ToArray());
                Assert.All(family.Key.Parts, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));

                Assert.Contains(
                    family.Key.Parts,
                    p => p.Required && p.Secret);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_that_pays_funding_has_funding_history_to_pay_it_from(string venue, string name)
    {
        // The two are one fact declared twice - the checklist says the adapter can fetch funding, the family says
        // positions are charged it - and two statements of one fact drift. A spot family that claimed funding would
        // have a host charging a cost nothing can price; a perpetual family that denied it would backtest free.
        VenueFamily family = Family(venue, name);
        Type? history = Assembly.Load("Bytex.Adapters." + venue)
            .GetType($"Bytex.Adapters.{venue}.{venue}History", throwOnError: false);

        bool canFetch = history is not null && history
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Any(m => m.Name == "FetchFundingRatesAsync");

        if (family.PaysFunding)
        {
            Assert.True(
                canFetch,
                $"{venue}'s {name} family is charged funding and {venue}History cannot fetch any, so a backtest over "
                + "it is free and a live run is not.");
        }

        // And the claim against what the family holds, because the helper is the venue's and the charge is the
        // family's: a venue with futures has a funding helper, which would let its spot family claim funding too.
        // A perpetual is funded by definition - that payment is the whole mechanism that holds it to spot - and
        // nothing that is only spot ever is.
        if (family.InstrumentClasses.Contains(InstrumentClass.Swap))
        {
            Assert.True(
                family.PaysFunding,
                $"{venue}'s {name} family holds perpetual swaps and says they are not charged funding. Holding one" 
                + "overnight would cost nothing, which is the one cost that makes a perpetual track spot at all.");
        }
        else if (family.InstrumentClasses.All(c => c == InstrumentClass.Spot))
        {
            Assert.False(
                family.PaysFunding,
                $"{venue}'s {name} family is spot only and says it is charged funding, so a host would price a cost "
                + "that is never paid into every position held on it.");
        }
    }

    [Fact]
    public void A_field_one_family_ignores_is_one_some_other_family_really_uses()
    {
        // Ignored config is how a host tells a configuration that means nothing here from one that is wrong. A name
        // no venue uses is neither - it is a leftover, and a host filtering on it hides a real mistake.
        IReadOnlyList<(string Venue, VenueDescriptor Descriptor)> declarations = Declarations();
        HashSet<string> used = [.. declarations
            .SelectMany(d => d.Descriptor.Families)
            .SelectMany(f => f.Config.Keys)];

        foreach ((string venue, VenueDescriptor descriptor) in declarations)
        {
            foreach (VenueFamily family in descriptor.Families)
            {
                foreach (string ignored in family.IgnoredConfig)
                {
                    Assert.True(
                        used.Contains(ignored),
                        $"{venue}'s {family.Name} family says it ignores \"{ignored}\" and no venue that ships asks "
                        + "for it. Drop it, or a host treats a typo as a field meant for somebody else.");

                    Assert.False(
                        family.Config.ContainsKey(ignored),
                        $"{venue}'s {family.Name} family both sets and ignores \"{ignored}\".");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Default_fees_are_fractions_of_a_trade_rather_than_percentages(string venue, string name)
    {
        // Ten basis points is 0.001 here. Written as 0.1 it reads the same to a person and costs a hundred times as
        // much in a backtest, which is the mistake that makes a strategy look unprofitable rather than broken. That
        // is what the magnitude bound is for, and it applies to a rebate just as much - a rebate written as a
        // percentage is the same error with the sign reversed.
        VenueFamily family = Family(venue, name);

        // A maker rate may be negative: some venues pay for liquidity rather than charging for it, and a venue that
        // publishes a rebate has to be able to declare it. Declaring zero instead would understate what trading
        // there is worth, and would not be what the venue says.
        Assert.InRange(family.DefaultFees.Maker, -0.01m, 0.01m);

        // A taker rate may not. No venue pays for taking liquidity, so a negative here is a sign error, and it would
        // flatter every result by exactly what trading costs.
        Assert.InRange(family.DefaultFees.Taker, 0m, 0.01m);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_free_dataset_is_an_absolute_address_a_host_could_fetch(string venue, string name)
    {
        VenueFamily family = Family(venue, name);

        foreach (VenueDataset dataset in family.FreeDatasets)
        {
            Assert.False(string.IsNullOrWhiteSpace(dataset.Kind));
            Assert.True(
                Uri.TryCreate(dataset.Address, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps,
                $"{venue}'s {name} family publishes {dataset.Kind} at \"{dataset.Address}\", which is not an address "
                + "anything could fetch.");
        }
    }

    [Fact]
    public void A_declaration_survives_the_json_a_host_reads_it_as()
    {
        // A host reads this out of `bytex venues --json` rather than by hosting the engine, so the JSON is the
        // interface and not a convenience. Anything that does not survive the trip is a fact that exists only for
        // callers who already reference the assembly - which is the callers that did not need the declaration.
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            string json = JsonSerializer.Serialize(descriptor, BytexJson.Options);
            VenueDescriptor? read = JsonSerializer.Deserialize<VenueDescriptor>(json, BytexJson.Options);

            Assert.NotNull(read);
            Assert.Equal(descriptor.Venue, read!.Venue);
            Assert.Equal(descriptor.BrokerTag, read.BrokerTag);
            Assert.Equal(
                descriptor.Families.Select(f => f.Name),
                read.Families.Select(f => f.Name));

            foreach ((VenueFamily before, VenueFamily after) in descriptor.Families.Zip(read.Families))
            {
                Assert.Equal(before.HttpBase, after.HttpBase);
                Assert.Equal(before.WsBase, after.WsBase);
                Assert.Equal(before.PaysFunding, after.PaysFunding);
                Assert.Equal(before.InstrumentClasses, after.InstrumentClasses);
                Assert.Equal(before.Config, after.Config);
                Assert.Equal(before.IgnoredConfig, after.IgnoredConfig);
                Assert.Equal(before.DefaultFees, after.DefaultFees);
                Assert.Equal(before.Key.Parts, after.Key.Parts);
                Assert.Equal(before.FreeDatasets, after.FreeDatasets);
            }

            Assert.DoesNotContain(
                "\"Venue\"",
                json,
                StringComparison.Ordinal);
        }
    }
}
