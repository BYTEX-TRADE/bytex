using System.Text.Json;
using System.Text.RegularExpressions;
using Bytex.Documents.Catalog;

namespace Bytex.Documents.Tests;

// Why: the catalog is the one source a palette, an inspector, a validator and an assistant all read, so what it
// promises is pinned here: every type is described well enough to be drawn and edited without reading the engine's
// code, wiring rules are the same for everyone who asks, and the export is stable enough to diff.
public class CatalogTests
{
    private static NodeCatalog Catalog => NodeCatalog.Default;

    /// <summary>Asserts the lookup found something and hands it back: this xUnit's Assert.NotNull returns nothing.</summary>
    private static T Present<T>(T? value, string because)
        where T : class
    {
        Assert.True(value is not null, because);
        return value!;
    }

    [Fact]
    public void The_builtin_catalog_describes_every_family_a_document_can_draw_from()
    {
        Dictionary<NodeKind, int> byKind = Catalog.Types.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(7, byKind[NodeKind.Data]);
        Assert.Equal(21, byKind[NodeKind.Indicator]);
        Assert.Equal(9, byKind[NodeKind.Level]);
        Assert.Equal(14, byKind[NodeKind.Condition]);
        Assert.Equal(13, byKind[NodeKind.Action]);
        Assert.Equal(8, byKind[NodeKind.Risk]);
        Assert.Equal(6, byKind[NodeKind.Flow]);
        Assert.Equal(2, byKind[NodeKind.Event]);
        Assert.Equal(80, Catalog.Types.Count);

        // A plugin's own types are the ninth kind and none are built in.
        Assert.False(byKind.ContainsKey(NodeKind.Custom));
        Assert.Equal("1.0", NodeCatalog.CatalogVersion);
    }

    [Fact]
    public void A_type_is_found_by_its_name_and_an_unknown_one_is_not_invented()
    {
        NodeTypeDescriptor ema = Present(Catalog.Find("ind.ema"), "ind.ema is missing from the catalog");

        Assert.Equal(NodeKind.Indicator, ema.Kind);
        Assert.Equal("EMA", ema.DisplayName);
        Assert.Equal("period", Present(ema.Param("period"), "EMA has no period").Name);
        Assert.Equal("bars", Present(ema.Input("bars"), "EMA reads no bars").Name);
        Assert.Equal(ValueKind.Series, Present(ema.Output("value"), "EMA publishes no value").Kind);

        Assert.True(Catalog.Contains("ind.ema"));
        Assert.False(Catalog.Contains("ind.nothing"));
        Assert.Null(Catalog.Find("ind.nothing"));
        Assert.Null(ema.Param("nothing"));
        Assert.Null(ema.Input("nothing"));
        Assert.Null(ema.Output("nothing"));
    }

    [Fact]
    public void One_type_name_belongs_to_one_descriptor()
    {
        NodeCatalog catalog = new([Catalog.Find("ind.ema")!]);

        InvalidOperationException twice = Assert.Throws<InvalidOperationException>(() => catalog.Register(Catalog.Find("ind.ema")!));
        Assert.Contains("ind.ema", twice.Message, StringComparison.Ordinal);
        Assert.Single(catalog.Types);
    }

    [Theory]
    // A number is a number, whatever it measures.
    [InlineData(ValueKind.Series, ValueKind.Price, true)]
    [InlineData(ValueKind.Price, ValueKind.Quantity, true)]
    [InlineData(ValueKind.Quantity, ValueKind.Series, true)]
    // A pulse is true for one bar, so it can feed anything that reads a condition - but not the other way round.
    [InlineData(ValueKind.Pulse, ValueKind.Bool, true)]
    [InlineData(ValueKind.Bool, ValueKind.Pulse, false)]
    // Bars and positions are handles, not numbers.
    [InlineData(ValueKind.Bars, ValueKind.Series, false)]
    [InlineData(ValueKind.Series, ValueKind.Bars, false)]
    [InlineData(ValueKind.Position, ValueKind.Bool, false)]
    [InlineData(ValueKind.Bars, ValueKind.Bars, true)]
    [InlineData(ValueKind.Position, ValueKind.Position, true)]
    public void A_port_takes_what_it_can_read_and_refuses_the_rest(ValueKind from, ValueKind to, bool compatible) =>
        Assert.Equal(compatible, PortSpec.Compatible(from, to));

    [Fact]
    public void Every_type_can_be_drawn_and_edited_from_its_description_alone()
    {
        foreach (NodeTypeDescriptor d in Catalog.Types)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Type), "a type without a name");
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName), d.Type);
            Assert.False(string.IsNullOrWhiteSpace(d.FaceTemplate), d.Type);
            Assert.NotEqual(NodeModes.None, d.Modes);
            Assert.Equal(d.Inputs.Count, d.Inputs.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(d.Outputs.Count, d.Outputs.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(d.Params.Count, d.Params.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.NotEmpty(d.Outputs);
        }
    }

    [Fact]
    public void What_a_face_names_is_a_parameter_or_a_port_of_the_same_node()
    {
        foreach (NodeTypeDescriptor d in Catalog.Types)
        {
            foreach (Match placeholder in Regex.Matches(d.FaceTemplate, @"\{([A-Za-z0-9_]+)\}"))
            {
                string name = placeholder.Groups[1].Value;
                bool known = d.Param(name) is not null || d.Input(name) is not null || d.Output(name) is not null;
                Assert.True(known, $"{d.Type} shows {{{name}}} on its card but has no such parameter or port");
            }
        }
    }

    [Fact]
    public void A_parameter_carries_a_value_to_start_from_and_the_limits_it_has_to_stay_within()
    {
        foreach (ParamSpec p in Catalog.Types.SelectMany(t => t.Params))
        {
            // A value a builder has to seed carries the value to start from. A reference to something the document
            // declares - an instrument, a bar type - does not: an absent instrument means the primary one.
            if (p.Type is ParamType.Int or ParamType.Decimal or ParamType.Bool or ParamType.Enum)
            {
                Assert.True(p.Default is not null, $"{p.Name} has nothing to start from");
            }
            else
            {
                Assert.True(p.Type is ParamType.Instrument or ParamType.BarType or ParamType.String or ParamType.Object or ParamType.Time,
                    $"{p.Name} is a {p.Type} with no default");
            }

            if (p.Type is ParamType.Enum)
            {
                Assert.NotNull(p.Choices);
                Assert.NotEmpty(p.Choices!);
                Assert.Contains(p.Default, p.ChoiceValues);
            }

            if (p.IsNumeric && p.Default is { } value)
            {
                decimal @default = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (p.Min is { } min)
                {
                    Assert.True(@default >= decimal.Parse(min, System.Globalization.CultureInfo.InvariantCulture), $"{p.Name} starts below its minimum");
                }

                if (p.Max is { } max)
                {
                    Assert.True(@default <= decimal.Parse(max, System.Globalization.CultureInfo.InvariantCulture), $"{p.Name} starts above its maximum");
                }
            }
        }
    }

    [Fact]
    public void An_indicator_reads_bars_and_says_when_it_has_seen_enough_of_them()
    {
        foreach (NodeTypeDescriptor d in Catalog.Types.Where(t => t.Kind == NodeKind.Indicator && t.Inputs.Any(i => i.Kind == ValueKind.Bars)))
        {
            PortSpec bars = Present(d.Input("bars"), $"{d.Type} reads no bars");
            Assert.Equal(ValueKind.Bars, bars.Kind);
            Assert.True(bars.Required, $"{d.Type} treats its bars as optional");
            Assert.Equal(ValueKind.Bool, Present(d.Output("ready"), $"{d.Type} never says it is ready").Kind);
            Assert.NotNull(d.Param("priceType"));
        }
    }

    [Fact]
    public void The_export_carries_the_whole_catalog_in_a_fixed_order()
    {
        string json = Catalog.ExportJson();

        Assert.Equal(json, Catalog.ExportJson());

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("1.0", doc.RootElement.GetProperty("catalogVersion").GetString());
        JsonElement[] types = doc.RootElement.GetProperty("types").EnumerateArray().ToArray();
        Assert.Equal(Catalog.Types.Count, types.Length);

        // Kinds are grouped in the order the model declares them, and one kind's types are in name order, so two
        // exports of one catalog diff cleanly.
        string[] kinds = types.Select(t => t.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal(["data", "indicator", "level", "condition", "action", "risk", "flow", "event"], kinds.Distinct());
        foreach (IGrouping<string, JsonElement> group in types.GroupBy(t => t.GetProperty("kind").GetString()!))
        {
            string[] names = group.Select(t => t.GetProperty("type").GetString()!).ToArray();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        }
    }

    [Fact]
    public void An_exported_type_says_everything_the_descriptor_says()
    {
        using JsonDocument doc = JsonDocument.Parse(Catalog.ExportJson());
        JsonElement donchian = doc.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "ind.donchian");

        Assert.Equal("indicator", donchian.GetProperty("kind").GetString());
        Assert.Equal("Donchian Channel", donchian.GetProperty("displayName").GetString());
        Assert.Equal("Donchian {period}", donchian.GetProperty("face").GetString());
        Assert.Equal(["lab", "paper", "live"], donchian.GetProperty("modes").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(["bars"], donchian.GetProperty("inputs").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(["upper", "middle", "lower", "ready"], donchian.GetProperty("outputs").EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        JsonElement period = donchian.GetProperty("params").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "period");
        Assert.Equal("int", period.GetProperty("type").GetString());
        Assert.Equal("20", period.GetProperty("default").GetString());
        Assert.Equal("1", period.GetProperty("min").GetString());

        JsonElement exclude = donchian.GetProperty("params").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "excludeCurrent");
        Assert.Equal("bool", exclude.GetProperty("type").GetString());
        Assert.Equal("false", exclude.GetProperty("default").GetString());
    }

    // The whole rule, written out rather than computed: a row per source kind, a column per target kind, in the order
    // of the enum. Changing what may feed what has to be a deliberate edit here as well as in PortSpec.
    private static readonly string[] CompatibilityMatrix =
    [
        //            bars series price quantity bool pulse position
        /* bars     */ "x      .     .      .      .    .      .",
        /* series   */ ".      x     x      x      .    .      .",
        /* price    */ ".      x     x      x      .    .      .",
        /* quantity */ ".      x     x      x      .    .      .",
        /* bool     */ ".      .     .      .      x    .      .",
        /* pulse    */ ".      .     .      .      x    x      .",
        /* position */ ".      .     .      .      .    .      x",
    ];

    [Fact]
    public void Every_pair_of_port_kinds_answers_the_way_the_table_says()
    {
        ValueKind[] kinds = Enum.GetValues<ValueKind>();

        Assert.Equal(kinds.Length, CompatibilityMatrix.Length);
        foreach (ValueKind from in kinds)
        {
            string[] row = CompatibilityMatrix[(int)from].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(kinds.Length, row.Length);
            foreach (ValueKind to in kinds)
            {
                bool expected = row[(int)to] == "x";
                Assert.True(expected == PortSpec.Compatible(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }

    [Fact]
    public void Everything_a_node_needs_can_be_fed_by_something_in_the_catalog()
    {
        ValueKind[] available = Catalog.Types.SelectMany(t => t.Outputs).Select(p => p.Kind).Distinct().ToArray();

        foreach (NodeTypeDescriptor d in Catalog.Types)
        {
            foreach (PortSpec input in d.Inputs.Where(i => i.Required))
            {
                NodeTypeDescriptor[] producers = Catalog.Types
                    .Where(t => t.Outputs.Any(o => PortSpec.Compatible(o.Kind, input.Kind)))
                    .ToArray();

                Assert.True(producers.Length > 0, $"{d.Type}:{input.Name} needs a {input.Kind} and nothing in the catalog produces one");
                Assert.Contains(input.Kind, available.Where(k => PortSpec.Compatible(k, input.Kind)));
            }
        }
    }

    [Fact]
    public void A_chain_a_strategy_would_actually_draw_is_accepted_end_to_end()
    {
        // bars -> EMA -> slope, and a quote price into a round-number level: the wiring a breakout document starts from.
        (string From, string FromPort, string To, string ToPort)[] chain =
        [
            ("data.bars", "bars", "ind.ema", "bars"),
            ("ind.ema", "value", "ind.slope", "value"),
            ("ind.ema", "value", "ind.distance", "reference"),
            ("data.bars", "bars", "level.range", "bars"),
            ("data.quotes", "mid", "level.round", "price"),
            ("data.quotes", "mid", "ind.zscore", "value"),
        ];

        foreach ((string from, string fromPort, string to, string toPort) in chain)
        {
            PortSpec output = Present(Present(Catalog.Find(from), $"{from} is missing").Output(fromPort), $"{from} has no {fromPort} output");
            PortSpec input = Present(Present(Catalog.Find(to), $"{to} is missing").Input(toPort), $"{to} has no {toPort} input");
            Assert.True(PortSpec.Compatible(output.Kind, input.Kind), $"{from}:{fromPort} ({output.Kind}) cannot feed {to}:{toPort} ({input.Kind})");
        }
    }

    [Fact]
    public void What_cannot_be_wired_is_refused_on_the_same_types()
    {
        NodeTypeDescriptor bars = Present(Catalog.Find("data.bars"), "data.bars is missing");
        NodeTypeDescriptor ema = Present(Catalog.Find("ind.ema"), "ind.ema is missing");

        // The one wiring that works between them, and the ones that do not: a bar stream is not a number, a number is
        // not a bar stream, and a ready flag is neither.
        Assert.True(PortSpec.Compatible(Present(bars.Output("bars"), "no bars output").Kind, Present(ema.Input("bars"), "no bars input").Kind));
        Assert.False(PortSpec.Compatible(Present(ema.Output("value"), "no value output").Kind, Present(ema.Input("bars"), "no bars input").Kind));
        Assert.False(PortSpec.Compatible(Present(bars.Output("bars"), "no bars output").Kind, Present(ema.Output("value"), "no value output").Kind));
        Assert.False(PortSpec.Compatible(Present(ema.Output("ready"), "no ready output").Kind, Present(ema.Input("bars"), "no bars input").Kind));
    }

    [Fact]
    public void A_type_is_filed_under_the_family_its_name_claims()
    {
        Dictionary<string, NodeKind> prefixes = new(StringComparer.Ordinal)
        {
            ["data"] = NodeKind.Data,
            ["ind"] = NodeKind.Indicator,
            ["level"] = NodeKind.Level,
            ["cond"] = NodeKind.Condition,
            ["act"] = NodeKind.Action,
            ["risk"] = NodeKind.Risk,
            ["flow"] = NodeKind.Flow,
            ["event"] = NodeKind.Event,
            ["custom"] = NodeKind.Custom,
        };

        foreach (NodeTypeDescriptor d in Catalog.Types)
        {
            // The first segment names the family; a family may group its types further, as cond.pattern.pinBar does.
            string[] parts = d.Type.Split('.');
            Assert.InRange(parts.Length, 2, 3);
            Assert.All(parts, part => Assert.False(string.IsNullOrWhiteSpace(part), d.Type));
            Assert.True(prefixes.ContainsKey(parts[0]), $"{d.Type} is in no known family");
            Assert.Equal(prefixes[parts[0]], d.Kind);
        }
    }

    [Fact]
    public void Every_action_can_be_told_when_to_act()
    {
        foreach (NodeTypeDescriptor d in Catalog.Types.Where(t => t.Kind == NodeKind.Action))
        {
            // Trailing and the grid stand for as long as their condition holds; every other action fires on one.
            string expected = d.Type is "act.trail" or "act.grid" ? "enable" : "trigger";
            PortSpec port = Present(d.Input(expected), $"{d.Type} cannot be told when to act");
            Assert.True(port.Required, $"{d.Type} treats its {expected} as optional");
            Assert.True(PortSpec.Compatible(ValueKind.Bool, port.Kind), $"{d.Type} does not read a condition");
            Assert.True(PortSpec.Compatible(ValueKind.Pulse, port.Kind), $"{d.Type} cannot be fired by a pulse");
        }
    }

    [Fact]
    public void Every_condition_publishes_something_another_condition_can_read()
    {
        foreach (NodeTypeDescriptor d in Catalog.Types.Where(t => t.Kind == NodeKind.Condition))
        {
            Assert.Contains(d.Outputs, o => PortSpec.Compatible(o.Kind, ValueKind.Bool));
        }
    }

    [Fact]
    public void An_action_that_places_an_order_says_how_much_to_trade()
    {
        foreach (string type in new[] { "act.order", "act.bracket", "act.ladder" })
        {
            NodeTypeDescriptor d = Present(Catalog.Find(type), $"{type} is missing");
            ParamSpec sizing = Present(d.Param("sizing"), $"{type} does not say how much to trade");

            Assert.Equal(ParamType.Object, sizing.Type);
            Assert.True(sizing.Required, $"{type} leaves its size optional");
            Assert.NotNull(sizing.Fields);
            ParamSpec mode = Present(sizing.Fields!.FirstOrDefault(f => f.Name == "mode"), $"{type} sizing has no mode");
            Assert.Equal(["fixed", "notional", "percentOfBalance", "riskPercent"], mode.ChoiceValues);
            Assert.Contains(sizing.Fields!, f => f.Name == "value" && f.IsNumeric);
        }
    }

    [Fact]
    public void An_event_filter_leaves_out_what_a_model_classified_until_it_is_asked_for()
    {
        ParamSpec[] filters = Catalog.Types
            .SelectMany(t => t.Params.Concat(t.Params.SelectMany(p => p.Fields ?? [])))
            .Where(p => p.Name == "includeAiClassified")
            .ToArray();

        Assert.NotEmpty(filters);
        foreach (ParamSpec filter in filters)
        {
            Assert.Equal(ParamType.Bool, filter.Type);
            Assert.Equal("false", filter.Default);
            Assert.Contains("allowAiAnnotationConditions", filter.Description ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_node_that_carries_state_across_a_phase_says_whether_entering_it_resets_that_state()
    {
        foreach (string type in new[] { "flow.counter", "flow.latch", "cond.once" })
        {
            NodeTypeDescriptor d = Present(Catalog.Find(type), $"{type} is missing");
            ParamSpec reset = Present(d.Param("resetOnPhaseEntry"), $"{type} does not say what entering a phase does to it");

            Assert.Equal(ParamType.Bool, reset.Type);
            Assert.Equal("true", reset.Default);
        }
    }

    /// <summary>Every parameter list in the catalog: each node's own parameters, and the fields of every object in them.</summary>
    private static IEnumerable<(string Type, string Path, IReadOnlyList<ParamSpec> Params)> ParamLists()
    {
        foreach (NodeTypeDescriptor d in Catalog.Types)
        {
            Queue<(string Path, IReadOnlyList<ParamSpec> Params)> pending = new();
            pending.Enqueue((string.Empty, d.Params));
            while (pending.Count > 0)
            {
                (string path, IReadOnlyList<ParamSpec> list) = pending.Dequeue();
                yield return (d.Type, path, list);
                foreach (ParamSpec p in list)
                {
                    if (p.Fields is { } fields)
                    {
                        pending.Enqueue((path.Length == 0 ? p.Name : path + "." + p.Name, fields));
                    }
                }
            }
        }
    }

    private static string Name(string path, ParamSpec p) => path.Length == 0 ? p.Name : path + "." + p.Name;

    // Why: a dropdown that offers "riskPercent" tells a trader nothing, and what it does - size the position so that a
    // stop-out costs that percentage - is the opposite of what the word suggests to most people who read it. So every
    // option carries its own name and its own sentence, and neither may be left out.
    [Fact]
    public void Every_option_an_enum_offers_says_in_words_what_picking_it_does()
    {
        List<string> seen = [];
        foreach ((string type, string path, IReadOnlyList<ParamSpec> list) in ParamLists())
        {
            foreach (ParamSpec p in list.Where(p => p.Type is ParamType.Enum))
            {
                string where = $"{type} {Name(path, p)}";
                Assert.NotNull(p.Choices);
                Assert.NotEmpty(p.Choices!);
                Assert.Contains(p.Default, p.ChoiceValues);
                Assert.Equal(p.ChoiceValues.Distinct(StringComparer.Ordinal).Count(), p.Choices!.Count);
                Assert.False(string.IsNullOrWhiteSpace(p.Description), $"{where} never says what it decides");

                foreach (ChoiceSpec c in p.Choices!)
                {
                    Assert.False(string.IsNullOrWhiteSpace(c.Label), $"{where} offers '{c.Value}' with nothing to show for it");
                    Assert.NotEqual(c.Value, c.Label);
                    Assert.True(c.Description.Length > 20 && c.Description.EndsWith(".", StringComparison.Ordinal),
                        $"{where}: '{c.Value}' is not explained in a sentence");
                    seen.Add(where + ":" + c.Value);
                }
            }
        }

        // The walk has to reach the nested objects, where the worst of the confusion lived.
        Assert.Contains("act.order sizing.mode:riskPercent", seen);
        Assert.Contains("risk.exit stop.offset.unit:atr", seen);
    }

    // Why: the confusion that started this was never one bad word, it was a number labelled "Value" whose meaning
    // changed with the option beside it. An option that decides how a number is read has to name that number and say
    // what it is then measured in; an option that leaves the number unused has to say that in its sentence.
    [Fact]
    public void An_option_that_decides_what_the_number_beside_it_means_says_so()
    {
        // The two options that deliberately leave the number unused: a target taken from an input, and no target at all.
        (string Type, string Param, string Value)[] numberNotUsed =
        [
            ("risk.exit", "target.unit", "level"),
            ("risk.exit", "target.unit", "none"),
        ];

        foreach ((string type, string path, IReadOnlyList<ParamSpec> list) in ParamLists())
        {
            foreach (ParamSpec p in list.Where(p => p.Type is ParamType.Enum))
            {
                string name = Name(path, p);
                foreach (ChoiceSpec c in p.Choices!)
                {
                    if (c.Governs is not { } governed)
                    {
                        continue;
                    }

                    Assert.Contains(list, sibling => sibling.Name == governed && !ReferenceEquals(sibling, p));
                    Assert.False(string.IsNullOrWhiteSpace(c.Unit),
                        $"{type} {name}: '{c.Value}' decides how {governed} is read but never says in what unit");
                }

                // An enum that names a unit, a mode or where something moves to, standing next to a number, decides
                // what that number means. Every one of its options says which, or says the number is not used.
                if (p.Name is not ("unit" or "mode" or "to") || !list.Any(s => s.IsNumeric))
                {
                    continue;
                }

                foreach (ChoiceSpec c in p.Choices!)
                {
                    if (numberNotUsed.Contains((type, name, c.Value)))
                    {
                        Assert.Null(c.Governs);
                        Assert.Contains("not used", c.Description, StringComparison.Ordinal);
                        continue;
                    }

                    Assert.False(c.Governs is null, $"{type} {name}: '{c.Value}' never says which number it reads");
                }
            }
        }
    }

    // Why: this is the case of record. Eight shipped templates sized by riskPercent and opened positions worth the
    // whole account, because "percent" read as "percent of the account to spend". The catalog now says otherwise.
    [Fact]
    public void Sizing_says_what_each_mode_stakes_and_that_a_near_stop_buys_a_large_position()
    {
        ParamSpec sizing = Present(Catalog.Find("act.order")?.Param("sizing"), "act.order does not say how much to trade");
        ParamSpec mode = Present(sizing.Fields!.FirstOrDefault(f => f.Name == "mode"), "sizing has no mode");

        Assert.Equal(["fixed", "notional", "percentOfBalance", "riskPercent"], mode.ChoiceValues);
        foreach (ChoiceSpec c in mode.Choices!)
        {
            Assert.Equal("value", c.Governs);
            Assert.False(string.IsNullOrWhiteSpace(c.Unit), $"sizing mode '{c.Value}' never says what the size is counted in");
        }

        ChoiceSpec risk = Present(mode.Choice("riskPercent"), "sizing can no longer size from the stop");
        Assert.Contains("stop", risk.Unit!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stakes the whole balance", risk.Description, StringComparison.Ordinal);
        Assert.Equal("100", risk.Max);

        ChoiceSpec spend = Present(mode.Choice("percentOfBalance"), "sizing can no longer spend a share of the balance");
        Assert.NotEqual(risk.Unit, spend.Unit);
    }

    // Why: a host draws its inspector from the export, so an option's meaning has to survive the wire.
    [Fact]
    public void The_export_carries_what_every_option_means()
    {
        using JsonDocument doc = JsonDocument.Parse(Catalog.ExportJson());
        JsonElement order = doc.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "act.order");
        JsonElement mode = order.GetProperty("params").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "sizing")
            .GetProperty("fields").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "mode");

        JsonElement[] choices = mode.GetProperty("choices").EnumerateArray().ToArray();
        Assert.Equal(["fixed", "notional", "percentOfBalance", "riskPercent"], choices.Select(c => c.GetProperty("value").GetString()));
        foreach (JsonElement choice in choices)
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("description").GetString()));
            Assert.Equal("value", choice.GetProperty("governs").GetString());
        }

        JsonElement risk = choices.Single(c => c.GetProperty("value").GetString() == "riskPercent");
        Assert.Contains("stop", risk.GetProperty("unit").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("100", risk.GetProperty("max").GetString());
        Assert.Equal("0.1", risk.GetProperty("step").GetString());

        // Nothing is invented for an option that governs nothing: a time in force has no unit and no governed number.
        JsonElement tif = order.GetProperty("params").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "tif");
        foreach (JsonElement choice in tif.GetProperty("choices").EnumerateArray())
        {
            Assert.False(choice.TryGetProperty("unit", out _));
            Assert.False(choice.TryGetProperty("governs", out _));
        }
    }
}
