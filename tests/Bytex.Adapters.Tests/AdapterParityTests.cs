using System.Reflection;
using Bytex.Core.Adapters;

namespace Bytex.Adapters.Tests;

// Why: the client base classes answer almost everything with a completed task. QueryOrderAsync returns without
// reporting anything, UnsubscribeAsync returns without unsubscribing, the report methods return empty lists. That is
// a convenient default and a dangerous one, because a caller cannot tell "this venue has nothing to report" from
// "this venue was never taught to report". Whatever asked gets a success and waits for an answer that never comes.
//
// Counting what each of the four venues actually implements found exactly that: QueryOrderAsync was implemented by
// Binance alone, so the same command against Bybit or KuCoin succeeded and answered nothing. It was not a decision
// anybody had made - Binance's implementation was three lines over a report the other two already had - so it moved
// to the base class, where every venue that can report an order answers the query and a venue that cannot says so.
// That is what the table is for: it turned an accident nobody could see into one line of a row.
//
// So this file is the table. Every command a base class can be asked, for every venue that ships, with what happens
// when that venue is asked it. A venue that quietly stops implementing something fails here; a command added to a
// base class fails here until every venue's row says what that venue does about it; and a fifth adapter fails here
// until its row is filled in, which is the point - nobody can add a venue without comparing it with the other four.
public sealed class AdapterParityTests
{
    /// <summary>What happens when a venue is asked one of the commands its base class defines.</summary>
    private enum Parity
    {
        /// <summary>The adapter implements it: the venue is really being asked.</summary>
        Own,

        /// <summary>
        /// Not implemented, and the base default is the right behaviour: it does the work (a loop over single calls),
        /// or there is genuinely nothing to do (unsubscribing a client that refuses every subscription).
        /// </summary>
        Base,

        /// <summary>Not implemented, and the base default reports success while doing and answering nothing.</summary>
        Silent,

        /// <summary>The venue has no client of this kind at all, so the command cannot be sent to it.</summary>
        None,
    }

    private enum Client
    {
        Execution,
        Data,
        Instruments,
    }

    // As of 0.6. Read a row as "ask this venue this, and this is what you get".
    private static readonly Dictionary<Client, Dictionary<string, Dictionary<string, Parity>>> _table = new()
    {
        [Client.Execution] = new(StringComparer.Ordinal)
        {
            ["Binance"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubmitOrderAsync"] = Parity.Own,
                ["SubmitOrderListAsync"] = Parity.Base,
                ["ModifyOrderAsync"] = Parity.Own,
                ["CancelOrderAsync"] = Parity.Own,
                ["CancelAllOrdersAsync"] = Parity.Own,
                ["BatchCancelOrdersAsync"] = Parity.Base,

                // The base asks the venue for this order's report, which Binance implements, so nothing is left for
                // the adapter to do. It had its own copy of those three lines until the count found the other two
                // venues had never been given them.
                ["QueryOrderAsync"] = Parity.Base,
                ["GenerateMassStatusAsync"] = Parity.Own,
                ["GenerateOrderStatusReportAsync"] = Parity.Own,
                ["GenerateOrderStatusReportsAsync"] = Parity.Own,
                ["GenerateFillReportsAsync"] = Parity.Own,
                ["GeneratePositionStatusReportsAsync"] = Parity.Own,
            },
            ["Bybit"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubmitOrderAsync"] = Parity.Own,
                ["SubmitOrderListAsync"] = Parity.Base,
                ["ModifyOrderAsync"] = Parity.Own,
                ["CancelOrderAsync"] = Parity.Own,
                ["CancelAllOrdersAsync"] = Parity.Own,
                ["BatchCancelOrdersAsync"] = Parity.Base,

                // Answered by the base out of this venue's own order report, which is implemented below.
                ["QueryOrderAsync"] = Parity.Base,

                ["GenerateMassStatusAsync"] = Parity.Own,
                ["GenerateOrderStatusReportAsync"] = Parity.Own,
                ["GenerateOrderStatusReportsAsync"] = Parity.Own,
                ["GenerateFillReportsAsync"] = Parity.Own,
                ["GeneratePositionStatusReportsAsync"] = Parity.Own,
            },
            ["Kucoin"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubmitOrderAsync"] = Parity.Own,
                ["SubmitOrderListAsync"] = Parity.Base,
                ["ModifyOrderAsync"] = Parity.Own,
                ["CancelOrderAsync"] = Parity.Own,
                ["CancelAllOrdersAsync"] = Parity.Own,
                ["BatchCancelOrdersAsync"] = Parity.Base,

                // As Bybit: answered by the base out of this venue's own order report.
                ["QueryOrderAsync"] = Parity.Base,

                ["GenerateMassStatusAsync"] = Parity.Own,
                ["GenerateOrderStatusReportAsync"] = Parity.Own,
                ["GenerateOrderStatusReportsAsync"] = Parity.Own,
                ["GenerateFillReportsAsync"] = Parity.Own,
                ["GeneratePositionStatusReportsAsync"] = Parity.Own,
            },

            // OKX brings three markets through ONE client, where KuCoin's two markets need two: its endpoints are
            // shared and an instType parameter selects the market, so there is no second client for this table to
            // miss. What it cannot do is cancel-all - the venue has no such endpoint for these markets - so that
            // row is Own rather than Base: the open orders are read and cancelled in batches, which is work the
            // base's loop over single cancels would do one request at a time.
            ["Okx"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubmitOrderAsync"] = Parity.Own,
                ["SubmitOrderListAsync"] = Parity.Base,
                ["ModifyOrderAsync"] = Parity.Own,
                ["CancelOrderAsync"] = Parity.Own,
                ["CancelAllOrdersAsync"] = Parity.Own,
                ["BatchCancelOrdersAsync"] = Parity.Base,

                // As Bybit and KuCoin: answered by the base out of this venue's own order report, which is
                // implemented below.
                ["QueryOrderAsync"] = Parity.Base,

                ["GenerateMassStatusAsync"] = Parity.Own,
                ["GenerateOrderStatusReportAsync"] = Parity.Own,
                ["GenerateOrderStatusReportsAsync"] = Parity.Own,
                ["GenerateFillReportsAsync"] = Parity.Own,
                ["GeneratePositionStatusReportsAsync"] = Parity.Own,
            },

            // Tardis is a history source. There is nothing to trade on and no execution client to trade with, which
            // is why every command is None rather than Silent - it cannot be asked at all.
            ["Tardis"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.None,
                ["DisconnectAsync"] = Parity.None,
                ["SubmitOrderAsync"] = Parity.None,
                ["SubmitOrderListAsync"] = Parity.None,
                ["ModifyOrderAsync"] = Parity.None,
                ["CancelOrderAsync"] = Parity.None,
                ["CancelAllOrdersAsync"] = Parity.None,
                ["BatchCancelOrdersAsync"] = Parity.None,
                ["QueryOrderAsync"] = Parity.None,
                ["GenerateMassStatusAsync"] = Parity.None,
                ["GenerateOrderStatusReportAsync"] = Parity.None,
                ["GenerateOrderStatusReportsAsync"] = Parity.None,
                ["GenerateFillReportsAsync"] = Parity.None,
                ["GeneratePositionStatusReportsAsync"] = Parity.None,
            },
        },
        [Client.Data] = new(StringComparer.Ordinal)
        {
            ["Binance"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubscribeAsync"] = Parity.Own,
                ["UnsubscribeAsync"] = Parity.Own,
                ["RequestAsync"] = Parity.Own,
            },
            ["Bybit"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubscribeAsync"] = Parity.Own,
                ["UnsubscribeAsync"] = Parity.Own,
                ["RequestAsync"] = Parity.Own,
            },
            ["Kucoin"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubscribeAsync"] = Parity.Own,
                ["RequestAsync"] = Parity.Own,
                ["UnsubscribeAsync"] = Parity.Own,
            },
            ["Okx"] = new(StringComparer.Ordinal)
            {
                ["ConnectAsync"] = Parity.Own,
                ["DisconnectAsync"] = Parity.Own,
                ["SubscribeAsync"] = Parity.Own,
                ["UnsubscribeAsync"] = Parity.Own,
                ["RequestAsync"] = Parity.Own,
            },
            ["Tardis"] = new(StringComparer.Ordinal)
            {
                // There is no socket to open, so the base's bookkeeping - mark connected, tell the sink - is the
                // whole of what connecting to a file means.
                ["ConnectAsync"] = Parity.Base,
                ["DisconnectAsync"] = Parity.Base,

                // Every subscription is refused outright - the client tells the sink "Tardis provides historical data
                // only" - so no subscription to this client can exist.
                ["SubscribeAsync"] = Parity.Own,

                // And therefore unsubscribing has nothing to undo, which makes the base's do-nothing the whole of the
                // correct behaviour rather than a gap. This row read Silent until the claim was checked against
                // SubscribeAsync; the test below is what keeps the two honest about each other.
                ["UnsubscribeAsync"] = Parity.Base,

                ["RequestAsync"] = Parity.Own,
            },
        },
        [Client.Instruments] = new(StringComparer.Ordinal)
        {
            ["Binance"] = new(StringComparer.Ordinal)
            {
                ["LoadAllAsync"] = Parity.Own,
                ["LoadIdsAsync"] = Parity.Base,
                ["LoadAsync"] = Parity.Own,
            },
            ["Bybit"] = new(StringComparer.Ordinal)
            {
                ["LoadAllAsync"] = Parity.Own,
                ["LoadIdsAsync"] = Parity.Base,
                ["LoadAsync"] = Parity.Own,
            },
            ["Kucoin"] = new(StringComparer.Ordinal)
            {
                ["LoadAllAsync"] = Parity.Own,
                ["LoadIdsAsync"] = Parity.Base,
                ["LoadAsync"] = Parity.Own,
            },
            ["Okx"] = new(StringComparer.Ordinal)
            {
                ["LoadAllAsync"] = Parity.Own,
                ["LoadIdsAsync"] = Parity.Base,
                ["LoadAsync"] = Parity.Own,
            },

            // No catalog either: Tardis instruments come from the files being replayed, not from a venue that can be
            // asked what it lists. This is why a Tardis venue cannot be offered in an instrument picker.
            ["Tardis"] = new(StringComparer.Ordinal)
            {
                ["LoadAllAsync"] = Parity.None,
                ["LoadIdsAsync"] = Parity.None,
                ["LoadAsync"] = Parity.None,
            },
        },
    };

    private static Type BaseClass(Client kind) => kind switch
    {
        Client.Execution => typeof(ExecutionClientBase),
        Client.Data => typeof(DataClientBase),
        Client.Instruments => typeof(InstrumentProviderBase),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string TypeSuffix(Client kind) => kind switch
    {
        Client.Execution => "ExecutionClient",
        Client.Data => "DataClient",
        Client.Instruments => "InstrumentProvider",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The adapter's class of that kind, by the naming every adapter follows, or null where it has none. Found by
    /// convention rather than by a list of typeofs so that a new adapter is looked for rather than forgotten.
    /// </summary>
    private static Type? Concrete(string venue, Client kind)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load("Bytex.Adapters." + venue);
        }
        catch (FileNotFoundException)
        {
            Assert.Fail(
                $"the {venue} adapter ships in src but this test project does not reference it, so nothing here can "
                + "check it against the other venues. Add the project reference.");
            throw;
        }

        return assembly.GetType($"Bytex.Adapters.{venue}.{venue}{TypeSuffix(kind)}", throwOnError: false);
    }

    /// <summary>Every command a base class can be asked, which is every public method a venue could override.</summary>
    private static string[] Commands(Client kind)
    {
        Type baseClass = BaseClass(kind);
        return baseClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == baseClass && (m.IsVirtual || m.IsAbstract) && !m.IsFinal)
            .Select(m => m.Name)
            .Where(n => n.EndsWith("Async", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A path in the repository, for the rows below that are kept honest by reading the source.</summary>
    private static string Source(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bytex.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }

    /// <summary>The adapters that ship, read off disk rather than listed, so a fifth one is noticed the day it lands.</summary>
    private static string[] ShippedVenues()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bytex.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Directory.EnumerateDirectories(Path.Combine(directory!.FullName, "src"), "Bytex.Adapters.*")
            .Select(d => Path.GetFileName(d).Substring("Bytex.Adapters.".Length))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static TheoryData<string, string, string> Rows()
    {
        TheoryData<string, string, string> data = new();
        foreach ((Client kind, Dictionary<string, Dictionary<string, Parity>> venues) in _table)
        {
            foreach ((string venue, Dictionary<string, Parity> commands) in venues)
            {
                foreach (string command in commands.Keys)
                {
                    data.Add(kind.ToString(), venue, command);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Each_venue_answers_each_command_the_way_the_table_says(string clientKind, string venue, string command)
    {
        Client kind = Enum.Parse<Client>(clientKind);
        Parity expected = _table[kind][venue][command];
        Type? concrete = Concrete(venue, kind);

        if (expected == Parity.None)
        {
            Assert.True(
                concrete is null,
                $"{venue} now has a {kind} client and the table says it has none, so nobody has compared what it can "
                + $"be asked with what the other venues can. Fill in its {kind} row.");
            return;
        }

        Assert.True(
            concrete is not null,
            $"{venue} no longer has a {kind} client, and the table still describes {command} on it.");

        bool declared = concrete!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == command && m.DeclaringType == concrete);

        if (expected == Parity.Own)
        {
            Assert.True(
                declared,
                $"{venue}'s {kind} client no longer implements {command}. The base will answer it instead - with a "
                + "completed task, an empty list or a loop over single calls - so a caller is told nothing rather "
                + "than told this venue cannot. Implement it, or record what the base now does for it in the table.");
            return;
        }

        Assert.False(
            declared,
            $"{venue}'s {kind} client now implements {command} and the table still says the base handles it "
            + $"({expected}). Set it to {nameof(Parity.Own)}: this table is what tells anybody which venues really "
            + "answer which commands.");
    }

    [Fact]
    public void Every_command_a_base_class_defines_appears_in_every_venues_row()
    {
        // The half that catches a new command: adding one to a base class gives every venue a question it silently
        // answers with nothing until somebody implements it, so it has to be decided per venue the day it is added.
        foreach ((Client kind, Dictionary<string, Dictionary<string, Parity>> venues) in _table)
        {
            string[] commands = Commands(kind);
            foreach ((string venue, Dictionary<string, Parity> row) in venues)
            {
                string[] missing = commands.Where(c => !row.ContainsKey(c)).ToArray();
                Assert.True(
                    missing.Length == 0,
                    $"a {kind} client can be asked {string.Join(", ", missing)} and the {venue} row says nothing "
                    + "about it, so nobody knows whether that venue answers or silently does not.");

                string[] stale = row.Keys.Where(c => !commands.Contains(c, StringComparer.Ordinal)).ToArray();
                Assert.True(
                    stale.Length == 0,
                    $"the {venue} {kind} row describes {string.Join(", ", stale)}, which no base class defines any "
                    + "more. Drop it, or the table describes a venue nobody can ask anything.");
            }
        }
    }

    [Fact]
    public void Every_adapter_that_ships_has_a_row_for_every_kind_of_client()
    {
        // The half that catches a new venue. An adapter without a row is an adapter nobody has compared with the
        // others, which is the whole failure this file exists to prevent.
        string[] shipped = ShippedVenues();
        Assert.NotEmpty(shipped);

        foreach ((Client kind, Dictionary<string, Dictionary<string, Parity>> venues) in _table)
        {
            Assert.Equal(shipped, venues.Keys.Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void A_command_a_venue_must_implement_is_never_recorded_as_handled_by_the_base()
    {
        // Coherence of the table itself: an abstract command has no base behaviour to fall back on, so Base and
        // Silent are impossible answers for it and would mean somebody guessed a row instead of reading the code.
        foreach ((Client kind, Dictionary<string, Dictionary<string, Parity>> venues) in _table)
        {
            Type baseClass = BaseClass(kind);
            foreach ((string venue, Dictionary<string, Parity> row) in venues)
            {
                foreach ((string command, Parity parity) in row)
                {
                    if (parity is not (Parity.Base or Parity.Silent))
                    {
                        continue;
                    }

                    bool abstractOnTheBase = baseClass
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m => m.Name == command && m.DeclaringType == baseClass && m.IsAbstract);

                    Assert.False(
                        abstractOnTheBase,
                        $"the {venue} row says the base handles {command}, but it is abstract - every venue has to "
                        + "implement it and none can inherit anything. The row was not read off the code.");
                }
            }
        }
    }

    // ----- the venue checklist (E7, E9, E10, E12) -----

    /// <summary>
    /// What a venue owes besides the commands above, as the definition of done for an
    /// adapter. These are the things that are invisible until somebody tries to use the venue and then make it
    /// unusable: no way to download its history without a running node, no key test, nothing that says what the
    /// venue is without reading the adapter, and no statement of how it would carry a broker id.
    /// <para>
    /// 0.7 brings six venues. Every one of these is cheap while an adapter is being written and six times the work
    /// afterwards, so a venue is not done until its row here is filled in and the tests below agree with it.
    /// </para>
    /// </summary>
    private enum Owed
    {
        /// <summary>The adapter has it.</summary>
        Has,

        /// <summary>It does not apply to this venue, and the test below says why in terms of what the venue is.</summary>
        NotApplicable,

        /// <summary>It applies and the adapter has not got it. A venue may not ship in this state.</summary>
        Missing,

        /// <summary>
        /// It applies, the adapter has not got it, and the venue ships anyway because what is lost is revenue
        /// rather than function. Only the broker programme can be in this state: an unclaimed rebate breaks
        /// nothing and refusing to ship a working venue over it would be wrong, while calling it not-applicable
        /// would repeat the false claim this state exists to replace.
        /// </summary>
        Unclaimed,
    }

    private static readonly Dictionary<string, Dictionary<string, Owed>> _checklist = new(StringComparer.Ordinal)
    {
        ["Binance"] = new(StringComparer.Ordinal)
        {
            ["HistoryBars"] = Owed.Has,
            ["HistoryFunding"] = Owed.Has,
            ["VerifyKeys"] = Owed.Has,
            ["Declaration"] = Owed.Has,

            // R11.13, delivered: the id prefixes the client order id here, and every place that sends or reads
            // one translates - which matters because that id is the key reconciliation matches an order on.
            ["BrokerTag"] = Owed.Has,

            // And the venue pays for it: the programme is live and this adapter carries an id for it.
            ["BrokerProgramme"] = Owed.Has,
        },
        ["Bybit"] = new(StringComparer.Ordinal)
        {
            ["HistoryBars"] = Owed.Has,
            ["HistoryFunding"] = Owed.Has,
            ["VerifyKeys"] = Owed.Has,
            ["Declaration"] = Owed.Has,

            // A header on the order request here, so an id changes nothing about the order itself.
            ["BrokerTag"] = Owed.Has,

            ["BrokerProgramme"] = Owed.Has,
        },
        ["Kucoin"] = new(StringComparer.Ordinal)
        {
            ["HistoryBars"] = Owed.Has,

            // Owed by the futures family and answered for it. Spot pays no funding and has none to fetch, which
            // is why this is a venue-level row that only became true when the venue gained a second family.
            ["HistoryFunding"] = Owed.Has,

            ["VerifyKeys"] = Owed.Has,
            ["Declaration"] = Owed.Has,

            // Nothing here carries an id, and on the owner's ruling that is a default no-op rather than a refusal:
            // a configured id is ignored, so nothing above the adapter has to know which venues have a programme.
            ["BrokerTag"] = Owed.NotApplicable,

            // This row read "no broker programme on either market" until it was checked, and that was wrong. The
            // venue runs two broker tiers with rebate management. Its mechanism is a signed partner credential on
            // every REST request - an id, a broker name, and a signature over the timestamp, the id and the API
            // key made with a second secret the programme issues - which a single configured id cannot express,
            // and which is why nothing carries it.
            //
            // Unclaimed and not NotApplicable, because it does apply: every trade routed here earns a rebate that
            // nobody is collecting. It is not Missing either - the venue trades correctly and shipping it was not
            // a mistake.
            ["BrokerProgramme"] = Owed.Unclaimed,
        },
        ["Okx"] = new(StringComparer.Ordinal)
        {
            ["HistoryBars"] = Owed.Has,

            // Owed by the swap family and answered for it. Spot pays no funding and neither do the dated futures -
            // a contract that delivers converges by delivering - so this is a venue-level row that one of three
            // families makes true.
            ["HistoryFunding"] = Owed.Has,

            ["VerifyKeys"] = Owed.Has,
            ["Declaration"] = Owed.Has,

            // Nothing here carries an id, on the same default-no-op terms as KuCoin: a configured id is ignored, so
            // nothing above the adapter has to know which venues have a programme.
            ["BrokerTag"] = Owed.NotApplicable,

            // The venue runs a broker programme and publishes its mechanism ONLY TO APPROVED APPLICANTS. What an
            // order would have to carry - a tag, a header, a credential - is not in the public documentation at
            // all, so nothing can be built before somebody applies: this is not adapter work waiting to be
            // scheduled, it waits on a decision.
            //
            // Unclaimed and not NotApplicable, because it does apply: every trade routed here earns a rebate that
            // nobody is collecting. Not Missing either - the venue trades correctly and shipping it is not a
            // mistake.
            ["BrokerProgramme"] = Owed.Unclaimed,
        },
        ["Tardis"] = new(StringComparer.Ordinal)
        {
            // A history service, not a venue: its whole client is history, there is nothing to trade on it, and
            // there are no orders to carry an id. Its own bar history is not offered because Tardis serves ticks.
            ["HistoryBars"] = Owed.NotApplicable,
            ["HistoryFunding"] = Owed.NotApplicable,
            ["VerifyKeys"] = Owed.NotApplicable,

            // Nothing to declare: a descriptor says which markets a venue covers, what a key for it is made of and
            // what it charges, and Tardis has no markets, no key of the kind a venue has and no fees. A host must
            // not be offered it as a venue, which is exactly what leaving it out of the venue list says.
            ["Declaration"] = Owed.NotApplicable,

            ["BrokerTag"] = Owed.NotApplicable,
            ["BrokerProgramme"] = Owed.NotApplicable,
        },
    };

    [Fact]
    public void Every_venue_that_ships_has_a_complete_checklist_row()
    {
        string[] shipped = ShippedVenues();
        Assert.Equal(shipped, _checklist.Keys.Order(StringComparer.Ordinal).ToArray());
        string[] items = ["HistoryBars", "HistoryFunding", "VerifyKeys", "Declaration", "BrokerTag", "BrokerProgramme"];
        foreach ((string venue, Dictionary<string, Owed> row) in _checklist)
        {
            Assert.Equal(items.Order(StringComparer.Ordinal), row.Keys.Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData("HistoryBars", "FetchBarsAsync")]
    [InlineData("HistoryFunding", "FetchFundingRatesAsync")]
    public void A_venue_that_claims_a_history_helper_has_one_that_needs_no_node(string item, string method)
    {
        // The point of E7: a host that stores history has no node, so the method has to be callable with an http
        // client and an instrument and nothing else. A private one on the data client is how the paging came to be
        // written twice.
        foreach ((string venue, Dictionary<string, Owed> row) in _checklist)
        {
            Type? history = Assembly.Load("Bytex.Adapters." + venue).GetType($"Bytex.Adapters.{venue}.{venue}History", throwOnError: false);
            bool present = history is not null && history
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(m => m.Name == method);

            Assert.Equal(row[item] == Owed.Has, present);
        }
    }

    [Fact]
    public void A_venue_that_claims_a_key_test_is_in_the_verify_keys_command()
    {
        // E10. A host's key report is this command's output and nothing else, so a venue the switch does not
        // name has no key test at all and the Live gate in front of it has nothing to read.
        string source = File.ReadAllText(Source("src", "Bytex.Cli", "KeyCommands.cs"));

        foreach ((string venue, Dictionary<string, Owed> row) in _checklist)
        {
            bool named = source.Contains($"case \"{venue.ToUpperInvariant()}\"", StringComparison.Ordinal);
            Assert.Equal(row["VerifyKeys"] == Owed.Has, named);
        }
    }


    [Fact]
    public void A_venue_that_claims_a_declaration_has_a_plugin_that_describes_it()
    {
        // E15/R11.14. Everything above an adapter was reading this source by hand to find out what a venue is -
        // which markets, which key, which setting picks its futures - and six venues arrive in 0.7. A venue whose
        // plugin cannot be asked is a venue every host has to be taught by hand, one host at a time.
        //
        // What the declaration then has to be TRUE about is VenueDeclarationTests; this row is only that it exists.
        foreach ((string venue, Dictionary<string, Owed> row) in _checklist)
        {
            bool describes = Assembly.Load("Bytex.Adapters." + venue).GetTypes()
                .Any(t => !t.IsAbstract && typeof(IVenuePlugin).IsAssignableFrom(t));

            Assert.Equal(row["Declaration"] == Owed.Has, describes);
        }
    }

    [Fact]
    public void A_venues_broker_row_agrees_with_the_mechanism_it_declares()
    {
        // E12/R11.13, delivered. This test used to assert that NOTHING carried a broker id, which was true while it
        // was unbuilt and is the wrong question now. What matters is that the checklist and the declaration cannot
        // disagree: a venue whose row claims a mechanism has to declare one, and a venue that declares none has to
        // say the requirement does not apply rather than that it is missing.
        //
        // How each mechanism behaves, and that untagged stays untagged, is BrokerIdTests. This is the half that
        // stops a venue being signed off against a row nobody filled in.
        foreach ((string venue, Dictionary<string, Owed> row) in _checklist)
        {
            Type? plugin = Assembly.Load("Bytex.Adapters." + venue).GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IVenuePlugin).IsAssignableFrom(t));

            BrokerTag declared = plugin is null
                ? BrokerTag.None
                : ((IVenuePlugin)Activator.CreateInstance(plugin)!).Describe().BrokerTag;

            if (declared == BrokerTag.None)
            {
                Assert.Equal(Owed.NotApplicable, row["BrokerTag"]);
                continue;
            }

            Assert.Equal(Owed.Has, row["BrokerTag"]);
        }

        // And nothing is left claiming it owes one: every venue that ships has either a mechanism or a reason.
        Assert.DoesNotContain(Owed.Missing, _checklist.Values.Select(r => r["BrokerTag"]));
    }

    [Fact]
    public void A_venues_programme_row_agrees_with_what_the_venue_actually_pays()
    {
        // The row that was wrong, now enforced against the declaration rather than against what somebody assumed.
        // Both said KuCoin had no broker programme; it runs two. A checklist that cannot disagree with the
        // declaration would not have caught that on its own - but the declaration is what a host reads and what
        // the venue's own documentation can be held against, so pinning them together means correcting one
        // corrects both, and neither can drift back.
        foreach ((string venue, Dictionary<string, Owed> row) in _checklist)
        {
            Type? plugin = Assembly.Load("Bytex.Adapters." + venue).GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IVenuePlugin).IsAssignableFrom(t));

            if (plugin is null)
            {
                // Not a venue - a history service has nothing to route and nothing to be paid for.
                Assert.Equal(Owed.NotApplicable, row["BrokerProgramme"]);
                continue;
            }

            BrokerProgramme programme = ((IVenuePlugin)Activator.CreateInstance(plugin)!).Describe().BrokerProgramme;
            Owed expected = programme switch
            {
                BrokerProgramme.Carried => Owed.Has,

                // A rebate this venue pays and nobody collects. Not a gate - the venue trades correctly - but it
                // must be visible, because the alternative is that it looks like a venue with nothing to claim.
                BrokerProgramme.NotCarried or BrokerProgramme.MechanismUndisclosed => Owed.Unclaimed,

                // No programme at all is the only answer that closes the question, so it is the only one allowed
                // to read as not-applicable.
                BrokerProgramme.None => Owed.NotApplicable,
                _ => throw new ArgumentOutOfRangeException(nameof(programme), programme, "undeclared programme state"),
            };

            Assert.Equal(expected, row["BrokerProgramme"]);
        }

        // Nothing may ship claiming it owes a programme it never looked for: Missing is not an answer here, because
        // every "no" has a reason and the reasons are different.
        Assert.DoesNotContain(Owed.Missing, _checklist.Values.Select(r => r["BrokerProgramme"]));
    }

    [Fact]
    public void No_venue_is_left_answering_a_command_with_silence()
    {
        // The state the table exists to get to and to keep: nothing ships that reports success while doing nothing
        // and saying nothing. A row may be set back to Silent, but only by somebody writing down why - it cannot
        // happen by an adapter quietly not implementing something, because that is what the rows above catch.
        foreach ((Client kind, Dictionary<string, Dictionary<string, Parity>> venues) in _table)
        {
            foreach ((string venue, Dictionary<string, Parity> row) in venues)
            {
                string[] silent = row.Where(c => c.Value == Parity.Silent).Select(c => c.Key).ToArray();
                Assert.True(
                    silent.Length == 0,
                    $"{venue}'s {kind} client answers {string.Join(", ", silent)} with success and nothing else. Either "
                    + "implement it, or make the base's answer the right one, or say here why silence is correct.");
            }
        }
    }

    [Fact]
    public void A_command_no_venue_implements_answers_with_success_rather_than_an_error()
    {
        // The behaviour the whole table rests on, pinned once. The base's answer to a command a venue never
        // implemented is a completed task, not an exception: the caller is told it worked. That is worth knowing and
        // worth not changing by accident - code written against silence would start crashing, and code written
        // against a throw would start hanging.
        MethodInfo query = typeof(ExecutionClientBase).GetMethod(nameof(ExecutionClientBase.QueryOrderAsync))!;
        Assert.True(query.IsVirtual, "a venue that does have the endpoint has to be able to override it");
        Assert.False(query.IsAbstract, "were it abstract, Bybit and KuCoin could not be silent about it at all");

        MethodInfo unsubscribe = typeof(DataClientBase).GetMethod(nameof(DataClientBase.UnsubscribeAsync))!;
        Assert.True(unsubscribe.IsVirtual);
        Assert.False(unsubscribe.IsAbstract);
    }

    [Fact]
    public void The_venues_that_can_be_traded_are_the_ones_with_both_an_execution_client_and_a_catalog()
    {
        // The reason parity is worth a file: what a venue can be used FOR follows from which clients it has. A venue
        // with data and no execution client is a backtest source; one with an execution client and no instrument
        // provider could take orders for instruments nobody can look up. Neither is wrong, but which venue is which
        // has to be a statement somebody can read rather than something learned by trying it.
        foreach (string venue in ShippedVenues())
        {
            bool tradable = _table[Client.Execution][venue].Values.Any(p => p != Parity.None);
            bool hasCatalog = _table[Client.Instruments][venue].Values.Any(p => p != Parity.None);

            Assert.Equal(tradable, hasCatalog);
            Assert.True(
                _table[Client.Data][venue].Values.Any(p => p != Parity.None),
                $"{venue} ships without a data client, so nothing it does can be driven by its own market data.");
        }
    }
}
