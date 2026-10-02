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
        yield return Single("ind.cci", "CCI", "Commodity channel index: distance from the mean in mean deviations", "CCI {period}",
            p => new CommodityChannelIndex(p.Int("period", 20)), i => ((CommodityChannelIndex)i).Value, P.Int("period", 20));
        yield return Single("ind.mfi", "MFI", "Money flow index, 0 to 100: price weighted by volume", "MFI {period}",
            p => new MoneyFlowIndex(p.Int("period", 14)), i => ((MoneyFlowIndex)i).Value, P.Int("period", 14));
        yield return Single("ind.cmo", "CMO", "Chande momentum oscillator, -100 to 100: the share of movement that went one way", "CMO {period}",
            p => new ChandeMomentumOscillator(p.Int("period", 14), PriceTypeOf(p)), i => ((ChandeMomentumOscillator)i).Value, P.Int("period", 14));

        yield return Multi("ind.linreg", "Linear regression", "The least-squares line through the last bars: where it ends, its slope, and how well it fits", "Regression over {period} bars",
            p => new LinearRegression(p.Int("period", 20), PriceTypeOf(p)),
            new (string, Func<IIndicator, decimal>)[] { ("value", i => ((LinearRegression)i).Value), ("slope", i => ((LinearRegression)i).Slope), ("rSquared", i => ((LinearRegression)i).RSquared) },
            P.Int("period", 20));
        yield return Multi("ind.supertrend", "Supertrend", "An ATR band that follows price and only tightens, until price closes through it", "Supertrend {period} × {multiplier}",
            p => new Supertrend(p.Int("period", 10), p.Dec("multiplier", 3m)),
            new (string, Func<IIndicator, decimal>)[] { ("value", i => ((Supertrend)i).Value), ("direction", i => ((Supertrend)i).Direction), ("upper", i => ((Supertrend)i).Upper), ("lower", i => ((Supertrend)i).Lower) },
            P.Int("period", 10), P.Dec("multiplier", 3m, 0.1m, 20m, 0.1m));
        yield return Multi("ind.psar", "Parabolic SAR", "A stop that accelerates towards price while the trend keeps making extremes", "Parabolic SAR {step}/{maximum}",
            p => new ParabolicSar(p.Dec("step", 0.02m), p.Dec("maximum", 0.2m)),
            new (string, Func<IIndicator, decimal>)[] { ("value", i => ((ParabolicSar)i).Value), ("direction", i => ((ParabolicSar)i).Direction) },
            P.Dec("step", 0.02m, 0.001m, 1m, 0.001m), P.Dec("maximum", 0.2m, 0.01m, 1m, 0.01m));
        yield return Multi("ind.ichimoku", "Ichimoku", "The Ichimoku lines, and the cloud as it applies to this bar rather than the one just computed", "Ichimoku {conversion}/{base}/{spanB}",
            p => new Ichimoku(p.Int("conversion", 9), p.Int("base", 26), p.Int("spanB", 52)),
            new (string, Func<IIndicator, decimal>)[]
            {
                ("conversion", i => ((Ichimoku)i).ConversionLine),
                ("base", i => ((Ichimoku)i).BaseLine),
                ("cloudTop", i => ((Ichimoku)i).CloudTop),
                ("cloudBottom", i => ((Ichimoku)i).CloudBottom),
                ("spanAAhead", i => ((Ichimoku)i).SpanAAhead),
                ("spanBAhead", i => ((Ichimoku)i).SpanBAhead),
                ("chikouReference", i => ((Ichimoku)i).ChikouReference),
            },
            P.Int("conversion", 9), P.Int("base", 26), P.Int("spanB", 52));

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
            Outputs = [Declared("ind.slope", "value"), new PortSpec("ready", ValueKind.Bool)],
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
            Outputs = [Declared("ind.distance", "value")],
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
            Outputs = [Declared("ind.math", "value")],
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
            Outputs = [Declared("ind.zscore", "value"), new PortSpec("ready", ValueKind.Bool)],
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

    /// <summary>
    /// What each indicator output MEANS, keyed <c>type:port</c>.
    ///
    /// <para>
    /// Declared here so that anything drawing an indicator - a chart deciding whether a line belongs on the price
    /// axis or in a pane of its own - does not keep a list of node types somewhere else. Such a list goes stale the
    /// moment an indicator is added and nobody notices until a line is drawn on the wrong scale. A test walks every
    /// indicator in the catalog and fails if an output is missing from this table, so adding one without saying
    /// what it means is not possible quietly.
    /// </para>
    ///
    /// <para>
    /// <b>Price</b> is a level on the instrument's own scale and overlays the candles. <b>Percent</b> carries its
    /// range when it has one. <b>Ratio</b> is a multiple or a flag. <b>Unbounded</b> is a difference, a cumulative
    /// total or an oscillator about zero - no natural scale, so its own pane. ATR is unbounded rather than a price
    /// on purpose: it is a distance, and drawing it among the candles would put it near zero.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (PortUnit Unit, decimal? Min, decimal? Max)> _outputUnits = new(StringComparer.Ordinal)
    {
        ["ind.sma:value"] = (PortUnit.Price, null, null),
        ["ind.ema:value"] = (PortUnit.Price, null, null),
        ["ind.wma:value"] = (PortUnit.Price, null, null),
        ["ind.dema:value"] = (PortUnit.Price, null, null),
        ["ind.hma:value"] = (PortUnit.Price, null, null),
        ["ind.vwap:value"] = (PortUnit.Price, null, null),
        ["ind.rsi:value"] = (PortUnit.Percent, 0m, 100m),
        ["ind.roc:value"] = (PortUnit.Percent, null, null),
        ["ind.obv:value"] = (PortUnit.Unbounded, null, null),
        ["ind.atr:value"] = (PortUnit.Unbounded, null, null),
        ["ind.cci:value"] = (PortUnit.Unbounded, null, null),
        ["ind.mfi:value"] = (PortUnit.Percent, 0m, 100m),
        ["ind.cmo:value"] = (PortUnit.Percent, -100m, 100m),
        ["ind.macd:value"] = (PortUnit.Unbounded, null, null),
        ["ind.macd:signal"] = (PortUnit.Unbounded, null, null),
        ["ind.macd:histogram"] = (PortUnit.Unbounded, null, null),
        ["ind.stoch:k"] = (PortUnit.Percent, 0m, 100m),
        ["ind.stoch:d"] = (PortUnit.Percent, 0m, 100m),
        ["ind.bbands:upper"] = (PortUnit.Price, null, null),
        ["ind.bbands:middle"] = (PortUnit.Price, null, null),
        ["ind.bbands:lower"] = (PortUnit.Price, null, null),
        ["ind.bbands:width"] = (PortUnit.Ratio, null, null),
        ["ind.keltner:upper"] = (PortUnit.Price, null, null),
        ["ind.keltner:middle"] = (PortUnit.Price, null, null),
        ["ind.keltner:lower"] = (PortUnit.Price, null, null),
        ["ind.donchian:upper"] = (PortUnit.Price, null, null),
        ["ind.donchian:middle"] = (PortUnit.Price, null, null),
        ["ind.donchian:lower"] = (PortUnit.Price, null, null),
        ["ind.adx:value"] = (PortUnit.Percent, 0m, 100m),
        ["ind.adx:plusDi"] = (PortUnit.Percent, 0m, 100m),
        ["ind.adx:minusDi"] = (PortUnit.Percent, 0m, 100m),
        ["ind.linreg:value"] = (PortUnit.Price, null, null),
        ["ind.linreg:slope"] = (PortUnit.Unbounded, null, null),
        ["ind.linreg:rSquared"] = (PortUnit.Ratio, 0m, 1m),
        ["ind.supertrend:value"] = (PortUnit.Price, null, null),
        ["ind.supertrend:direction"] = (PortUnit.Ratio, -1m, 1m),
        ["ind.supertrend:upper"] = (PortUnit.Price, null, null),
        ["ind.supertrend:lower"] = (PortUnit.Price, null, null),
        ["ind.psar:value"] = (PortUnit.Price, null, null),
        ["ind.psar:direction"] = (PortUnit.Ratio, -1m, 1m),
        ["ind.ichimoku:conversion"] = (PortUnit.Price, null, null),
        ["ind.ichimoku:base"] = (PortUnit.Price, null, null),
        ["ind.ichimoku:cloudTop"] = (PortUnit.Price, null, null),
        ["ind.ichimoku:cloudBottom"] = (PortUnit.Price, null, null),
        ["ind.ichimoku:spanAAhead"] = (PortUnit.Price, null, null),
        ["ind.ichimoku:spanBAhead"] = (PortUnit.Price, null, null),
        ["ind.ichimoku:chikouReference"] = (PortUnit.Price, null, null),
        ["ind.aroon:up"] = (PortUnit.Percent, 0m, 100m),
        ["ind.aroon:down"] = (PortUnit.Percent, 0m, 100m),
        ["ind.aroon:value"] = (PortUnit.Percent, -100m, 100m),
        ["ind.slope:value"] = (PortUnit.Unbounded, null, null),
        ["ind.distance:value"] = (PortUnit.Unbounded, null, null),
        
        // Whatever the expression makes it. Declared Unbounded rather than left unsaid, because "no scale stated"
        // and "a scale nobody got round to declaring" must not look the same to a reader.
        ["ind.math:value"] = (PortUnit.Unbounded, null, null),
        ["ind.zscore:value"] = (PortUnit.Unbounded, null, null),
    };

    /// <summary>One output whose unit comes from the same table the generated indicators use.</summary>
    private static PortSpec Declared(string type, string port)
    {
        (PortUnit unit, decimal? min, decimal? max) = _outputUnits.GetValueOrDefault(type + ":" + port);
        return new PortSpec(port, ValueKind.Series, Unit: unit, Min: min, Max: max);
    }

    private static NodeTypeDescriptor Multi(string type, string name, string description, string face, Func<NodeParams, IIndicator> create, (string Port, Func<IIndicator, decimal> Read)[] outputs, params ParamSpec[] parameters)
    {
        List<PortSpec> outs = outputs
            .Select(o =>
            {
                (PortUnit unit, decimal? min, decimal? max) = _outputUnits.GetValueOrDefault(type + ":" + o.Port);
                return new PortSpec(o.Port, ValueKind.Series, Unit: unit, Min: min, Max: max);
            })
            .ToList();
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

    /// <summary>Wraps one engine indicator: registered for its bar type so the runtimeModule updates it before evaluation.</summary>
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

        public IEnumerable<(CandleSeries CandleSeries, IIndicator Indicator)> Indicators
        {
            get
            {
                if (Ctx.SourceCandleSeries is { } bt)
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
