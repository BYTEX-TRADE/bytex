using System.Text.Json;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class FlowNodes
{
    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "flow.timer",
            Kind = NodeKind.Flow,
            DisplayName = "Every N bars",
            Description = "Pulses every N bars, counted from when its phase became active.",
            FaceTemplate = "Every {everyBars} bars",
            Outputs = [new PortSpec("tick", ValueKind.Pulse), new PortSpec("count", ValueKind.Series)],
            Params = [P.Int("everyBars", 10, 1, 100_000), P.Bool("fireImmediately", false, "Fire on the first bar")],
            Factory = ctx => new TimerNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "flow.counter",
            Kind = NodeKind.Flow,
            DisplayName = "Counter",
            Description = "Counts pulses; true once the target is reached.",
            FaceTemplate = "Count {increment} up to {target}",
            Inputs = [new PortSpec("increment", ValueKind.Bool, Required: true), new PortSpec("reset", ValueKind.Pulse)],
            Outputs = [new PortSpec("count", ValueKind.Series), new PortSpec("reached", ValueKind.Bool)],
            Params = [P.Int("target", 3, 1, 1_000_000), P.Bool("resetOnPhaseEntry", true, "Reset when the phase is entered")],
            Factory = ctx => new CounterNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "flow.latch",
            Kind = NodeKind.Flow,
            DisplayName = "Latch",
            Description = "Turns on with set and stays on until reset.",
            FaceTemplate = "Latch: set by {set}, reset by {reset}",
            Inputs = [new PortSpec("set", ValueKind.Bool, Required: true), new PortSpec("reset", ValueKind.Bool)],
            Outputs = [new PortSpec("out", ValueKind.Bool)],
            Params = [P.Bool("resetOnPhaseEntry", true, "Reset when the phase is entered")],
            Factory = ctx => new LatchNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "flow.completeRun",
            Kind = NodeKind.Flow,
            DisplayName = "Complete run",
            Description = "Ends a fired-once strategy: the runner stops it with reason 'finished'.",
            FaceTemplate = "Complete the run when {trigger}",
            Inputs = [new PortSpec("trigger", ValueKind.Bool, Required: true)],
            Outputs = [new PortSpec("done", ValueKind.Pulse)],
            Factory = ctx => new CompleteRunNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "flow.gate",
            Kind = NodeKind.Flow,
            DisplayName = "Gate",
            Description = "Passes the input only while enabled.",
            FaceTemplate = "{in} while {enable}",
            Inputs = [new PortSpec("in", ValueKind.Bool, Required: true), new PortSpec("enable", ValueKind.Bool, Required: true)],
            Outputs = [new PortSpec("out", ValueKind.Bool)],
            Factory = ctx => new GateNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "flow.delay",
            Kind = NodeKind.Flow,
            DisplayName = "Delay",
            Description = "Re-emits a pulse N bars later.",
            FaceTemplate = "{in}, delayed {bars} bars",
            Inputs = [new PortSpec("in", ValueKind.Pulse, Required: true)],
            Outputs = [new PortSpec("out", ValueKind.Pulse)],
            Params = [P.Int("bars", 1, 1, 100_000)],
            Factory = ctx => new DelayNode(ctx),
        };
    }

    private sealed class TimerNode : NodeBase, IPhaseAware
    {
        private long _count;

        public TimerNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPhaseEntered() => _count = 0;

        public void OnPhaseExited()
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            int every = P.Int("everyBars", 10);
            bool immediate = P.Bool("fireImmediately", false);
            bool tick = immediate ? _count % every == 0 : _count > 0 && _count % every == 0;
            _count++;
            ctx.Set("tick", tick);
            ctx.Set("count", (decimal)_count);
        }
    }

    private sealed class CounterNode : NodeBase, IPhaseAware, IStatefulNode
    {
        private readonly Edge _edge = new();
        private long _count;

        public CounterNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPhaseEntered()
        {
            if (P.Bool("resetOnPhaseEntry", true))
            {
                _count = 0;
                _edge.Reset();
            }
        }

        public void OnPhaseExited()
        {
        }

        public JsonElement SaveState() => JsonSerializer.SerializeToElement(new { count = _count });

        public void LoadState(JsonElement state) => _count = state.TryGetProperty("count", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Bool("reset"))
            {
                _count = 0;
            }

            if (_edge.Rising(ctx.Bool("increment")))
            {
                _count++;
            }

            ctx.Set("count", (decimal)_count);
            ctx.Set("reached", _count >= P.Int("target", 3));
        }
    }

    private sealed class LatchNode : NodeBase, IPhaseAware, IStatefulNode
    {
        private bool _on;

        public LatchNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPhaseEntered()
        {
            if (P.Bool("resetOnPhaseEntry", true))
            {
                _on = false;
            }
        }

        public void OnPhaseExited()
        {
        }

        public JsonElement SaveState() => JsonSerializer.SerializeToElement(new { on = _on });

        public void LoadState(JsonElement state) => _on = state.TryGetProperty("on", out JsonElement o) && o.ValueKind == JsonValueKind.True;

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Bool("reset"))
            {
                _on = false;
            }
            else if (ctx.Bool("set"))
            {
                _on = true;
            }

            ctx.Set("out", _on);
        }
    }

    private sealed class CompleteRunNode : NodeBase
    {
        private readonly Edge _edge = new();

        public CompleteRunNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire)
            {
                ctx.Emit("complete", "run complete");
                Services.RequestStop("finished");
            }

            ctx.Set("done", fire);
        }
    }

    private sealed class GateNode : NodeBase
    {
        public GateNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx) => ctx.Set("out", ctx.Bool("in") && ctx.Bool("enable"));
    }

    private sealed class DelayNode : NodeBase
    {
        private readonly Queue<long> _due = new();

        public DelayNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (ctx.Bool("in"))
            {
                _due.Enqueue(ctx.Index + P.Int("bars", 1));
            }

            bool fire = false;
            while (_due.Count > 0 && _due.Peek() <= ctx.Index)
            {
                _due.Dequeue();
                fire = true;
            }

            ctx.Set("out", fire);
        }
    }
}
