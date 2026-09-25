using Bytex.Core.Model.Data;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class ConditionNodes
{
    private static readonly PortSpec Out = new("out", ValueKind.Bool, Label: "condition");

    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "cond.cross",
            Kind = NodeKind.Condition,
            DisplayName = "Cross",
            Description = "True on the bar where the value crosses the reference.",
            FaceTemplate = "{value} crosses {direction} {reference}",
            Inputs = [new PortSpec("value", ValueKind.Series, Required: true), new PortSpec("reference", ValueKind.Series, Required: true)],
            Outputs = [Out, new PortSpec("above", ValueKind.Bool, Description: "value is currently above the reference")],
            Params =
            [
                P.Enum("direction", "above", "Direction", "Which crossing fires the condition.",
                    P.Choice("above", "Crossing up", "Fires on the bar where the value goes from at or below the reference to above it."),
                    P.Choice("below", "Crossing down", "Fires on the bar where the value goes from at or above the reference to below it."),
                    P.Choice("either", "Either way", "Fires on any bar where the value crosses the reference, in either direction.")),
                P.Enum("confirm", "barClose", "Confirm on", "When the crossing is judged.",
                    P.Choice("barClose", "The bar close", "Completed bars only, so a crossing that reverses inside a bar never fires."),
                    P.Choice("instant", "Every tick", "Reserved for tick evaluation. Nothing evaluates ticks yet, so this behaves exactly like the bar close.")),
            ],
            Factory = ctx => new CrossNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.compare",
            Kind = NodeKind.Condition,
            DisplayName = "Compare",
            Description = "Compares a value with another series or with a constant.",
            FaceTemplate = "{a} {op} {b}",
            Inputs = [new PortSpec("a", ValueKind.Series, Required: true), new PortSpec("b", ValueKind.Series, Description: "leave unconnected to compare with the constant")],
            Outputs = [Out],
            Params =
            [
                P.Enum("op", "gt", "Operator", "How a is compared with b.",
                    P.Choice("gt", "Greater than", "True while a is above b."),
                    P.Choice("gte", "Greater than or equal", "True while a is above b or exactly equal to it; the tolerance is not used."),
                    P.Choice("lt", "Less than", "True while a is below b."),
                    P.Choice("lte", "Less than or equal", "True while a is below b or exactly equal to it; the tolerance is not used."),
                    P.Choice("eq", "Equal", "True while a and b are no further apart than the tolerance."),
                    P.Choice("neq", "Not equal", "True while a and b are further apart than the tolerance.")),
                P.Dec("value", 0m, null, null, null, "Constant"),
                P.Dec("tolerance", 0m, 0m, null, null, "Tolerance", null, "For eq and neq."),
            ],
            Factory = ctx => new CompareNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.inBand",
            Kind = NodeKind.Condition,
            DisplayName = "Inside band",
            Description = "True while the value sits between lower and upper.",
            FaceTemplate = "{value} inside {lower} to {upper}",
            Inputs = [new PortSpec("value", ValueKind.Series, Required: true), new PortSpec("lower", ValueKind.Series, Required: true), new PortSpec("upper", ValueKind.Series, Required: true)],
            Outputs = [Out],
            Params = [P.Bool("inclusive", true)],
            Factory = ctx => new InBandNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.holdFor",
            Kind = NodeKind.Condition,
            DisplayName = "Hold for N bars",
            Description = "True once the input has been true for N consecutive bars.",
            FaceTemplate = "{in} holds for {bars} bars",
            Inputs = [new PortSpec("in", ValueKind.Bool, Required: true)],
            Outputs = [Out, new PortSpec("count", ValueKind.Series)],
            Params = [P.Int("bars", 3, 1, 5000)],
            Factory = ctx => new HoldForNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.pattern.pinBar",
            Kind = NodeKind.Condition,
            DisplayName = "Pin bar",
            Description = "A bar with a long wick and a small body, rejecting a level.",
            FaceTemplate = "{direction} pin bar",
            Inputs = [new PortSpec("bars", ValueKind.Bars, Required: true)],
            Outputs = [Out],
            Params =
            [
                P.Enum("direction", "bullish", "Direction", "Which way the bar has to reject.",
                    P.Choice("bullish", "Bullish", "The long wick below the body: the bar was sold down and came back."),
                    P.Choice("bearish", "Bearish", "The long wick above the body: the bar was bought up and came back."),
                    P.Choice("either", "Either way", "A long wick on either side.")),
                P.Dec("wickRatio", 0.66m, 0.3m, 0.95m, 0.01m, "Min wick / range"),
                P.Dec("bodyMax", 0.3m, 0.05m, 0.6m, 0.01m, "Max body / range"),
            ],
            Factory = ctx => new PinBarNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.pattern.engulfing",
            Kind = NodeKind.Condition,
            DisplayName = "Engulfing",
            Description = "The current body engulfs the previous body in the opposite direction.",
            FaceTemplate = "{direction} engulfing bar",
            Inputs = [new PortSpec("bars", ValueKind.Bars, Required: true)],
            Outputs = [Out],
            Params =
            [
                P.Enum("direction", "bullish", "Direction", "Which way the engulfing bar has to go.",
                    P.Choice("bullish", "Bullish", "An up bar whose body covers the previous down bar's body."),
                    P.Choice("bearish", "Bearish", "A down bar whose body covers the previous up bar's body."),
                    P.Choice("either", "Either way", "Either of the two, whichever way the engulfing bar goes.")),
            ],
            Factory = ctx => new EngulfingNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.pattern.divergence",
            Kind = NodeKind.Condition,
            DisplayName = "Divergence",
            Description = "Price makes a new extreme over the lookback while the oscillator does not.",
            FaceTemplate = "{type} divergence between {price} and {oscillator} over {lookback} bars",
            Inputs = [new PortSpec("price", ValueKind.Series, Required: true), new PortSpec("oscillator", ValueKind.Series, Required: true)],
            Outputs = [Out],
            Params =
            [
                P.Enum("type", "bullish", "Type", "Which divergence to look for.",
                    P.Choice("bullish", "Bullish", "Price makes a lower low over the lookback while the oscillator makes a higher one."),
                    P.Choice("bearish", "Bearish", "Price makes a higher high over the lookback while the oscillator makes a lower one.")),
                P.Int("lookback", 20, 3, 1000),
            ],
            Factory = ctx => new DivergenceNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.time.window",
            Kind = NodeKind.Condition,
            DisplayName = "Time window",
            Description = "True while the bar time is inside a UTC window; the window may wrap midnight.",
            FaceTemplate = "Between {from} and {to} UTC",
            Outputs = [Out],
            Params = [P.Time("from", "08:00"), P.Time("to", "16:00"), P.Str("days", "mon,tue,wed,thu,fri,sat,sun", false, "Days", "Comma-separated weekday names.")],
            Factory = ctx => new TimeWindowNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.time.session",
            Kind = NodeKind.Condition,
            DisplayName = "Session phase",
            Description = "True during a part of the session: inside, outside, its first N bars, or its last N bars.",
            FaceTemplate = "{phase} the session {start} to {end}",
            Outputs = [Out],
            Params =
            [
                P.Time("start", "00:00"),
                P.Time("end", "23:59"),
                P.Enum("phase", "inside", "Phase", "Which part of the session the condition holds in.",
                    P.Choice("inside", "Inside the session", "True on every bar between the start and the end; the bars count is not used."),
                    P.Choice("outside", "Outside the session", "True on every bar that is not between the start and the end; the bars count is not used."),
                    P.Choice("open", "The first bars", "True for the first bars of the session, counting from the start.", "bars", "bars", min: "1"),
                    P.Choice("close", "The last bars", "True for the last bars of the session, counting back from the end.", "bars", "bars", min: "1")),
                P.Int("bars", 4, 1, 1000, "Bars", null, "For open and close phases."),
            ],
            Factory = ctx => new SessionPhaseNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.time.afterBars",
            Kind = NodeKind.Condition,
            DisplayName = "After N bars",
            Description = "True once N bars have passed since the last pulse on the input.",
            FaceTemplate = "{bars} bars after {since}",
            Inputs = [new PortSpec("since", ValueKind.Pulse, Required: true)],
            Outputs = [Out, new PortSpec("elapsed", ValueKind.Series)],
            Params = [P.Int("bars", 12, 1, 100_000)],
            Factory = ctx => new AfterBarsNode(ctx),
        };

        yield return Logic("cond.all", "All of", "True when every connected input is true.", "all of {a}, {b}, {c}, {d}", (a, b, c, d) => a && b && c && d, identity: true);
        yield return Logic("cond.any", "Any of", "True when at least one connected input is true.", "any of {a}, {b}, {c}, {d}", (a, b, c, d) => a || b || c || d, identity: false);

        yield return new NodeTypeDescriptor
        {
            Type = "cond.not",
            Kind = NodeKind.Condition,
            DisplayName = "Not",
            Description = "Inverts a condition.",
            FaceTemplate = "not {in}",
            Inputs = [new PortSpec("in", ValueKind.Bool, Required: true)],
            Outputs = [Out],
            Factory = ctx => new NotNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "cond.once",
            Kind = NodeKind.Condition,
            DisplayName = "Once",
            Description = "Passes the first true only, until reset or until the phase is entered again.",
            FaceTemplate = "{in}, once",
            Inputs = [new PortSpec("in", ValueKind.Bool, Required: true), new PortSpec("reset", ValueKind.Pulse)],
            Outputs = [Out],
            Params = [P.Bool("resetOnPhaseEntry", true, "Reset when the phase is entered")],
            Factory = ctx => new OnceNode(ctx),
        };
    }

    private static NodeTypeDescriptor Logic(string type, string name, string description, string face, Func<bool, bool, bool, bool, bool> combine, bool identity) => new()
    {
        Type = type,
        Kind = NodeKind.Condition,
        DisplayName = name,
        Description = description,
        FaceTemplate = face,
        Inputs = [new PortSpec("a", ValueKind.Bool, Required: true), new PortSpec("b", ValueKind.Bool), new PortSpec("c", ValueKind.Bool), new PortSpec("d", ValueKind.Bool)],
        Outputs = [Out],
        Factory = ctx => new LogicNode(ctx, combine, identity),
    };

    private sealed class CrossNode : NodeBase
    {
        private int _previousSign;
        private bool _hasPrevious;

        public CrossNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("value") is not { } v || ctx.Dec("reference") is not { } r)
            {
                ctx.Set("out", false);
                return;
            }

            int sign = v > r ? 1 : v < r ? -1 : 0;
            bool crossedAbove = _hasPrevious && _previousSign <= 0 && sign > 0;
            bool crossedBelow = _hasPrevious && _previousSign >= 0 && sign < 0;
            string direction = P.Str("direction", "above");
            bool fired = direction switch
            {
                "below" => crossedBelow,
                "either" => crossedAbove || crossedBelow,
                _ => crossedAbove,
            };
            if (sign != 0)
            {
                _previousSign = sign;
                _hasPrevious = true;
            }

            ctx.Set("out", fired);
            ctx.Set("above", sign > 0);
            if (fired)
            {
                ctx.Emit("condition", $"crossed {(crossedAbove ? "above" : "below")}", new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = F(v), ["reference"] = F(r) });
            }
        }
    }

    private sealed class CompareNode : NodeBase
    {
        public CompareNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("a") is not { } a)
            {
                ctx.Set("out", false);
                return;
            }

            decimal b = ctx.IsConnected("b") ? ctx.Dec("b") ?? decimal.MinValue : P.Dec("value", 0m);
            if (b == decimal.MinValue)
            {
                ctx.Set("out", false);
                return;
            }

            decimal tolerance = P.Dec("tolerance", 0m);
            bool result = P.Str("op", "gt") switch
            {
                "gte" => a >= b,
                "lt" => a < b,
                "lte" => a <= b,
                "eq" => Math.Abs(a - b) <= tolerance,
                "neq" => Math.Abs(a - b) > tolerance,
                _ => a > b,
            };
            ctx.Set("out", result);
        }
    }

    private sealed class InBandNode : NodeBase
    {
        public InBandNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("value") is not { } v || ctx.Dec("lower") is not { } lo || ctx.Dec("upper") is not { } hi)
            {
                ctx.Set("out", false);
                return;
            }

            bool inclusive = P.Bool("inclusive", true);
            ctx.Set("out", inclusive ? v >= lo && v <= hi : v > lo && v < hi);
        }
    }

    private sealed class HoldForNode : NodeBase
    {
        private int _count;

        public HoldForNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            _count = ctx.Bool("in") ? _count + 1 : 0;
            ctx.Set("count", (decimal)_count);
            ctx.Set("out", _count >= P.Int("bars", 3));
        }
    }

    private sealed class PinBarNode : NodeBase
    {
        public PinBarNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Bars("bars") is not { } bar)
            {
                ctx.Set("out", false);
                return;
            }

            decimal range = BarMath.Range(bar);
            if (range <= 0m)
            {
                ctx.Set("out", false);
                return;
            }

            decimal body = BarMath.Body(bar) / range;
            decimal lowerWick = (Math.Min(bar.Open.Value, bar.Close.Value) - bar.Low.Value) / range;
            decimal upperWick = (bar.High.Value - Math.Max(bar.Open.Value, bar.Close.Value)) / range;
            decimal wickRatio = P.Dec("wickRatio", 0.66m);
            decimal bodyMax = P.Dec("bodyMax", 0.3m);
            bool bullish = body <= bodyMax && lowerWick >= wickRatio;
            bool bearish = body <= bodyMax && upperWick >= wickRatio;
            ctx.Set("out", P.Str("direction", "bullish") switch { "bearish" => bearish, "either" => bullish || bearish, _ => bullish });
        }
    }

    private sealed class EngulfingNode : NodeBase, IBarObserver
    {
        private Bar? _previous;
        private Bar? _current;

        public EngulfingNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnBar(Bar bar)
        {
            _previous = _current;
            _current = bar;
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (_previous is not { } p || _current is not { } c)
            {
                ctx.Set("out", false);
                return;
            }

            decimal pHi = Math.Max(p.Open.Value, p.Close.Value), pLo = Math.Min(p.Open.Value, p.Close.Value);
            decimal cHi = Math.Max(c.Open.Value, c.Close.Value), cLo = Math.Min(c.Open.Value, c.Close.Value);
            bool engulfs = cHi > pHi && cLo < pLo;
            bool bullish = engulfs && BarMath.IsUp(c) && !BarMath.IsUp(p);
            bool bearish = engulfs && !BarMath.IsUp(c) && BarMath.IsUp(p);
            ctx.Set("out", P.Str("direction", "bullish") switch { "bearish" => bearish, "either" => bullish || bearish, _ => bullish });
        }
    }

    private sealed class DivergenceNode : NodeBase
    {
        private readonly History _price;
        private readonly History _osc;

        public DivergenceNode(NodeBuildContext ctx)
            : base(ctx)
        {
            int lookback = P.Int("lookback", 20);
            _price = new History(lookback);
            _osc = new History(lookback);
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("price") is not { } p || ctx.Dec("oscillator") is not { } o)
            {
                ctx.Set("out", false);
                return;
            }

            bool ready = _price.IsFull;
            bool result = false;
            if (ready)
            {
                if (P.Str("type", "bullish") == "bullish")
                {
                    result = p < _price.Min() && o > _osc.Min();
                }
                else
                {
                    result = p > _price.Max() && o < _osc.Max();
                }
            }

            _price.Add(p);
            _osc.Add(o);
            ctx.Set("out", result);
        }
    }

    private sealed class TimeWindowNode : NodeBase
    {
        public TimeWindowNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            DateTimeOffset t = BarMath.Time(ctx.Bar);
            TimeOnly now = TimeOnly.FromDateTime(t.UtcDateTime);
            TimeOnly from = P.Time("from", new TimeOnly(8, 0));
            TimeOnly to = P.Time("to", new TimeOnly(16, 0));
            bool inside = from <= to ? now >= from && now < to : now >= from || now < to;
            string days = P.Str("days", "mon,tue,wed,thu,fri,sat,sun");
            string today = t.UtcDateTime.DayOfWeek.ToString()[..3].ToLowerInvariant();
            bool dayOk = days.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(d => d.Equals(today, StringComparison.OrdinalIgnoreCase));
            ctx.Set("out", inside && dayOk);
        }
    }

    private sealed class SessionPhaseNode : NodeBase
    {
        private DateOnly? _day;
        private int _barsSinceStart;

        public SessionPhaseNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            DateTimeOffset t = BarMath.Time(ctx.Bar);
            TimeOnly now = TimeOnly.FromDateTime(t.UtcDateTime);
            TimeOnly start = P.Time("start", new TimeOnly(0, 0));
            TimeOnly end = P.Time("end", new TimeOnly(23, 59));
            bool inside = start <= end ? now >= start && now <= end : now >= start || now <= end;
            DateOnly day = DateOnly.FromDateTime((t - start.ToTimeSpan()).UtcDateTime);
            if (_day != day)
            {
                _day = day;
                _barsSinceStart = 0;
            }

            if (inside)
            {
                _barsSinceStart++;
            }

            int bars = P.Int("bars", 4);
            long barNanos = ctx.Bar.BarType.Spec.IsTimeAggregated ? ctx.Bar.BarType.Spec.IntervalNanos : 0L;
            TimeSpan toEnd = end.ToTimeSpan() - now.ToTimeSpan();
            if (toEnd < TimeSpan.Zero)
            {
                toEnd += TimeSpan.FromDays(1);
            }

            bool closing = inside && barNanos > 0 && toEnd.Ticks * 100L <= bars * barNanos;
            ctx.Set("out", P.Str("phase", "inside") switch
            {
                "outside" => !inside,
                "open" => inside && _barsSinceStart <= bars,
                "close" => closing,
                _ => inside,
            });
        }
    }

    private sealed class AfterBarsNode : NodeBase
    {
        private long? _since;

        public AfterBarsNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Bool("since"))
            {
                _since = ctx.Index;
            }

            if (_since is null)
            {
                ctx.Set("out", false);
                return;
            }

            long elapsed = ctx.Index - _since.Value;
            ctx.Set("elapsed", (decimal)elapsed);
            ctx.Set("out", elapsed >= P.Int("bars", 12));
        }
    }

    private sealed class LogicNode : NodeBase
    {
        private readonly Func<bool, bool, bool, bool, bool> _combine;
        private readonly bool _identity;

        public LogicNode(NodeBuildContext ctx, Func<bool, bool, bool, bool, bool> combine, bool identity)
            : base(ctx)
        {
            _combine = combine;
            _identity = identity;
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool In(string port) => ctx.IsConnected(port) ? ctx.Bool(port) : _identity;
            ctx.Set("out", _combine(In("a"), In("b"), In("c"), In("d")));
        }
    }

    private sealed class NotNode : NodeBase
    {
        public NotNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx) => ctx.Set("out", !ctx.Bool("in"));
    }

    private sealed class OnceNode : NodeBase, IPhaseAware, IStatefulNode
    {
        private bool _fired;

        public OnceNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPhaseEntered()
        {
            if (P.Bool("resetOnPhaseEntry", true))
            {
                _fired = false;
            }
        }

        public void OnPhaseExited()
        {
        }

        public System.Text.Json.JsonElement SaveState() => System.Text.Json.JsonSerializer.SerializeToElement(new { fired = _fired });

        public void LoadState(System.Text.Json.JsonElement state) => _fired = state.TryGetProperty("fired", out System.Text.Json.JsonElement f) && f.ValueKind == System.Text.Json.JsonValueKind.True;

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Bool("reset"))
            {
                _fired = false;
            }

            bool input = ctx.Bool("in");
            bool pass = input && !_fired;
            if (pass)
            {
                _fired = true;
            }

            ctx.Set("out", pass);
        }
    }
}
