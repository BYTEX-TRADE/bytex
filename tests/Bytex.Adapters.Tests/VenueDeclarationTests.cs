using System.Reflection;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
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

    /// <summary>Where a configured client really talks, asked of the adapter's own resolver.</summary>
    private static (string Http, string? Ws) Talks(string venue, object config) => venue switch
    {
        "Binance" => (BinanceVenue.HttpBase((IBinanceSettings)config), BinanceVenue.WsBase((IBinanceSettings)config)),
        "Bybit" => (BybitVenue.HttpBase((IBybitSettings)config), BybitVenue.WsPublic((IBybitSettings)config)),

        // KuCoin has no socket base to resolve: the venue answers a REST call with the address, per connection.
        "Kucoin" => (KucoinVenue.HttpBase((IKucoinSettings)config), null),

        // OKX has one host and one socket root for all three markets, which is why the answer here is the same for
        // every family it declares. What selects a family is an instType parameter on the request, so the test
        // below that asks "do the declared settings really pick this family" cannot be answered by an address on
        // this venue - it is answered by the catalog instead.
        "Okx" => (OkxVenue.HttpBase((IOkxSettings)config), OkxVenue.WsBase((IOkxSettings)config)),
        _ => throw new InvalidOperationException(
            $"{venue} declares itself and this test does not know how to ask it where it talks. Add it here - the "
            + "declaration is only worth having if something checks it against the adapter."),
    };

    /// <summary>The venue's own catalog response for one family, so the classes it returns can be counted.</summary>
    private static Routes CatalogRoutes(string venue, string family) => (venue, family) switch
    {
        ("Binance", "spot") => new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo),
        ("Binance", "usdm-futures") => new Routes().On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo),
        ("Bybit", "spot") => new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.SpotInstruments),
        ("Bybit", "linear") => new Routes().On(
            "GET",
            "/v5/market/instruments-info",
            r => StubResponse.Json(r.Query("cursor") is null ? BybitPayloads.LinearInstrumentsPage1 : BybitPayloads.LinearInstrumentsPage2)),
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
    public void A_venue_declares_each_instrument_class_in_exactly_one_family()
    {
        // FamilyFor answers "which family handles a swap on this venue" by taking the first that lists the class, so
        // two families listing one class is a question with two answers and an answer that depends on declaration
        // order. A venue that really does offer one class two ways - inverse and linear perpetuals, say - has to say
        // which is the one to use rather than leaving it to whoever wrote the list.
        foreach ((string venue, VenueDescriptor descriptor) in Declarations())
        {
            foreach (InstrumentClass instrumentClass in Enum.GetValues<InstrumentClass>())
            {
                string[] claiming = descriptor.Families
                    .Where(f => f.InstrumentClasses.Contains(instrumentClass))
                    .Select(f => f.Name)
                    .ToArray();

                Assert.True(
                    claiming.Length <= 1,
                    $"{venue} declares {instrumentClass} in {string.Join(" and ", claiming)}, so which one handles it "
                    + "depends on the order they are written in. Say which, or split the class.");
            }

            foreach (VenueFamily family in descriptor.Families)
            {
                Assert.NotEmpty(family.InstrumentClasses);
                foreach (InstrumentClass instrumentClass in family.InstrumentClasses)
                {
                    Assert.Equal(family, descriptor.FamilyFor(instrumentClass));
                }
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
        (string http, string? ws) = Talks(venue, Configure(venue, family));

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
        (string defaultHttp, string? defaultWs) = Talks(venue, Configure(venue, family));
        (string writtenBack, string? writtenBackWs) = Talks(venue, Configure(venue, family, family.HttpBase, family.WsBase));

        Assert.Equal(defaultHttp, writtenBack);
        Assert.Equal(defaultWs, writtenBackWs);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task The_config_a_family_declares_is_what_selects_that_family(string venue, string name)
    {
        // Naming the field without the value leaves a host guessing the spelling of a venue's own enum. Applying the
        // declared settings has to land on this family and no other.
        //
        // On the venues that shipped first, where that lands is visible in the ADDRESS: Binance's two families
        // differ by host and Bybit's by the path its socket is opened on. OKX is the first venue here where it is
        // not - one host and one socket root serve its spot, swap and futures markets, and what selects a market is
        // an instType parameter on each request. So "they talk to the same place" is a fact about that venue rather
        // than a declaration nobody checked, and demanding different addresses would be demanding this venue be
        // built like the others.
        //
        // Where the addresses are the same, the proof moves to the catalog: the declared settings are applied, the
        // provider is built from them, and the instrument CLASSES that come back have to be this family's. That is
        // a stronger check than the address for such a venue - a family whose settings selected the wrong market
        // would read the wrong catalog and fail it - and it is why the fixture behind it answers by instType.
        VenueFamily family = Family(venue, name);
        VenueDescriptor descriptor = Declarations().Single(d => d.Venue == venue).Descriptor;
        (string http, string? ws) = Talks(venue, Configure(venue, family));

        foreach (VenueFamily other in descriptor.Families.Where(f => f.Name != name))
        {
            (string otherHttp, string? otherWs) = Talks(venue, Configure(venue, other));
            if (http != otherHttp || ws != otherWs)
            {
                continue;
            }

            Assert.False(
                family.Config.Count == 0 || family.Config.SequenceEqual(other.Config),
                $"{venue}'s {name} and {other.Name} talk to exactly the same place AND declare the same settings, "
                + "so nothing anywhere could tell which family a host had configured.");

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
        // much in a backtest, which is the mistake that makes a strategy look unprofitable rather than broken.
        VenueFamily family = Family(venue, name);

        foreach (decimal fee in new[] { family.DefaultFees.Maker, family.DefaultFees.Taker })
        {
            Assert.InRange(fee, 0m, 0.01m);
        }
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
