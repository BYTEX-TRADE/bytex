using Bytex.Core.Model.Data;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class LevelNodes
{
    private static readonly PortSpec BarsIn = new("bars", ValueKind.Bars, Required: true, Label: "bars");

    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "level.range",
            Kind = NodeKind.Level,
            DisplayName = "Range",
            Description = "Highest high and lowest low of the last N completed bars.",
            FaceTemplate = "Range high and low of the last {lookback} bars",
            Inputs = [BarsIn],
            Outputs = [new PortSpec("high", ValueKind.Price), new PortSpec("low", ValueKind.Price), new PortSpec("mid", ValueKind.Price), new PortSpec("width", ValueKind.Series), new PortSpec("ready", ValueKind.Bool)],
            Params = [P.Int("lookback", 20, 2, 5000), P.Bool("excludeCurrent", true, "Exclude the current bar", "Use only completed bars, so the current bar can break the range.")],
            Factory = ctx => new RangeNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.swing",
            Kind = NodeKind.Level,
            DisplayName = "Swing points",
            Description = "The last confirmed pivot high and pivot low (a bar higher or lower than its neighbours on both sides).",
            FaceTemplate = "Swing high and low, strength {strength}",
            Inputs = [BarsIn],
            Outputs = [new PortSpec("high", ValueKind.Price), new PortSpec("low", ValueKind.Price), new PortSpec("newHigh", ValueKind.Pulse), new PortSpec("newLow", ValueKind.Pulse)],
            Params = [P.Int("strength", 3, 1, 50, "Strength", null, "Bars on each side that must be lower (for a high) or higher (for a low).")],
            Factory = ctx => new SwingNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.session",
            Kind = NodeKind.Level,
            DisplayName = "Session levels",
            Description = "Open, high, and low since the session boundary (a UTC time of day).",
            FaceTemplate = "Session open, high and low since {boundary} UTC",
            Inputs = [BarsIn],
            Outputs = [new PortSpec("open", ValueKind.Price), new PortSpec("high", ValueKind.Price), new PortSpec("low", ValueKind.Price), new PortSpec("barsInSession", ValueKind.Series), new PortSpec("sessionStart", ValueKind.Pulse)],
            Params = [P.Time("boundary", "00:00", "Session boundary")],
            Factory = ctx => new SessionNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.vwapBands",
            Kind = NodeKind.Level,
            DisplayName = "VWAP bands",
            Description = "Daily VWAP with bands at k standard deviations of price around it.",
            FaceTemplate = "VWAP ± {k} σ",
            Inputs = [BarsIn],
            Outputs = [new PortSpec("vwap", ValueKind.Price), new PortSpec("upper", ValueKind.Price), new PortSpec("lower", ValueKind.Price), new PortSpec("ready", ValueKind.Bool)],
            Params = [P.Dec("k", 1m, 0.1m, 5m, 0.1m), P.Int("period", 50, 2, 5000, "Deviation window")],
            Factory = ctx => new VwapBandsNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.fib",
            Kind = NodeKind.Level,
            DisplayName = "Fibonacci",
            Description = "Retracement levels between a high and a low.",
            FaceTemplate = "Fibonacci retracements of {high} to {low}",
            Inputs = [new PortSpec("high", ValueKind.Price, Required: true), new PortSpec("low", ValueKind.Price, Required: true)],
            Outputs = [new PortSpec("r236", ValueKind.Price), new PortSpec("r382", ValueKind.Price), new PortSpec("r500", ValueKind.Price), new PortSpec("r618", ValueKind.Price), new PortSpec("r786", ValueKind.Price)],
            Params =
            [
                P.Enum("direction", "up", "Direction", "Which way the move being retraced went.",
                    P.Choice("up", "Retracing a rise", "The move went up, so the levels are measured down from the high."),
                    P.Choice("down", "Retracing a fall", "The move went down, so the levels are measured up from the low.")),
            ],
            Factory = ctx => new FibNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.pinned",
            Kind = NodeKind.Level,
            DisplayName = "Pinned price",
            Description = "A price the user set by hand, on the chart or in the inspector.",
            FaceTemplate = "Price {price}",
            Outputs = [new PortSpec("price", ValueKind.Price)],
            Params = [P.Dec("price", 0m, 0m, null, null, "Price", null, "Set by dragging on the chart.")],
            Factory = ctx => new PinnedNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.prevBar",
            Kind = NodeKind.Level,
            DisplayName = "Previous bar",
            Description = "Open, high, low, and close of the bar before the current one.",
            FaceTemplate = "Previous bar",
            Inputs = [BarsIn],
            Outputs = [new PortSpec("open", ValueKind.Price), new PortSpec("high", ValueKind.Price), new PortSpec("low", ValueKind.Price), new PortSpec("close", ValueKind.Price), new PortSpec("ready", ValueKind.Bool)],
            Params = [P.Int("back", 1, 1, 500, "Bars back")],
            Factory = ctx => new PrevBarNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.channel",
            Kind = NodeKind.Level,
            DisplayName = "Regression channel",
            Description = "Linear regression of closes over the lookback with bands at k standard deviations.",
            FaceTemplate = "Regression channel over {lookback} bars ± {k} σ",
            Inputs = [BarsIn],
            Outputs = [new PortSpec("mid", ValueKind.Price), new PortSpec("upper", ValueKind.Price), new PortSpec("lower", ValueKind.Price), new PortSpec("slope", ValueKind.Series), new PortSpec("ready", ValueKind.Bool)],
            Params = [P.Int("lookback", 50, 3, 5000), P.Dec("k", 2m, 0.1m, 10m, 0.1m)],
            Factory = ctx => new ChannelNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "level.round",
            Kind = NodeKind.Level,
            DisplayName = "Round numbers",
            Description = "The nearest round price above and below the input, at the given step.",
            FaceTemplate = "Round numbers every {step} around {price}",
            Inputs = [new PortSpec("price", ValueKind.Price, Required: true)],
            Outputs = [new PortSpec("above", ValueKind.Price), new PortSpec("below", ValueKind.Price)],
            Params = [P.Dec("step", 1000m, 0.00000001m, null, null, "Step")],
            Factory = ctx => new RoundNode(ctx),
        };
    }

    private sealed class RangeNode : NodeBase, IBarObserver
    {
        private readonly History _highs;
        private readonly History _lows;
        private readonly bool _excludeCurrent;
        private readonly int _lookback;
        private Bar? _current;

        public RangeNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _lookback = P.Int("lookback", 20);
            _excludeCurrent = P.Bool("excludeCurrent", true);
            _highs = new History(_lookback + 1);
            _lows = new History(_lookback + 1);
        }

        public void OnBar(Bar bar)
        {
            _current = bar;
            _highs.Add(bar.High.Value);
            _lows.Add(bar.Low.Value);
        }

        public override void Evaluate(EvalContext ctx)
        {
            int needed = _excludeCurrent ? _lookback + 1 : _lookback;
            bool ready = _highs.Count >= needed;
            ctx.Set("ready", ready);
            if (!ready)
            {
                return;
            }

            decimal high = decimal.MinValue, low = decimal.MaxValue;
            int last = _excludeCurrent ? _highs.Count - 2 : _highs.Count - 1;
            for (int i = last; i > last - _lookback; i--)
            {
                high = Math.Max(high, _highs[i]);
                low = Math.Min(low, _lows[i]);
            }

            ctx.Set("high", high);
            ctx.Set("low", low);
            ctx.Set("mid", (high + low) / 2m);
            ctx.Set("width", high - low);
        }
    }

    private sealed class SwingNode : NodeBase, IBarObserver
    {
        private readonly int _strength;
        private readonly List<Bar> _bars = new();
        private decimal? _high;
        private decimal? _low;
        private bool _newHigh;
        private bool _newLow;

        public SwingNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _strength = P.Int("strength", 3);
        }

        public void OnBar(Bar bar)
        {
            _bars.Add(bar);
            int window = _strength * 2 + 1;
            if (_bars.Count > window + 2)
            {
                _bars.RemoveAt(0);
            }

            if (_bars.Count < window)
            {
                return;
            }

            int pivot = _bars.Count - 1 - _strength;
            Bar candidate = _bars[pivot];
            bool isHigh = true, isLow = true;
            for (int i = pivot - _strength; i <= pivot + _strength; i++)
            {
                if (i == pivot)
                {
                    continue;
                }

                if (_bars[i].High.Value >= candidate.High.Value)
                {
                    isHigh = false;
                }

                if (_bars[i].Low.Value <= candidate.Low.Value)
                {
                    isLow = false;
                }
            }

            if (isHigh)
            {
                _high = candidate.High.Value;
                _newHigh = true;
            }

            if (isLow)
            {
                _low = candidate.Low.Value;
                _newLow = true;
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (_high is { } h)
            {
                ctx.Set("high", h);
            }

            if (_low is { } l)
            {
                ctx.Set("low", l);
            }

            ctx.Set("newHigh", _newHigh);
            ctx.Set("newLow", _newLow);
            _newHigh = false;
            _newLow = false;
        }
    }

    private sealed class SessionNode : NodeBase, IBarObserver
    {
        private readonly TimeOnly _boundary;
        private DateOnly? _sessionDay;
        private decimal _open, _high, _low;
        private int _bars;
        private bool _started;

        public SessionNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _boundary = P.Time("boundary", new TimeOnly(0, 0));
        }

        public void OnBar(Bar bar)
        {
            DateTimeOffset t = BarMath.Time(bar);
            DateTimeOffset shifted = t - _boundary.ToTimeSpan();
            DateOnly day = DateOnly.FromDateTime(shifted.UtcDateTime);
            if (_sessionDay != day)
            {
                _sessionDay = day;
                _open = bar.Open.Value;
                _high = bar.High.Value;
                _low = bar.Low.Value;
                _bars = 0;
                _started = true;
            }
            else
            {
                _high = Math.Max(_high, bar.High.Value);
                _low = Math.Min(_low, bar.Low.Value);
            }

            _bars++;
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (_sessionDay is null)
            {
                return;
            }

            ctx.Set("open", _open);
            ctx.Set("high", _high);
            ctx.Set("low", _low);
            ctx.Set("barsInSession", (decimal)_bars);
            ctx.Set("sessionStart", _started);
            _started = false;
        }
    }

    private sealed class VwapBandsNode : NodeBase, IBarObserver
    {
        private readonly decimal _k;
        private readonly History _deviations;
        private DateOnly? _day;
        private decimal _pv;
        private decimal _volume;

        public VwapBandsNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _k = P.Dec("k", 1m);
            _deviations = new History(P.Int("period", 50));
        }

        public void OnBar(Bar bar)
        {
            DateOnly day = DateOnly.FromDateTime(BarMath.Time(bar).UtcDateTime);
            if (_day != day)
            {
                _day = day;
                _pv = 0m;
                _volume = 0m;
            }

            decimal typical = BarMath.Typical(bar);
            _pv += typical * bar.Volume.Value;
            _volume += bar.Volume.Value;
            if (_volume > 0m)
            {
                _deviations.Add(typical - _pv / _volume);
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool ready = _volume > 0m && _deviations.Count >= 2;
            ctx.Set("ready", ready);
            if (!ready)
            {
                return;
            }

            decimal vwap = _pv / _volume;
            decimal sd = _deviations.StandardDeviation();
            ctx.Set("vwap", vwap);
            ctx.Set("upper", vwap + _k * sd);
            ctx.Set("lower", vwap - _k * sd);
        }
    }

    private sealed class FibNode : NodeBase
    {
        public FibNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("high") is not { } high || ctx.Dec("low") is not { } low)
            {
                return;
            }

            bool up = P.Str("direction", "up") == "up";
            decimal range = high - low;
            foreach ((string port, decimal ratio) in new[] { ("r236", 0.236m), ("r382", 0.382m), ("r500", 0.5m), ("r618", 0.618m), ("r786", 0.786m) })
            {
                ctx.Set(port, up ? high - range * ratio : low + range * ratio);
            }
        }
    }

    private sealed class PinnedNode : NodeBase
    {
        public PinnedNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            decimal price = P.Dec("price", 0m);
            if (price > 0m)
            {
                ctx.Set("price", price);
            }
        }
    }

    private sealed class PrevBarNode : NodeBase, IBarObserver
    {
        private readonly int _back;
        private readonly List<Bar> _bars = new();

        public PrevBarNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _back = P.Int("back", 1);
        }

        public void OnBar(Bar bar)
        {
            _bars.Add(bar);
            if (_bars.Count > _back + 1)
            {
                _bars.RemoveAt(0);
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool ready = _bars.Count > _back;
            ctx.Set("ready", ready);
            if (!ready)
            {
                return;
            }

            Bar b = _bars[_bars.Count - 1 - _back];
            ctx.Set("open", b.Open.Value);
            ctx.Set("high", b.High.Value);
            ctx.Set("low", b.Low.Value);
            ctx.Set("close", b.Close.Value);
        }
    }

    private sealed class ChannelNode : NodeBase, IBarObserver
    {
        private readonly int _lookback;
        private readonly decimal _k;
        private readonly History _closes;

        public ChannelNode(NodeBuildContext ctx)
            : base(ctx)
        {
            _lookback = P.Int("lookback", 50);
            _k = P.Dec("k", 2m);
            _closes = new History(_lookback);
        }

        public void OnBar(Bar bar) => _closes.Add(bar.Close.Value);

        public override void Evaluate(EvalContext ctx)
        {
            bool ready = _closes.IsFull;
            ctx.Set("ready", ready);
            if (!ready)
            {
                return;
            }

            int n = _closes.Count;
            decimal sumX = 0m, sumY = 0m, sumXY = 0m, sumXX = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal x = i, y = _closes[i];
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumXX += x * x;
            }

            decimal denominator = n * sumXX - sumX * sumX;
            decimal slope = denominator == 0m ? 0m : (n * sumXY - sumX * sumY) / denominator;
            decimal intercept = (sumY - slope * sumX) / n;
            decimal mid = intercept + slope * (n - 1);
            decimal sumSq = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal residual = _closes[i] - (intercept + slope * i);
                sumSq += residual * residual;
            }

            decimal sd = DecimalMathSqrt(sumSq / n);
            ctx.Set("mid", mid);
            ctx.Set("upper", mid + _k * sd);
            ctx.Set("lower", mid - _k * sd);
            ctx.Set("slope", slope);
        }

        private static decimal DecimalMathSqrt(decimal value) => Bytex.Indicators.DecimalMath.Sqrt(value);
    }

    private sealed class RoundNode : NodeBase
    {
        public RoundNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Dec("price") is not { } price)
            {
                return;
            }

            decimal step = P.Dec("step", 1000m);
            if (step <= 0m)
            {
                return;
            }

            decimal below = Math.Floor(price / step) * step;
            decimal above = below == price ? price + step : below + step;
            ctx.Set("above", above);
            ctx.Set("below", below);
        }
    }
}
