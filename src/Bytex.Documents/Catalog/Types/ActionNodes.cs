using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class ActionNodes
{
    private static readonly PortSpec Trigger = new("trigger", ValueKind.Bool, Required: true, Label: "when");

    internal static readonly ParamSpec SizingParam = P.Obj("sizing", true, "Size",
        "How much to trade. The mode decides what the number beside it means, and the four modes mean four different things, so read the mode before reading the number.",
        P.Enum("mode", "fixed", "Mode", "What the size is counted in.",
            P.Choice("fixed", "Fixed amount", "The number is the quantity itself, in the instrument's base units: 0.5 on BTCUSDT is half a bitcoin, whatever the account holds.", "base units", "value", min: "0"),
            P.Choice("notional", "Amount to spend", "The number is what the position should be worth in the quote currency: 500 on BTCUSDT buys 500 USDT worth at the price it goes in at.", "quote currency", "value", min: "0"),
            P.Choice("percentOfBalance", "Percent of the balance to spend", "The number is the share of the free quote balance to put into the position: 10 spends a tenth of it and leaves the rest untouched.", "% of the free balance", "value", min: "0", max: "100", step: "1"),
            P.Choice("riskPercent", "Percent of the balance to risk on the stop", "The number is what a stop-out may cost, not what to spend: the quantity is chosen so that the distance from the entry to the stop input works out to this share of the balance. The nearer the stop, the larger the position it buys \u2014 1 with a stop one percent away stakes the whole balance \u2014 so a ceiling is the only thing that bounds it: set maxPercentOfBalance or maxNotional beside it. Without one, the only bound is what the free balance can pay for at 1x. Needs the stop input; without it nothing is sent.", "% of the balance lost if the stop is hit", "value", min: "0", max: "100", step: "0.1")),
        P.Dec("value", 1m, 0m, null, null, "Value"),
        P.Dec("maxNotional", 0m, 0m, null, null, "Never more than", "quote currency",
            "A ceiling on the order whatever the mode works out: the most the position may be worth, in the quote currency. 0 is no ceiling."),
        P.Dec("maxPercentOfBalance", 0m, 0m, 100m, 0.1m, "Never more than", "% of the free balance",
            "A ceiling on the order whatever the mode works out, as a share of the free quote balance. 0 is no ceiling. This is what keeps riskPercent with a near stop from staking the whole account: set both and the smaller one holds."));

    internal static readonly ParamSpec OffsetParam = P.Obj("offset", false, "Price offset", "Moves the order away from the price input. The unit decides what the number beside it counts.",
        P.Enum("unit", "ticks", "Unit", "What the offset is counted in.",
            P.Choice("ticks", "Ticks", "Whole price steps of the instrument: 5 on an instrument quoted in steps of 0.1 moves the order 0.5 of price.", "ticks", "value"),
            P.Choice("percent", "Percent of the price", "A share of the price input: 0.5 puts the order half a percent away from it.", "% of the price", "value", step: "0.1"),
            P.Choice("atr", "ATR multiples", "Multiples of the atr input, so the offset widens as the market gets noisier. Needs the atr input; without it there is no offset.", "\u00d7 ATR", "value", step: "0.1"),
            P.Choice("price", "Price amount", "The offset in the quote currency itself: 25 on BTCUSDT is 25 USDT away from the price input.", "price", "value")),
        P.Dec("value", 0m, null, null, null, "Value"));

    private static readonly ParamSpec TifParam = P.Enum("tif", "gtc", "Time in force", "How long the order stays alive at the venue.",
        P.Choice("gtc", "Good till cancelled", "Rests at the venue until it fills or something cancels it."),
        P.Choice("ioc", "Immediate or cancel", "Whatever can fill at once fills; the rest is cancelled."),
        P.Choice("fok", "Fill or kill", "Fills in full immediately, or is cancelled untouched."),
        P.Choice("day", "Day order", "Cancelled by the venue at the end of its trading day if it has not filled. A market order ignores this and is sent good till cancelled."));

    internal static readonly ParamSpec WorkParam = P.Obj(OrderWork.Param, false, "Work the order",
        "How the order reaches the market. Left alone it goes to the venue in one piece, which is what every order has always done; worked, it is cut into smaller orders that go out on a pace, so a size the book cannot take at once is not paid for in slippage.",
        P.Enum(OrderWork.Algorithm, OrderWork.None, "Algorithm", "What works the order.",
            P.Choice(OrderWork.None, "Send it in one piece", "The order goes to the venue as it is, at once."),
            P.Choice(OrderWork.Twap, "Time-weighted average price (TWAP)",
                "The order is cut into equal pieces that go out at an even pace - one every interval, until the horizon is up - so what it pays is the average of the prices over that stretch rather than whatever the book held at one moment. The node still reports one order: it is working until the last piece is done, and the fill price it publishes is what the pieces averaged. Only a market or a limit order can be worked this way, and the whole size is worked whether or not the market moves away, so the horizon is a promise to be in by the end of it.")),
        P.Dec(OrderWork.HorizonMinutes, OrderWork.DefaultHorizonMinutes, OrderWork.MinMinutes, OrderWork.MaxMinutes, OrderWork.StepMinutes,
            "Over", "minutes", "How long the whole order is worked over."),
        P.Dec(OrderWork.IntervalMinutes, OrderWork.DefaultIntervalMinutes, OrderWork.MinMinutes, OrderWork.MaxMinutes, OrderWork.StepMinutes,
            "A piece every", "minutes", "How often a piece goes out: the horizon divided by this is how many pieces there are, so 5 minutes every 1 is five of them. A piece the venue would refuse as too small is not sent on its own - the order is then cut into as many pieces as it can be cut into, and no more."));

    private static readonly ParamSpec SideParam = P.Enum("side", "buy", "Side", "Which way the order goes.",
        P.Choice("buy", "Buy", "Buys the base asset: opens a long, adds to one, or reduces a short that is already open."),
        P.Choice("sell", "Sell", "Sells the base asset: opens a short where the venue allows one, adds to it, or reduces a long that is already open."));

    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "act.order",
            Kind = NodeKind.Action,
            DisplayName = "Place order",
            Description = "Submits one order when the trigger turns true. Tracks it until it fills, cancels, or times out.",
            FaceTemplate = "{side} {orderType}, size {sizing}",
            Inputs =
            [
                Trigger,
                new PortSpec("price", ValueKind.Price, Description: "limit or trigger price for non-market orders; defaults to the close"),
                new PortSpec("stop", ValueKind.Price, Description: "stop price used by riskPercent sizing"),
                new PortSpec("quantity", ValueKind.Quantity, Description: "overrides sizing when connected"),
                new PortSpec("atr", ValueKind.Series, Description: "for atr offsets"),
            ],
            Outputs =
            [
                new PortSpec("submitted", ValueKind.Pulse), new PortSpec("filled", ValueKind.Pulse), new PortSpec("rejected", ValueKind.Pulse),
                new PortSpec("position", ValueKind.Position), new PortSpec("fillPrice", ValueKind.Price), new PortSpec("working", ValueKind.Bool),
            ],
            Params =
            [
                SideParam,
                P.Enum("orderType", "market", "Order type", "What is sent when the trigger turns true.",
                    P.Choice("market", "Market", "Takes the best price on offer at once. It fills, at whatever the book gives."),
                    P.Choice("limit", "Limit", "Rests at the price input, after the offset, and fills only there or better. It may never fill."),
                    P.Choice("stopMarket", "Stop market", "Waits for the market to trade through the price input, then goes to market: an entry on a breakout, or a way out."),
                    P.Choice("stopLimit", "Stop limit", "Waits for the market to trade through the price input, then rests a limit order at that same price: a bound on what it pays, which can leave it unfilled."),
                    P.Choice("marketIfTouched", "Market if touched", "Waits for the market to come back to the price input, then goes to market. The mirror of a stop market."),
                    P.Choice("limitIfTouched", "Limit if touched", "Waits for the market to reach the price input, then rests a limit order at it.")),
                SizingParam,
                OffsetParam,
                TifParam,
                P.Bool("postOnly", false, "Post only"),
                P.Bool("reduceOnly", false, "Reduce only"),
                P.Bool("onlyWhenFlat", true, "Only when flat", "Skip the trigger while a position is open on the instrument."),
                WorkParam,
                P.Int("cancelAfterBars", 0, 0, 100_000, "Cancel after bars", null, "0 leaves the order working."),
                P.Str("tag", null, false, "Tag"),
            ],
            Factory = ctx => new OrderNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.bracket",
            Kind = NodeKind.Action,
            DisplayName = "Bracket order",
            Description = "Entry with a stop-loss and a take-profit submitted together as one contingent list.",
            FaceTemplate = "{side} bracket: stop at {stop}, target at {target}",
            Inputs =
            [
                Trigger,
                new PortSpec("stop", ValueKind.Price, Required: true), new PortSpec("target", ValueKind.Price, Required: true),
                new PortSpec("entry", ValueKind.Price, Description: "limit entry price; market entry when unconnected"),
                new PortSpec("quantity", ValueKind.Quantity),
            ],
            Outputs = [new PortSpec("submitted", ValueKind.Pulse), new PortSpec("filled", ValueKind.Pulse), new PortSpec("position", ValueKind.Position), new PortSpec("closed", ValueKind.Pulse)],
            Params = [SideParam, SizingParam, TifParam, P.Bool("onlyWhenFlat", true, "Only when flat")],
            Factory = ctx => new BracketNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.ladder",
            Kind = NodeKind.Action,
            DisplayName = "Ladder",
            Description = "Several limit orders spread evenly between two prices, the total size split across them.",
            FaceTemplate = "{levels} {side} limits from {from} to {to}",
            Inputs = [Trigger, new PortSpec("from", ValueKind.Price, Required: true), new PortSpec("to", ValueKind.Price, Required: true), new PortSpec("quantity", ValueKind.Quantity)],
            Outputs = [new PortSpec("submitted", ValueKind.Pulse), new PortSpec("filledCount", ValueKind.Series), new PortSpec("position", ValueKind.Position), new PortSpec("working", ValueKind.Bool)],
            Params = [SideParam, P.Int("levels", 4, 2, 50), SizingParam, TifParam, P.Bool("postOnly", true, "Post only")],
            Factory = ctx => new LadderNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.dca",
            Kind = NodeKind.Action,
            DisplayName = "Safety orders",
            Description = "A ladder of safety orders anchored at the entry fill: each step further from the entry than the last, each larger, and never more in total than the cap. Cancelled when the position closes.",
            FaceTemplate = "{count} safety orders, first {firstStep} away, step x{stepScale}, size x{volumeScale}",
            Inputs =
            [
                Trigger,
                new PortSpec("position", ValueKind.Position, Required: true),
                new PortSpec("atr", ValueKind.Series, Description: "for atr steps"),
            ],
            Outputs =
            [
                new PortSpec("placed", ValueKind.Pulse), new PortSpec("working", ValueKind.Bool),
                new PortSpec("filledCount", ValueKind.Series), new PortSpec("remaining", ValueKind.Series),
                new PortSpec("nextPrice", ValueKind.Price),
            ],
            Params =
            [
                P.Int("count", 3, 1, 50, "Safety orders"),
                P.Obj("firstStep", true, "First step", "How far the first safety order sits from the entry fill. The unit decides what the number beside it counts.",
                    P.Enum("unit", "percent", "Unit", "What the step is counted in.",
                        P.Choice("percent", "Percent of the entry", "A share of the entry price: 1 puts the first safety order one percent below a long entry.", "% of the entry price", "value", step: "0.1"),
                        P.Choice("atr", "ATR multiples", "Multiples of the atr input as it stood when the position opened. Needs the atr input.", "\u00d7 ATR", "value", step: "0.1"),
                        P.Choice("ticks", "Ticks", "Whole price steps of the instrument.", "ticks", "value"),
                        P.Choice("price", "Price amount", "The distance in the quote currency itself.", "price", "value")),
                    P.Dec("value", 1m, 0m, null, null, "Value")),
                P.Dec("stepScale", 1.5m, 1m, 10m, 0.1m, "Step scale", null, "Each step is this many times the one before it."),
                P.Dec("volumeScale", 1.5m, 0.1m, 10m, 0.1m, "Volume scale", null, "Each safety order is this many times the size of the one before it."),
                P.Dec("firstSize", 1m, 0.01m, 100m, 0.1m, "First size", null, "The first safety order, as a multiple of what opened the position."),
                P.Dec("maxTotalSize", 0m, 0m, null, null, "Max total size", null, "The most the position may reach, in base units; 0 leaves it uncapped."),
                TifParam,
                P.Bool("postOnly", true, "Post only"),
            ],
            Factory = ctx => new DcaNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.grid",
            Kind = NodeKind.Action,
            DisplayName = "Grid",
            Description = "Works a price range level by level: at every level an order waiting to enter, and once it has, an order waiting to take the profit on it. A level that completes its round trip arms itself again, so the grid keeps working the range for as long as it is enabled. What is resting is cancelled when the grid is switched off or the strategy stops.",
            FaceTemplate = "{levels} levels from {from} to {to}, {profitPerGrid}% per grid",
            Inputs =
            [
                new PortSpec("enable", ValueKind.Bool, Required: true, Label: "while", Description: "the grid stands for as long as this holds"),
                new PortSpec("from", ValueKind.Price, Required: true, Label: "range from"),
                new PortSpec("to", ValueKind.Price, Required: true, Label: "range to"),
            ],
            Outputs =
            [
                new PortSpec("armed", ValueKind.Bool),
                new PortSpec("levels", ValueKind.Series, Label: "Levels armed"),
                new PortSpec("roundTrips", ValueKind.Series, Label: "Round trips"),
                new PortSpec("filled", ValueKind.Pulse, Label: "A level entered"),
                new PortSpec("closed", ValueKind.Pulse, Label: "A level turned over"),
            ],
            Params =
            [
                P.Int("levels", 5, 2, 100, "Levels"),
                P.Dec("profitPerGrid", 0.5m, 0.01m, 100m, 0.1m, "Profit per grid", "%", "How far from its own price a level takes the profit on what it entered."),
                P.Dec("sizePerLevel", 0.01m, 0m, null, null, "Size per level", null, "In base units, at every level."),
                P.Enum("side", "buy", "Side", "Which way the grid works the range.",
                    P.Choice("buy", "Buy the range", "A buy waiting at every level and a sell above it, so the grid works the range long."),
                    P.Choice("sell", "Sell the range", "A sell waiting at every level and a buy below it, so the grid works the range short. Needs a venue that allows shorting the instrument.")),
                TifParam,
                P.Bool("postOnly", true, "Post only"),
            ],
            Factory = ctx => new GridNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.cancel",
            Kind = NodeKind.Action,
            DisplayName = "Cancel orders",
            Description = "Cancels this strategy's open orders on the instrument.",
            FaceTemplate = "Cancel {scope} orders",
            Inputs = [Trigger],
            Outputs = [new PortSpec("done", ValueKind.Pulse)],
            Params =
            [
                P.Enum("scope", "all", "Scope", "Which of this strategy's working orders on the instrument are cancelled.",
                    P.Choice("all", "All orders", "Every order this strategy has working on the instrument."),
                    P.Choice("buys", "Buys", "Only the working buy orders."),
                    P.Choice("sells", "Sells", "Only the working sell orders."),
                    P.Choice("entries", "Entries", "Only the orders that would open a position or add to one: everything that is not reduce-only."),
                    P.Choice("exits", "Exits", "Only the reduce-only orders: the stops, the targets and anything else placed to get out.")),
            ],
            Factory = ctx => new CancelNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.close",
            Kind = NodeKind.Action,
            DisplayName = "Close position",
            Description = "Flattens the instrument with a reduce-only market order, and publishes the price it got out at.",
            FaceTemplate = "Close the position",
            Inputs = [Trigger],
            Outputs = [new PortSpec("done", ValueKind.Pulse), new PortSpec("filled", ValueKind.Pulse, Label: "Closed"), new PortSpec("fillPrice", ValueKind.Price, Label: "Exit price")],
            Factory = ctx => new CloseNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.modify",
            Kind = NodeKind.Action,
            DisplayName = "Modify orders",
            Description = "Moves this strategy's working limit orders on the instrument to a new price.",
            FaceTemplate = "Move working limits to {price}",
            Inputs = [Trigger, new PortSpec("price", ValueKind.Price, Required: true)],
            Outputs = [new PortSpec("done", ValueKind.Pulse)],
            Params = [P.Str("tag", null, false, "Only orders with tag")],
            AmendsOrders = true,
            Factory = ctx => new ModifyNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.trail",
            Kind = NodeKind.Action,
            DisplayName = "Trail stop",
            Description = "Keeps the protective stop a fixed distance behind a reference while enabled, never loosening it.",
            FaceTemplate = "Trail the stop {distance} behind {reference}",
            Inputs = [new PortSpec("enable", ValueKind.Bool, Required: true), new PortSpec("reference", ValueKind.Price, Required: true), new PortSpec("atr", ValueKind.Series)],
            Outputs = [new PortSpec("stopPrice", ValueKind.Price), new PortSpec("moved", ValueKind.Pulse)],
            Params =
            [
                P.Obj("distance", true, "Distance", "How far behind the reference the stop is kept. The unit decides what the number beside it counts.",
                    P.Enum("unit", "percent", "Unit", "What the distance is counted in.",
                        P.Choice("percent", "Percent of the reference", "A share of the reference price: 1 keeps the stop one percent behind it.", "% of the reference", "value", step: "0.1"),
                        P.Choice("atr", "ATR multiples", "Multiples of the atr input, so the stop gives the market more room when it is noisier. Needs the atr input.", "\u00d7 ATR", "value", step: "0.1"),
                        P.Choice("ticks", "Ticks", "Whole price steps of the instrument.", "ticks", "value"),
                        P.Choice("price", "Price amount", "The distance in the quote currency itself.", "price", "value")),
                    P.Dec("value", 1m, 0m, null, null, "Value")),
            ],
            AmendsOrders = true,
            Factory = ctx => new TrailNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.moveStop",
            Kind = NodeKind.Action,
            DisplayName = "Move stop",
            Description = "Moves the protective stop to a price when the trigger turns true.",
            FaceTemplate = "Move the stop to {price}",
            Inputs = [Trigger, new PortSpec("price", ValueKind.Price, Required: true)],
            Outputs = [new PortSpec("done", ValueKind.Pulse)],
            Params = [P.Bool("onlyImprove", true, "Only tighten", "Never move the stop further from the market.")],
            AmendsOrders = true,
            Factory = ctx => new MoveStopNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.scaleOut",
            Kind = NodeKind.Action,
            DisplayName = "Scale out",
            Description = "Reduces the open position by a fraction with a reduce-only market order.",
            FaceTemplate = "Take {fraction}% off",
            Inputs = [Trigger],
            Outputs = [new PortSpec("done", ValueKind.Pulse), new PortSpec("filled", ValueKind.Pulse)],
            Params = [P.Dec("fraction", 50m, 1m, 100m, 1m, "Fraction (%)")],
            Factory = ctx => new ScaleOutNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.signal",
            Kind = NodeKind.Action,
            DisplayName = "Publish signal",
            Description = "Publishes a named value other strategies and actors can subscribe to.",
            FaceTemplate = "Publish signal {name}",
            Inputs = [Trigger, new PortSpec("value", ValueKind.Series)],
            Outputs = [new PortSpec("done", ValueKind.Pulse)],
            Params = [P.Str("name", "signal", true, "Name"), P.Dec("value", 1m, null, null, null, "Constant", null, "Used when the value input is unconnected.")],
            Factory = ctx => new SignalNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "act.note",
            Kind = NodeKind.Action,
            DisplayName = "Note",
            Description = "Writes a decision event with a message, for the monitor and the assistant.",
            FaceTemplate = "Note: {message}",
            Inputs = [Trigger],
            Outputs = [new PortSpec("done", ValueKind.Pulse)],
            Params = [P.Str("message", "note", true, "Message")],
            Factory = ctx => new NoteNode(ctx),
        };
    }

    internal static OrderSide Side(string s) => s == "sell" ? OrderSide.Sell : OrderSide.Buy;

    internal static TimeInForce Tif(string s) => s switch { "ioc" => TimeInForce.Ioc, "fok" => TimeInForce.Fok, "day" => TimeInForce.Day, _ => TimeInForce.Gtc };

    internal static decimal ApplyOffset(NodeParams offset, Instrument instrument, decimal price, decimal? atr, int direction)
    {
        decimal value = offset.Dec("value", 0m);
        if (value == 0m)
        {
            return price;
        }

        decimal delta = offset.Str("unit", "ticks") switch
        {
            "percent" => price * value / 100m,
            "atr" => (atr ?? 0m) * value,
            "price" => value,
            _ => instrument.PriceIncrement.Value * value,
        };
        return price + direction * delta;
    }

    /// <summary>Tracks one strategy order from submission to a terminal state and turns its events into pulses.</summary>

    private sealed class OrderNode : NodeBase, IOrderEventObserver, IStatefulNode
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));
        private readonly TrackedOrder _order = new();

        public OrderNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        // The cache goes with the event because an order worked in pieces is followed through them: each piece is an
        // order of its own, and only the cache knows whose.
        public void OnOrderEvent(OrderEvent e) => _order.OnOrderEvent(e, Services.Cache);

        public JsonElement SaveState() => _order.Save();

        public void LoadState(JsonElement state) => _order.Load(state);

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            _order.Refresh(Services);
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            bool onlyWhenFlat = P.Bool("onlyWhenFlat", true);
            if (fire && onlyWhenFlat && !Services.Portfolio.IsFlat(instrument.Id))
            {
                fire = false;
                ctx.Emit("skip", "trigger ignored: position already open");
            }

            if (fire && _order.IsWorking)
            {
                fire = false;
                ctx.Emit("skip", "trigger ignored: an order is still working");
            }

            // What happened to the order the node was following, read before a new one can take its place: a fill can
            // arrive between two bars, and a trigger firing on this bar would otherwise throw that report away unsent.
            bool submitted = _order.PendingSubmitted;
            bool filled = _order.PendingFilled;
            bool rejected = _order.PendingRejected;
            string? rejectedReason = _order.LastReason;
            decimal? fillPrice = _order.FillPrice;
            PositionId? positionId = _order.PositionId;

            if (fire)
            {
                Submit(ctx, instrument);
            }

            submitted |= _order.PendingSubmitted;
            filled |= _order.PendingFilled;
            rejected |= _order.PendingRejected;
            rejectedReason = _order.PendingRejected ? _order.LastReason : rejectedReason;
            fillPrice = _order.FillPrice ?? fillPrice;
            positionId = _order.PositionId ?? positionId;

            int cancelAfter = P.Int("cancelAfterBars", 0);
            if (cancelAfter > 0 && _order.IsWorking && _order.Id is { } id && ctx.Index - _order.SubmittedAt >= cancelAfter)
            {
                Order? working = Services.Cache.Order(id);
                if (working is not null && working.IsOpen)
                {
                    Services.CancelOrder(working);
                    ctx.Emit("cancel", $"order cancelled after {cancelAfter} bars without a fill");
                }
            }

            if (rejected)
            {
                ctx.Emit("rejected", rejectedReason ?? "order rejected");
            }

            ctx.Set("submitted", submitted);
            ctx.Set("filled", filled);
            ctx.Set("rejected", rejected);
            ctx.Set("working", _order.IsWorking);
            if (positionId is { } pid)
            {
                ctx.Set("position", pid);
            }

            if (fillPrice is { } px)
            {
                ctx.Set("fillPrice", px);
            }

            // A worked order is not at the venue for this to read - the instruction never goes there - and the pieces
            // have already had their say; what the cache says about an instruction is what the pieces said.
            if (_order.PendingFilled)
            {
                _order.IsWorking = _order.Id is { } oid && Services.Cache.IsOrderOpen(oid);
            }

            _order.PendingSubmitted = false;
            _order.PendingFilled = false;
            _order.PendingRejected = false;
        }

        private void Submit(EvalContext ctx, Instrument instrument)
        {
            OrderSide side = Side(P.Str("side", "buy"));
            decimal close = ctx.Bar.Close.Value;
            decimal basePrice = ctx.Dec("price") ?? close;
            string orderType = P.Str("orderType", "market");
            int direction = side == OrderSide.Buy ? -1 : 1;
            if (orderType is "stopMarket" or "stopLimit")
            {
                direction = -direction;
            }

            decimal priceValue = ApplyOffset(P.Obj("offset"), instrument, basePrice, ctx.Dec("atr"), direction);
            Price price = instrument.MakePrice(priceValue);

            Quantity? quantity = ctx.Dec("quantity") is { } q ? instrument.MakeQuantity(q) : null;
            if (quantity is null)
            {
                NodeParams sizing = P.Obj("sizing");
                quantity = Sizing.Compute(Services, instrument, sizing, orderType == "market" ? close : price.Value, ctx.Dec("stop"), m => ctx.Emit("sizing", m));
            }

            if (quantity is null || quantity.Value.Value <= 0m)
            {
                ctx.Emit("skip", "trigger ignored: sizing produced no quantity");
                return;
            }

            TimeInForce tif = Tif(P.Str("tif", "gtc"));
            bool reduceOnly = P.Bool("reduceOnly", false);
            bool postOnly = P.Bool("postOnly", false);
            List<string> tags = P.StrOrNull("tag") is { } tag ? [tag, "node:" + NodeId] : ["node:" + NodeId];

            // Who works the order, if anyone. Only a market or a limit order can be worked, and the validator refuses a
            // document that asks for anything else, which is why nothing here has to decide what to do about one.
            NodeParams work = P.Obj(OrderWork.Param);
            string algorithm = work.Str(OrderWork.Algorithm, OrderWork.None);
            ExecAlgorithmId? execAlgorithmId = algorithm switch
            {
                OrderWork.Twap => TwapExecAlgorithm.DefaultExecAlgorithmId,
                _ => null,
            };
            IReadOnlyDictionary<string, string>? execAlgorithmParams = null;
            TimeSpan horizon = default;
            TimeSpan interval = default;
            if (execAlgorithmId is not null)
            {
                horizon = TimeSpan.FromMinutes((double)work.Dec(OrderWork.HorizonMinutes, OrderWork.DefaultHorizonMinutes));
                interval = TimeSpan.FromMinutes((double)work.Dec(OrderWork.IntervalMinutes, OrderWork.DefaultIntervalMinutes));
                execAlgorithmParams = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TwapExecAlgorithm.HorizonParam] = horizon.ToString("c", CultureInfo.InvariantCulture),
                    [TwapExecAlgorithm.IntervalParam] = interval.ToString("c", CultureInfo.InvariantCulture),
                };
            }

            Order order = orderType switch
            {
                "limit" => Services.OrderFactory.Limit(instrument.Id, side, quantity.Value, price, tif, postOnly: postOnly, reduceOnly: reduceOnly, execAlgorithmId: execAlgorithmId, execAlgorithmParams: execAlgorithmParams, tags: tags),
                "stopMarket" => Services.OrderFactory.StopMarket(instrument.Id, side, quantity.Value, price, reduceOnly: reduceOnly, tags: tags),
                "stopLimit" => Services.OrderFactory.StopLimit(instrument.Id, side, quantity.Value, price, price, reduceOnly: reduceOnly, tags: tags),
                "marketIfTouched" => Services.OrderFactory.MarketIfTouched(instrument.Id, side, quantity.Value, price, reduceOnly: reduceOnly, tags: tags),
                "limitIfTouched" => Services.OrderFactory.LimitIfTouched(instrument.Id, side, quantity.Value, price, price, reduceOnly: reduceOnly, tags: tags),
                _ => Services.OrderFactory.Market(instrument.Id, side, quantity.Value, tif == TimeInForce.Day ? TimeInForce.Gtc : tif, reduceOnly: reduceOnly, execAlgorithmId: execAlgorithmId, execAlgorithmParams: execAlgorithmParams, tags: tags),
            };

            // Track before submitting: with zero latency the simulated venue fills synchronously, and the fill must find
            // its owner. A worked order is followed until the algorithm has had its say - the horizon, and one interval
            // of slack for the last piece - because the instruction itself never fills and never closes.
            _order.Track(order, ctx.Index, execAlgorithmId is null ? null : Services.Clock.Timestamp + horizon + interval);
            Dictionary<string, string> values = new(StringComparer.Ordinal)
            {
                ["clientOrderId"] = order.ClientOrderId.Value,
                ["quantity"] = F(quantity.Value.Value),
                ["price"] = F(price.Value),
            };
            string what = $"{side} {orderType} {quantity.Value} @ {(orderType == "market" ? "market" : F(price.Value))}";
            if (execAlgorithmId is { } worked)
            {
                values[OrderWork.Param] = algorithm;
                values[OrderWork.HorizonMinutes] = F((decimal)horizon.TotalMinutes);
                values[OrderWork.IntervalMinutes] = F((decimal)interval.TotalMinutes);
                what += $", worked as {worked} in pieces every {F((decimal)interval.TotalMinutes)} min over {F((decimal)horizon.TotalMinutes)} min";
            }

            ctx.Emit("order", what, values);
            Services.SubmitOrder(order);
        }
    }

    private sealed class BracketNode : NodeBase, IOrderEventObserver, IPositionEventObserver, IStatefulNode
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));
        private readonly TrackedOrder _entry = new();
        private bool _pendingClosed;

        public BracketNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnOrderEvent(OrderEvent e) => _entry.OnOrderEvent(e);

        public void OnPositionEvent(PositionEvent e)
        {
            if (e is PositionClosed closed && _entry.PositionId is { } pid && closed.PositionId == pid)
            {
                _pendingClosed = true;
            }
        }

        public JsonElement SaveState() => _entry.Save();

        public void LoadState(JsonElement state) => _entry.Load(state);

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            _entry.Refresh(Services);
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire && P.Bool("onlyWhenFlat", true) && !Services.Portfolio.IsFlat(instrument.Id))
            {
                fire = false;
                ctx.Emit("skip", "trigger ignored: position already open");
            }

            if (fire && (ctx.Dec("stop") is not { } stop || ctx.Dec("target") is not { } target))
            {
                fire = false;
                ctx.Emit("skip", "trigger ignored: stop or target missing");
            }
            else if (fire)
            {
                decimal stopValue = ctx.Dec("stop")!.Value;
                decimal targetValue = ctx.Dec("target")!.Value;
                OrderSide side = Side(P.Str("side", "buy"));
                decimal? entryValue = ctx.Dec("entry");
                decimal reference = entryValue ?? ctx.Bar.Close.Value;
                bool isBuy = side == OrderSide.Buy;
                decimal entryPrice = instrument.MakePrice(reference).Value;
                decimal stopPrice = instrument.MakePrice(stopValue).Value;
                decimal targetPrice = instrument.MakePrice(targetValue).Value;
                Quantity? quantity = ctx.Dec("quantity") is { } q ? instrument.MakeQuantity(q) : null;
                if (quantity is null)
                {
                    NodeParams sizing = P.Obj("sizing");
                    quantity = Sizing.Compute(Services, instrument, sizing, reference, stopValue, m => ctx.Emit("sizing", m));
                }

                // A bracket is an entry with a stop behind it and a target beyond it. A level on the wrong side would fill or be
                // refused the moment the entry fills, leaving a trade nobody designed; the setup is not taken and the log says why.
                if (isBuy ? targetPrice <= entryPrice : targetPrice >= entryPrice)
                {
                    ctx.Emit("skip", $"trigger ignored: target {F(targetPrice)} is not {(isBuy ? "above" : "below")} the entry {F(entryPrice)}");
                }
                else if (isBuy ? stopPrice >= entryPrice : stopPrice <= entryPrice)
                {
                    ctx.Emit("skip", $"trigger ignored: stop {F(stopPrice)} is not {(isBuy ? "below" : "above")} the entry {F(entryPrice)}");
                }
                else if (quantity is null)
                {
                    ctx.Emit("skip", "trigger ignored: sizing produced no quantity");
                }
                else
                {
                    OrderList list = Services.OrderFactory.BracketOrder(instrument.Id, side, quantity.Value, instrument.MakePrice(stopValue), instrument.MakePrice(targetValue),
                        entryValue is { } ep ? instrument.MakePrice(ep) : null, timeInForce: Tif(P.Str("tif", "gtc")));
                    _entry.Track(list.First, ctx.Index);
                    ctx.Emit("order", $"{side} bracket {quantity.Value}: stop {F(stopValue)}, target {F(targetValue)}",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["listId"] = list.Id.Value, ["quantity"] = F(quantity.Value.Value) });
                    Services.SubmitOrderList(list);
                }
            }

            ctx.Set("submitted", _entry.PendingSubmitted);
            ctx.Set("filled", _entry.PendingFilled);
            ctx.Set("closed", _pendingClosed);
            if (_entry.PositionId is { } pid)
            {
                ctx.Set("position", pid);
            }

            _entry.PendingSubmitted = false;
            _entry.PendingFilled = false;
            _pendingClosed = false;
        }
    }

    /// <summary>
    /// The safety-order ladder an averaging strategy is built on. It anchors at the fill that opened the position,
    /// steps away from it by a growing distance with a growing size, and stops at the cap on the whole position -
    /// which is the only thing standing between "average down" and an account spent on one idea. The rungs are the
    /// node's own orders: it cancels what is left of them when the position closes, so a new position starts with a
    /// new ladder rather than inheriting a stale one.
    /// </summary>
    private sealed class DcaNode : NodeBase, IOrderEventObserver, IPositionEventObserver, IStatefulNode
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        private readonly List<TrackedOrder> _rungs = new();
        private PositionId? _managed;
        private int _filled;
        private bool _pendingPlaced;
        private bool _pendingClosed;
        private decimal? _nextPrice;

        public DcaNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnOrderEvent(OrderEvent e)
        {
            foreach (TrackedOrder rung in _rungs)
            {
                rung.OnOrderEvent(e);
            }
        }

        public void OnPositionEvent(PositionEvent e)
        {
            if (_managed is { } id && e is PositionClosed closed && closed.PositionId == id)
            {
                _pendingClosed = true;
            }
        }

        public JsonElement SaveState() => JsonSerializer.SerializeToElement(new
        {
            managed = _managed?.Value,
            filled = _filled,
            rungs = _rungs.Select(r => r.Id?.Value).Where(v => v is not null).ToArray(),
        });

        public void LoadState(JsonElement state)
        {
            if (state.TryGetProperty("managed", out JsonElement managed) && managed.ValueKind == JsonValueKind.String)
            {
                _managed = new PositionId(managed.GetString()!);
            }

            _filled = state.TryGetProperty("filled", out JsonElement filled) && filled.ValueKind == JsonValueKind.Number ? filled.GetInt32() : 0;
            if (state.TryGetProperty("rungs", out JsonElement rungs) && rungs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement rung in rungs.EnumerateArray())
                {
                    if (rung.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    TrackedOrder tracked = new() { Id = new ClientOrderId(rung.GetString()!), IsWorking = true };
                    _rungs.Add(tracked);
                }
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            foreach (TrackedOrder rung in _rungs)
            {
                rung.Refresh(Services);
                if (rung.PendingFilled)
                {
                    _filled++;
                    rung.PendingFilled = false;
                }
            }

            Position? position = Services.Cache.PositionsOpen(instrumentId: instrument.Id, strategyId: Services.StrategyId).FirstOrDefault();
            if (_pendingClosed || (_managed is not null && position is null))
            {
                Cancel(ctx, "the position closed");
            }

            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire && position is null)
            {
                fire = false;
                ctx.Emit("skip", "trigger ignored: there is no position to average into");
            }

            if (fire && _rungs.Any(r => r.IsWorking))
            {
                fire = false;
                ctx.Emit("skip", "trigger ignored: the safety orders of this position are still working");
            }

            if (fire)
            {
                Place(ctx, instrument, position!);
            }

            ctx.Set("placed", _pendingPlaced);
            ctx.Set("working", _rungs.Any(r => r.IsWorking));
            ctx.Set("filledCount", (decimal)_filled);
            ctx.Set("remaining", (decimal)_rungs.Count(r => r.IsWorking));
            if (_nextPrice is { } next && _rungs.Any(r => r.IsWorking))
            {
                ctx.Set("nextPrice", instrument.MakePrice(next));
            }

            _pendingPlaced = false;
        }

        private void Place(EvalContext ctx, Instrument instrument, Position position)
        {
            _managed = position.Id;
            _rungs.Clear();
            _filled = 0;
            _nextPrice = null;

            bool isLong = position.IsLong;
            int away = isLong ? -1 : 1;
            decimal entry = position.AvgPxOpen;
            decimal step = ActionNodes.ApplyOffset(P.Obj("firstStep"), instrument, entry, ctx.Dec("atr"), away) - entry;
            if (step == 0m)
            {
                ctx.Emit("skip", "no safety orders placed: the first step is zero, so every rung would sit on the entry");
                return;
            }

            decimal stepScale = P.Dec("stepScale", 1.5m);
            decimal volumeScale = P.Dec("volumeScale", 1.5m);
            decimal size = position.Quantity.Value * P.Dec("firstSize", 1m);
            decimal cap = P.Dec("maxTotalSize", 0m);
            decimal total = position.Quantity.Value;
            decimal price = entry;
            decimal distance = step;
            OrderSide side = isLong ? OrderSide.Buy : OrderSide.Sell;
            int count = P.Int("count", 3);
            int placed = 0;

            for (int i = 0; i < count; i++)
            {
                price = entry + distance;
                Quantity quantity = instrument.MakeQuantity(size);
                if (quantity.IsZero)
                {
                    ctx.Emit("skip", $"safety order {i + 1} of {count} not placed: its size rounds to zero");
                    break;
                }

                if (cap > 0m && total + quantity.Value > cap)
                {
                    ctx.Emit("skip", $"safety order {i + 1} of {count} not placed: it would take the position past the cap of {F(cap)}");
                    break;
                }

                LimitOrder order = Services.OrderFactory.Limit(instrument.Id, side, quantity, instrument.MakePrice(price), Tif(P.Str("tif", "gtc")),
                    postOnly: P.Bool("postOnly", true), tags: ["dca:" + (i + 1), "node:" + NodeId]);
                TrackedOrder tracked = new();
                tracked.Track(order, ctx.Index);
                _rungs.Add(tracked);
                Services.SubmitOrder(order);
                if (placed == 0)
                {
                    _nextPrice = price;
                }

                placed++;
                total += quantity.Value;
                size *= volumeScale;
                distance *= stepScale;
            }

            if (placed > 0)
            {
                _pendingPlaced = true;
                ctx.Emit("order", $"{placed} safety {(placed == 1 ? "order" : "orders")} from {F(entry + step)} to {F(price)}, total position at most {F(total)}",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["entry"] = F(entry), ["first"] = F(entry + step), ["total"] = F(total) });
            }
        }

        private void Cancel(EvalContext ctx, string why)
        {
            int cancelled = 0;
            foreach (TrackedOrder rung in _rungs)
            {
                if (rung.Id is { } id && Services.Cache.Order(id) is { IsOpen: true } order)
                {
                    Services.CancelOrder(order);
                    cancelled++;
                }
            }

            _rungs.Clear();
            _managed = null;
            _pendingClosed = false;
            _nextPrice = null;
            if (cancelled > 0)
            {
                ctx.Emit("cancel", $"{cancelled} safety {(cancelled == 1 ? "order" : "orders")} cancelled: {why}");
            }
        }
    }

    /// <summary>
    /// A grid. At every level of the range one order waits to enter and, once it has, one waits to take the profit on
    /// that entry; the level that completes the round trip arms itself again. That is the part a hand-wired graph could
    /// never do - an order node per level still leaves the level empty once it has been through - and the reason the
    /// runtime keeps state per level. The grid stands for as long as it is enabled: switched off, or the strategy
    /// stopped, what is resting is taken back rather than left on the venue.
    /// </summary>
    private sealed class GridNode : NodeBase, IOrderEventObserver, IStatefulNode, IStoppableNode
    {
        private readonly NodeLevels _levels = new();
        private bool _armed;
        private int _roundTrips;
        private bool _pendingFilled;
        private bool _pendingClosed;

        /// <summary>The range the venue would not take, so the same range is not tried again bar after bar.</summary>
        private (decimal From, decimal To)? _refused;

        public GridNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnOrderEvent(OrderEvent e) => _levels.OnOrderEvent(e);

        public void OnStrategyStopping()
        {
            if (_armed)
            {
                Disarm("the strategy is stopping");
            }
        }

        public JsonElement SaveState() => JsonSerializer.SerializeToElement(new
        {
            armed = _armed,
            roundTrips = _roundTrips,
            levels = _levels.Save(),
        });

        public void LoadState(JsonElement state)
        {
            _armed = state.TryGetProperty("armed", out JsonElement armed) && armed.ValueKind == JsonValueKind.True;
            _roundTrips = state.TryGetProperty("roundTrips", out JsonElement trips) && trips.ValueKind == JsonValueKind.Number ? trips.GetInt32() : 0;
            if (state.TryGetProperty("levels", out JsonElement levels))
            {
                _levels.Load(levels);
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            _levels.Refresh(Services);

            if (!ctx.Bool("enable"))
            {
                if (_armed)
                {
                    Disarm("the grid was switched off");
                }

                _refused = null;
            }
            else
            {
                if (!_armed && ctx.Dec("from") is { } from && ctx.Dec("to") is { } to)
                {
                    Arm(ctx, instrument, from, to);
                }

                if (_armed)
                {
                    Work(ctx, instrument);
                }
            }

            ctx.Set("armed", _armed);
            ctx.Set("levels", (decimal)_levels.Count);
            ctx.Set("roundTrips", (decimal)_roundTrips);
            ctx.Set("filled", _pendingFilled);
            ctx.Set("closed", _pendingClosed);
            _pendingFilled = false;
            _pendingClosed = false;
        }

        private void Arm(EvalContext ctx, Instrument instrument, decimal from, decimal to)
        {
            decimal low = Math.Min(from, to);
            decimal high = Math.Max(from, to);
            if (_refused == (low, high))
            {
                // Said once already, and nothing about the range has changed since.
                return;
            }

            if (high - low <= 0m)
            {
                _refused = (low, high);
                ctx.Emit("skip", $"no grid placed: a range from {F(low)} to {F(high)} has no width");
                return;
            }

            Quantity size = instrument.MakeQuantity(P.Dec("sizePerLevel", 0.01m));
            if (size.IsZero || (instrument.MinQuantity is { } minimum && size.Value < minimum.Value))
            {
                _refused = (low, high);
                ctx.Emit("skip", $"no grid placed: {F(P.Dec("sizePerLevel", 0.01m))} per level is below what {instrument.Id.Symbol} trades in");
                return;
            }

            int count = P.Int("levels", 5);
            _levels.Clear();
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                Price price = instrument.MakePrice(low + ((high - low) * i / (count - 1)));
                if (instrument.MinNotional is { } min && instrument.NotionalValue(size, price).Amount < min.Amount)
                {
                    ctx.Emit("skip", $"level {i + 1} of {count} at {F(price.Value)} not placed: {F(instrument.NotionalValue(size, price).Amount)} is below the venue's minimum of {F(min.Amount)}");
                    continue;
                }

                NodeLevel level = _levels.Ensure((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                level.Price = price.Value;
                level.Size = size.Value;
                Enter(instrument, level, ctx.Index);
                placed++;
            }

            if (placed == 0)
            {
                _levels.Clear();
                _refused = (low, high);
                return;
            }

            _armed = true;
            _refused = null;
            ctx.Emit("order", $"grid armed: {placed} {(placed == 1 ? "level" : "levels")} from {F(low)} to {F(high)}, {F(size.Value)} each, {F(P.Dec("profitPerGrid", 0.5m))}% per grid",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["levels"] = placed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["from"] = F(low),
                    ["to"] = F(high),
                });
        }

        private void Work(EvalContext ctx, Instrument instrument)
        {
            bool isLong = P.Str("side", "buy") == "buy";
            decimal profit = P.Dec("profitPerGrid", 0.5m) / 100m;
            List<string> dead = new();
            foreach (NodeLevel level in _levels.All)
            {
                if (level.Entry.PendingFilled)
                {
                    level.Entry.PendingFilled = false;
                    _pendingFilled = true;
                    Price target = instrument.MakePrice(isLong ? level.Price * (1m + profit) : level.Price * (1m - profit));
                    LimitOrder order = Services.OrderFactory.Limit(instrument.Id, isLong ? OrderSide.Sell : OrderSide.Buy, instrument.MakeQuantity(level.Size), target,
                        Tif(P.Str("tif", "gtc")), postOnly: P.Bool("postOnly", true), tags: ["grid:" + level.Key, "grid:exit", "node:" + NodeId]);
                    level.Exit.Track(order, ctx.Index);
                    Services.SubmitOrder(order);
                    ctx.Emit("order", $"level {level.Key} entered at {F(level.Price)}; taking the profit at {F(target.Value)}");
                }

                if (level.Exit.PendingFilled)
                {
                    level.Exit.PendingFilled = false;
                    level.RoundTrips++;
                    _roundTrips++;
                    _pendingClosed = true;
                    ctx.Emit("order", $"level {level.Key} turned over ({level.RoundTrips} so far); arming it again at {F(level.Price)}");
                    Enter(instrument, level, ctx.Index);
                }

                // A level the venue refused - a post-only order it would have filled at once, a limit it will not take -
                // is out of the grid and said so, rather than counted as armed for the rest of the run.
                if (level.Entry.PendingRejected || level.Exit.PendingRejected)
                {
                    string reason = (level.Entry.PendingRejected ? level.Entry.LastReason : level.Exit.LastReason) ?? "refused";
                    ctx.Emit("reject", $"level {level.Key} at {F(level.Price)} left the grid: {reason}");
                    dead.Add(level.Key);
                }
            }

            foreach (string key in dead)
            {
                _levels.Remove(key);
            }

            if (_levels.Count == 0)
            {
                _armed = false;
                ctx.Emit("skip", "the grid has no levels left");
            }
        }

        private void Enter(Instrument instrument, NodeLevel level, long index)
        {
            bool isLong = P.Str("side", "buy") == "buy";
            LimitOrder order = Services.OrderFactory.Limit(instrument.Id, isLong ? OrderSide.Buy : OrderSide.Sell, instrument.MakeQuantity(level.Size), instrument.MakePrice(level.Price),
                Tif(P.Str("tif", "gtc")), postOnly: P.Bool("postOnly", true), tags: ["grid:" + level.Key, "grid:entry", "node:" + NodeId]);
            level.Entry.Track(order, index);
            Services.SubmitOrder(order);
        }

        private void Disarm(string why)
        {
            int cancelled = 0;
            foreach (NodeLevel level in _levels.All)
            {
                foreach (TrackedOrder tracked in new[] { level.Entry, level.Exit })
                {
                    if (tracked.Id is { } id && Services.Cache.Order(id) is { IsOpen: true } order)
                    {
                        Services.CancelOrder(order);
                        cancelled++;
                    }
                }
            }

            _levels.Clear();
            _armed = false;
            Services.Emit(NodeId, "cancel", $"{cancelled} grid {(cancelled == 1 ? "order" : "orders")} cancelled: {why}");
        }
    }

    private sealed class LadderNode : NodeBase, IOrderEventObserver
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));
        private readonly List<TrackedOrder> _orders = new();
        private int _filled;
        private bool _pendingSubmitted;

        public LadderNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnOrderEvent(OrderEvent e)
        {
            foreach (TrackedOrder o in _orders)
            {
                o.OnOrderEvent(e);
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            foreach (TrackedOrder o in _orders)
            {
                o.Refresh(Services);
                if (o.PendingFilled)
                {
                    _filled++;
                    o.PendingFilled = false;
                }
            }

            bool working = _orders.Any(o => o.IsWorking);
            if (_edge.Rising(ctx.Bool("trigger")) && !working && ctx.Dec("from") is { } from && ctx.Dec("to") is { } to)
            {
                int levels = P.Int("levels", 4);
                OrderSide side = Side(P.Str("side", "buy"));
                NodeParams sizing = P.Obj("sizing");
                Quantity? total = ctx.Dec("quantity") is { } q ? instrument.MakeQuantity(q) : Sizing.Compute(Services, instrument, sizing, (from + to) / 2m, null, m => ctx.Emit("sizing", m));
                if (total is null)
                {
                    ctx.Emit("skip", "trigger ignored: sizing produced no quantity");
                }
                else
                {
                    Quantity each = instrument.MakeQuantity(total.Value.Value / levels);
                    if (each.Value <= 0m)
                    {
                        ctx.Emit("skip", "trigger ignored: size per level rounds to zero");
                    }
                    else
                    {
                        _orders.Clear();
                        _filled = 0;
                        ctx.Emit("order", $"{side} ladder of {levels} from {F(from)} to {F(to)}, {F(each.Value)} each");
                        for (int i = 0; i < levels; i++)
                        {
                            decimal priceValue = levels == 1 ? from : from + (to - from) * i / (levels - 1);
                            LimitOrder order = Services.OrderFactory.Limit(instrument.Id, side, each, instrument.MakePrice(priceValue), Tif(P.Str("tif", "gtc")), postOnly: P.Bool("postOnly", true), tags: ["node:" + NodeId]);
                            TrackedOrder tracked = new();
                            tracked.Track(order, ctx.Index);
                            _orders.Add(tracked);
                            Services.SubmitOrder(order);
                        }

                        _pendingSubmitted = true;
                    }
                }
            }

            ctx.Set("submitted", _pendingSubmitted);
            ctx.Set("filledCount", (decimal)_filled);
            ctx.Set("working", _orders.Any(o => o.IsWorking));
            PositionId? pid = _orders.Select(o => o.PositionId).FirstOrDefault(p => p is not null);
            if (pid is { } id)
            {
                ctx.Set("position", id);
            }

            _pendingSubmitted = false;
        }
    }

    private sealed class CancelNode : NodeBase
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        public CancelNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire)
            {
                string scope = P.Str("scope", "all");
                IReadOnlyList<Order> open = Services.Cache.OrdersOpen(instrumentId: Ctx.InstrumentId, strategyId: Services.StrategyId);
                int count = 0;
                foreach (Order order in open)
                {
                    bool match = scope switch
                    {
                        "buys" => order.IsBuy,
                        "sells" => order.IsSell,
                        "entries" => !order.IsReduceOnly,
                        "exits" => order.IsReduceOnly,
                        _ => true,
                    };
                    if (match)
                    {
                        Services.CancelOrder(order);
                        count++;
                    }
                }

                ctx.Emit("cancel", $"cancelled {count} {scope} orders");
            }

            ctx.Set("done", fire);
        }
    }

    private sealed class CloseNode : NodeBase, IOrderEventObserver, IStatefulNode
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        // The node places its own reduce-only order instead of asking the strategy to flatten, because an order it
        // owns is one it can follow: a document that closes a position usually wants to know at what price it got out.
        private readonly TrackedOrder _order = new();

        public CloseNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnOrderEvent(OrderEvent e) => _order.OnOrderEvent(e);

        public JsonElement SaveState() => _order.Save();

        public void LoadState(JsonElement state) => _order.Load(state);

        public override void Evaluate(EvalContext ctx)
        {
            _order.Refresh(Services);
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire && !Services.Portfolio.IsFlat(Ctx.InstrumentId))
            {
                Close(ctx);
            }

            ctx.Set("done", fire);
            ctx.Set("filled", _order.PendingFilled);
            if (_order.FillPrice is { } px)
            {
                ctx.Set("fillPrice", px);
            }

            if (_order.PendingRejected)
            {
                ctx.Emit("rejected", _order.LastReason ?? "the closing order was rejected");
            }

            _order.PendingSubmitted = false;
            _order.PendingFilled = false;
            _order.PendingRejected = false;
        }

        private void Close(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            decimal net = Services.Portfolio.NetPosition(instrument.Id);
            OrderSide side = net > 0m ? OrderSide.Sell : OrderSide.Buy;
            Quantity quantity = instrument.MakeQuantity(Math.Abs(net));
            if (quantity.IsZero)
            {
                ctx.Emit("skip", "trigger ignored: the position is smaller than one size step");
                return;
            }

            MarketOrder order = Services.OrderFactory.Market(instrument.Id, side, quantity, reduceOnly: true, tags: ["exit:close", "node:" + NodeId]);
            // Track before submitting: with zero latency the simulated venue fills synchronously.
            _order.Track(order, ctx.Index);
            ctx.Emit("close", $"closing {quantity} at market",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["clientOrderId"] = order.ClientOrderId.Value, ["quantity"] = F(quantity.Value) });
            Services.SubmitOrder(order);
        }
    }

    private sealed class ModifyNode : NodeBase
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        public ModifyNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire && ctx.Dec("price") is { } priceValue)
            {
                Instrument instrument = RequireInstrument();
                Price price = instrument.MakePrice(priceValue);
                string? tag = P.StrOrNull("tag");
                int count = 0;
                foreach (Order order in Services.Cache.OrdersOpen(instrumentId: instrument.Id, strategyId: Services.StrategyId))
                {
                    if (order.Type != OrderType.Limit || (tag is not null && !order.Tags.Contains(tag)))
                    {
                        continue;
                    }

                    if (order.Price is { } current && current.Value != price.Value)
                    {
                        Services.ModifyOrder(order, price: price);
                        count++;
                    }
                }

                ctx.Emit("modify", $"moved {count} limit orders to {F(price.Value)}");
            }

            ctx.Set("done", fire);
        }
    }

    internal static IEnumerable<Order> ProtectiveStops(IStrategyServices services, InstrumentId instrumentId) =>
        services.Cache.OrdersOpen(instrumentId: instrumentId, strategyId: services.StrategyId).Where(o => o.Type is OrderType.StopMarket or OrderType.StopLimit && o.IsReduceOnly);

    private sealed class TrailNode : NodeBase
    {
        public TrailNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool moved = false;
            if (ctx.Bool("enable") && ctx.Dec("reference") is { } reference)
            {
                Instrument instrument = RequireInstrument();
                NodeParams distance = P.Obj("distance");
                foreach (Order stop in ProtectiveStops(Services, instrument.Id).ToList())
                {
                    int direction = stop.IsSell ? -1 : 1;
                    decimal target = ApplyOffset(distance, instrument, reference, ctx.Dec("atr"), direction);
                    Price newTrigger = instrument.MakePrice(target);
                    if (stop.TriggerPrice is { } current && (stop.IsSell ? newTrigger.Value > current.Value : newTrigger.Value < current.Value))
                    {
                        Services.ModifyOrder(stop, triggerPrice: newTrigger);
                        moved = true;
                        ctx.Set("stopPrice", newTrigger.Value);
                        ctx.Emit("trail", $"stop moved to {F(newTrigger.Value)}");
                    }
                    else if (stop.TriggerPrice is { } keep)
                    {
                        ctx.Set("stopPrice", keep.Value);
                    }
                }
            }

            ctx.Set("moved", moved);
        }
    }

    private sealed class MoveStopNode : NodeBase
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        public MoveStopNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire && ctx.Dec("price") is { } priceValue)
            {
                Instrument instrument = RequireInstrument();
                Price price = instrument.MakePrice(priceValue);
                bool onlyImprove = P.Bool("onlyImprove", true);
                foreach (Order stop in ProtectiveStops(Services, instrument.Id).ToList())
                {
                    if (stop.TriggerPrice is { } current && onlyImprove && (stop.IsSell ? price.Value <= current.Value : price.Value >= current.Value))
                    {
                        continue;
                    }

                    Services.ModifyOrder(stop, triggerPrice: price);
                    ctx.Emit("moveStop", $"stop moved to {F(price.Value)}");
                }
            }

            ctx.Set("done", fire);
        }
    }

    private sealed class ScaleOutNode : NodeBase, IOrderEventObserver
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));
        private readonly TrackedOrder _order = new();

        public ScaleOutNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnOrderEvent(OrderEvent e) => _order.OnOrderEvent(e);

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire)
            {
                Instrument instrument = RequireInstrument();
                Core.Model.Positions.Position? position = Services.Cache.PositionsOpen(instrumentId: instrument.Id, strategyId: Services.StrategyId).FirstOrDefault();
                if (position is not null)
                {
                    Quantity quantity = instrument.MakeQuantity(position.Quantity.Value * P.Dec("fraction", 50m) / 100m);
                    if (quantity.Value > 0m)
                    {
                        MarketOrder order = Services.OrderFactory.Market(instrument.Id, position.IsLong ? OrderSide.Sell : OrderSide.Buy, quantity, reduceOnly: true, tags: ["node:" + NodeId]);
                        _order.Track(order, ctx.Index);
                        Services.SubmitOrder(order);
                        ctx.Emit("scaleOut", $"reducing by {F(quantity.Value)}");
                    }
                }
            }

            ctx.Set("done", fire);
            ctx.Set("filled", _order.PendingFilled);
            _order.PendingFilled = false;
        }
    }

    private sealed class SignalNode : NodeBase
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        public SignalNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire)
            {
                Services.PublishSignal(P.Str("name", "signal"), ctx.Dec("value") ?? P.Dec("value", 1m));
            }

            ctx.Set("done", fire);
        }
    }

    private sealed class NoteNode : NodeBase
    {
        private readonly Edge _edge = new();

        public override void Prime(EvalContext ctx) => _edge.Rising(ctx.Bool("trigger"));

        public NoteNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            bool fire = _edge.Rising(ctx.Bool("trigger"));
            if (fire)
            {
                ctx.Emit("note", P.Str("message", "note"));
            }

            ctx.Set("done", fire);
        }
    }
}
