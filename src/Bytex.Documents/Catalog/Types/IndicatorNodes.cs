using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Documents.Runtime;
using Bytex.Indicators;

namespace Bytex.Documents.Catalog.Types;

internal static class IndicatorNodes
{
    private static readonly PortSpec BarsIn = new("bars", ValueKind.Bars, Required: true, Label: "bars");

    private static readonly ParamSpec PriceTypeParam = P.Enum("priceType", "last", "Price", "Which bar price feeds the indicator.",
        P.Choice("last", "Last traded", "The bars built from trades: the close of each one."),
        P.Choice("bid", "Bid", "The bars built from the best bid."),
        P.Choice("ask", "Ask", "The bars built from the best ask."),
        P.Choice("mid", "Mid", "The bars built from the midpoint between bid and ask."));

    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return Single("ind.sma", "SMA", "Simple moving average", "SMA {period}", p => new SimpleMovingAverage(p.Int("period", 20), PriceTypeOf(p)), i => ((SimpleMovingAverage)i).Value, P.Int("period", 20));
        yield return Single("ind.ema", "EMA", "Exponential moving average", "EMA {period}", p => new ExponentialMovingAverage(p.Int("period", 20), PriceTypeOf(p)), i => ((ExponentialMovingAverage)i).Value, P.Int("period", 20));
        yield return Single("ind.wma", "WMA", "Weighted moving average", "WMA {period}", p => new WeightedMovingAverage(p.Int("period", 20), PriceTypeOf(p)), i => ((WeightedMovingAverage)i).Value, P.Int("period", 20));
        yield return Single("ind.dema", "DEMA", "Double exponential moving average", "DEMA {period}", p => new DoubleExponentialMovingAverage(p.Int("period", 20), PriceTypeOf(p)), i => ((DoubleExponentialMovingAverage)i).Value, P.Int("period", 20));
        yield return Single("ind.hma", "HMA", "Hull moving average", "HMA {period}", p => new HullMovingAverage(p.Int("period", 20), PriceTypeOf(p)), i => ((HullMovingAverage)i).Value, P.Int("period", 20));
        yield return Single("ind.vwap", "VWAP", "Volume-weighted average price, resetting daily", "VWAP (daily)", _ => new VolumeWeightedAveragePrice(), i => ((VolumeWeightedAveragePrice)i).Value);
        yield return Single("ind.rsi", "RSI", "Relative strength index, 0 to 100", "RSI {period}", p => new RelativeStrengthIndex(p.Int("period", 14), PriceTypeOf(p)), i => ((RelativeStrengthIndex)i).Value, P.Int("period", 14));
        yield return Single("ind.roc", "ROC", "Rate of change, percent", "ROC {period}", p => new RateOfChange(p.Int("period", 10), PriceTypeOf(p)), i => ((RateOfChange)i).Value, P.Int("period", 10));
        yield return Single("ind.obv", "OBV", "On-balance volume", "OBV", _ => new OnBalanceVolume(), i => ((OnBalanceVolume)i).Value);
        yield return Single("ind.atr", "ATR", "Average true range", "ATR {period}", p => new AverageTrueRange(p.Int("period", 14)), i => ((AverageTrueRange)i).Value, P.Int("period", 14));

        yield return Multi("ind.macd", "MACD", "Moving average convergence/divergence", "MACD {fast}/{slow}/{signal}",
            p => new MovingAverageConvergenceDivergence(p.Int("fast", 12), p.Int("slow", 26), p.Int("signal", 9), PriceTypeOf(p)),
            new (string, Func<IIndicator, decimal>)[] { ("value", i => ((MovingAverageConvergenceDivergence)i).Value), ("signal", i => ((MovingAverageConvergenceDivergence)i).Signal), ("histogram", i => ((MovingAverageConvergenceDivergence)i).Histogram) },
            P.Int("fast", 12), P.Int("slow", 26), P.Int("signal", 9));
        yield return Multi("ind.stoch", "Stochastics", "Stochastic oscillator %K and %D", "Stochastics {k}/{d}",
            p => new Stochastics(p.Int("k", 14), p.Int("d", 3)),
            new (string, Func<IIndicator, decimal>)[] { ("k", i => ((Stochastics)i).ValueK), ("d", i => ((Stochastics)i).ValueD) },
            P.Int("k", 14), P.Int("d", 3));
        yield return Multi("ind.bbands", "Bollinger Bands", "Moving average with standard-deviation bands", "Bollinger {period} × {k}",
            p => new BollingerBands(p.Int("period", 20), p.Dec("k", 2m), PriceTypeOf(p)),
            new (string, Func<IIndicator, decimal>)[] { ("upper", i => ((BollingerBands)i).Upper), ("middle", i => ((BollingerBands)i).Middle), ("lower", i => ((BollingerBands)i).Lower), ("width", i => ((BollingerBands)i).Width) },
            P.Int("period", 20), P.Dec("k", 2m, 0.1m, 10m, 0.1m));
        yield return Multi("ind.keltner", "Keltner Channel", "EMA with ATR bands", "Keltner {period} × {k}",
            p => new KeltnerChannel(p.Int("period", 20), p.Dec("k", 2m), p.Int("atrPeriod", 10)),
            new (string, Func<IIndicator, decimal>)[] { ("upper", i => ((KeltnerChannel)i).Upper), ("middle", i => ((KeltnerChannel)i).Middle), ("lower", i => ((KeltnerChannel)i).Lower) },
            P.Int("period", 20), P.Dec("k", 2m, 0.1m, 10m, 0.1m), P.Int("atrPeriod", 10));
        yield return Multi("ind.donchian", "Donchian Channel", "Highest high and lowest low over the period", "Donchian {period}",
            p => new DonchianChannel(p.Int("period", 20), p.Bool("excludeCurrent", false)),
            new (string, Func<IIndicator, decimal>)[] { ("upper", i => ((DonchianChannel)i).Upper), ("middle", i => ((DonchianChannel)i).Middle), ("lower", i => ((DonchianChannel)i).Lower) },
            P.Int("period", 20), P.Bool("excludeCurrent", false, "Exclude the current bar", "Use only completed bars, so the current bar can break the channel (needed for a close-above-upper breakout)."));
        yield return Multi("ind.adx", "ADX", "Average directional index with +DI and −DI", "ADX {period}",
            p => new AverageDirectionalIndex(p.Int("period", 14)),
            new (string, Func<IIndicator, decimal>)[] { ("value", i => ((AverageDirectionalIndex)i).Value), ("plusDi", i => ((AverageDirectionalIndex)i).PlusDi), ("minusDi", i => ((AverageDirectionalIndex)i).MinusDi) },
            P.Int("period", 14));
        yield return Multi("ind.aroon", "Aroon", "Aroon up, down, and oscillator", "Aroon {period}",
            p => new AroonOscillator(p.Int("period", 25)),
            new (string, Func<IIndicator, decimal>)[] { ("up", i => ((AroonOscillator)i).AroonUp), ("down", i => ((AroonOscillator)i).AroonDown), ("value", i => ((AroonOscillator)i).Value) },
            P.Int("period", 25));

        yield return new NodeTypeDescriptor
        {
            Type = "ind.slope",
            Kind = NodeKind.Indicator,
            DisplayName = "Slope",
            Description = "Change of a series over a lookback, per bar.",
            FaceTemplate = "Slope of {value} over {lookback} bars",
            Inputs = [new PortSpec("value", ValueKind.Series, Required: true)],
            Outputs = [new PortSpec("value", ValueKind.Series), new PortSpec("ready", ValueKind.Bool)],
            Params = [P.Int("lookback", 5, 1, 1000), P.Bool("percent", false, "As percent", "Express the slope as a percentage of the older value.")],
            Factory = ctx => new SlopeNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "ind.distance",
            Kind = NodeKind.Indicator,
            DisplayName = "Distance",
            Description = "Difference between two series, absolute or as a percentage of the reference.",
            FaceTemplate = "Distance from {value} to {reference}",
            Inputs = [new PortSpec("value", ValueKind.Series, Required: true), new PortSpec("reference", ValueKind.Series, Required: true)],
            Outputs = [new PortSpec("value", ValueKind.Series)],
            Params =
            [
                P.Enum("unit", "percent", "Unit", "What the distance is reported in.",
                    P.Choice("percent", "Percent of the reference", "The gap between the two series as a percentage of the reference."),
                    P.Choice("absolute", "Absolute", "The gap between the two series in price, as it is.")),
            ],
            Factory = ctx => new DistanceNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "ind.math",
            Kind = NodeKind.Indicator,
            DisplayName = "Arithmetic",
            Description = "One value worked out from another: a plus, a minus, a times or a divide, against a second series or against a constant. This is how a strategy compares against a price it computes itself - a level times 1.03 - rather than one an input already carries.",
            FaceTemplate = "{a} {op} {b}",
            Inputs =
            [
                new PortSpec("a", ValueKind.Series, Required: true),
                new PortSpec("b", ValueKind.Series, Description: "leave unconnected to use the constant"),
            ],
            Outputs = [new PortSpec("value", ValueKind.Series)],
            Params =
            [
                P.Enum("op", "multiply", "Operator", "What is done to a.",
                    P.Choice("add", "Add", "a plus b, or plus the constant while b is unconnected."),
                    P.Choice("subtract", "Subtract", "b taken away from a, or the constant taken away from a."),
                    P.Choice("multiply", "Multiply", "a multiplied by b, or by the constant: a level times 1.03 sits three percent above it."),
                    P.Choice("divide", "Divide", "a divided by b, or by the constant. A divisor of zero leaves the node with nothing to publish for that bar.")),
                P.Dec("value", 1m, null, null, null, "Constant", null, "Stands in for b while b is unconnected. 1 with multiply leaves a as it is."),
            ],
            Factory = ctx => new MathNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "ind.zscore",
            Kind = NodeKind.Indicator,
            DisplayName = "Z-score",
            Description = "How many standard deviations the value sits from its rolling mean.",
            FaceTemplate = "Z-score of {value} over {period}",
            Inputs = [new PortSpec("value", ValueKind.Series, Required: true)],
            Outputs = [new PortSpec("value", ValueKind.Series), new PortSpec("ready", ValueKind.Bool)],
            Params = [P.Int("period", 50, 2, 5000)],
            Factory = ctx => new ZScoreNode(ctx),
        };
    }

    private static PriceType PriceTypeOf(NodeParams p) => p.Str("priceType", "last") switch
    {
        "bid" => PriceType.Bid,
        "ask" => PriceType.Ask,
        "mid" => PriceType.Mid,
        _ => PriceType.Last,
    };

    private static NodeTypeDescriptor Single(string type, string name, string description, string face, Func<NodeParams, IIndicator> create, Func<IIndicator, decimal> value, params ParamSpec[] parameters) =>
        Multi(type, name, description, face, create, new (string, Func<IIndicator, decimal>)[] { ("value", value) }, parameters);

    private static NodeTypeDescriptor Multi(string type, string name, string description, string face, Func<NodeParams, IIndicator> create, (string Port, Func<IIndicator, decimal> Read)[] outputs, params ParamSpec[] parameters)
    {
        List<PortSpec> outs = outputs.Select(o => new PortSpec(o.Port, ValueKind.Series)).ToList();
        outs.Add(new PortSpec("ready", ValueKind.Bool, Description: "true once the indicator has enough input"));
        List<ParamSpec> pars = parameters.ToList();
        pars.Add(PriceTypeParam);
        return new NodeTypeDescriptor
        {
            Type = type,
            Kind = NodeKind.Indicator,
            DisplayName = name,
            Description = description,
            FaceTemplate = face,
            Inputs = [BarsIn],
            Outputs = outs,
            Params = pars,
            Factory = ctx => new EngineIndicatorNode(ctx, create(ctx.Params), outputs),
        };
    }

    /// <summary>Wraps one engine indicator: registered for its bar type so the actor updates it before evaluation.</summary>
    private sealed class EngineIndicatorNode : NodeBase, IIndicatorHost
    {
        private readonly IIndicator _indicator;
        private readonly (string Port, Func<IIndicator, decimal> Read)[] _outputs;

        public EngineIndicatorNode(NodeBuildContext ctx, IIndicator indicator, (string Port, Func<IIndicator, decimal> Read)[] outputs)
            : base(ctx)
        {
            _indicator = indicator;
            _outputs = outputs;
        }

        public IEnumerable<(BarType BarType, IIndicator Indicator)> Indicators
        {
            get
            {
                if (Ctx.SourceBarType is { } bt)
                {
                    yield return (bt, _indicator);
                }
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            ctx.Set("ready", _indicator.IsInitialized);
            if (!_indicator.IsInitialized)
            {
                return;
            }

            foreach ((string port, Func<IIndicator, decimal> read) in _outputs)
            {
                ctx.Set(port, read(_indicator));
            }
        }
    }

    private sealed class SlopeNode : NodeBase
    {
        private readonly History _history;
        private readonly int _lookback;
        private readonly bool _percent;

        public SlopeNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _lookback = P.Int("lookback", 5);
            _percent = P.Bool("percent", false);
            _history = new History(_lookback + 1);
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("value") is not { } v)
            {
                ctx.Set("ready", false);
                return;
            }

            _history.Add(v);
            bool ready = _history.Count > _lookback;
            ctx.Set("ready", ready);
            if (!ready)
            {
                return;
            }

            decimal older = _history.Back(_lookback);
            decimal slope = (v - older) / _lookback;
            ctx.Set("value", _percent && older != 0m ? slope / older * 100m : slope);
        }
    }

    private sealed class DistanceNode : NodeBase
    {
        public DistanceNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("value") is not { } v || ctx.Dec("reference") is not { } r)
            {
                return;
            }

            ctx.Set("value", P.Str("unit", "percent") == "percent" ? (r == 0m ? 0m : (v - r) / r * 100m) : v - r);
        }
    }

    /// <summary>
    /// Arithmetic on two numbers. A connected b that has no value yet publishes nothing, rather than treating the
    /// missing number as zero, because a value computed from a warm-up gap would be wrong and would look right.
    /// </summary>
    private sealed class MathNode : NodeBase
    {
        public MathNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("a") is not { } a)
            {
                return;
            }

            decimal? operand = ctx.IsConnected("b") ? ctx.Dec("b") : P.Dec("value", 1m);
            if (operand is not { } b)
            {
                return;
            }

            string op = P.Str("op", "multiply");
            if (op == "divide" && b == 0m)
            {
                // Nothing is published: a strategy comparing against this value waits, which is what an absent number
                // means everywhere else in the graph. Said on every bar it happens, like any other skip - a node
                // cannot tell a warm-up bar from a live one, and a single report made during warm-up is thrown away.
                ctx.Emit("math", "nothing to divide by: the divisor is zero, so no value is published for this bar");
                return;
            }

            ctx.Set("value", op switch
            {
                "add" => a + b,
                "subtract" => a - b,
                "divide" => a / b,
                _ => a * b,
            });
        }
    }

    private sealed class ZScoreNode : NodeBase
    {
        private readonly History _history;

        public ZScoreNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _history = new History(P.Int("period", 50));
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("value") is not { } v)
            {
                ctx.Set("ready", false);
                return;
            }

            _history.Add(v);
            ctx.Set("ready", _history.IsFull);
            if (!_history.IsFull)
            {
                return;
            }

            decimal sd = _history.StandardDeviation();
            ctx.Set("value", sd == 0m ? 0m : (v - _history.Average) / sd);
        }
    }
}
