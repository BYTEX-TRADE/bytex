using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;
using Bytex.Documents.Annotations;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class RiskNodes
{
    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "risk.exit",
            Kind = NodeKind.Risk,
            DisplayName = "Exit & risk",
            Description = "Protects a position: places the stop and the target when it opens, re-prices them from the average entry when the position is added to if asked, trails the stop, applies a time stop, and cleans up when it closes.",
            FaceTemplate = "Stop {stop}, target {target}, trail {trail}",
            Inputs =
            [
                new PortSpec("position", ValueKind.Position, Required: true),
                new PortSpec("stopLevel", ValueKind.Price, Description: "anchor for stop.anchor = level"),
                new PortSpec("targetLevel", ValueKind.Price, Description: "used when target.unit = level"),
                new PortSpec("atr", ValueKind.Series, Description: "for atr offsets"),
            ],
            Outputs =
            [
                new PortSpec("stop", ValueKind.Price), new PortSpec("target", ValueKind.Price), new PortSpec("rMultiple", ValueKind.Series),
                new PortSpec("managing", ValueKind.Bool), new PortSpec("closed", ValueKind.Pulse),
            ],
            Params =
            [
                P.Obj("stop", true, "Stop", "Where the protective stop goes when the position opens.",
                    P.Enum("anchor", "level", "Anchor", "What the stop is measured from.",
                        P.Choice("level", "A price from elsewhere", "The stop starts at the stopLevel input, and the offset then moves it further away in whatever the offset unit says. Without that input the fill price is used instead."),
                        P.Choice("entry", "The fill price", "The stop starts at the price the position was filled at, and the offset moves it away from there in whatever the offset unit says."),
                        P.Choice("percent", "A percentage from the fill", "The stop sits this far from the fill price as a percentage of it, read straight from the offset value; under this anchor the offset unit is not used.", "% of the fill price", "offset")),
                    P.Obj("offset", false, "Offset", "Moves the stop away from the anchor, on the losing side. The unit decides what the number beside it counts.",
                        P.Enum("unit", "atr", "Unit", "What the offset is counted in.",
                            P.Choice("atr", "ATR multiples", "Multiples of the atr input, so the stop stands further off when the market is noisier. Needs the atr input.", "\u00d7 ATR", "value", step: "0.1"),
                            P.Choice("percent", "Percent of the anchor", "A share of the anchor price.", "% of the anchor", "value", step: "0.1"),
                            P.Choice("ticks", "Ticks", "Whole price steps of the instrument.", "ticks", "value"),
                            P.Choice("price", "Price amount", "The distance in the quote currency itself.", "price", "value")),
                        P.Dec("value", 1m, 0m, null, null, "Value"))),
                P.Obj("target", true, "Target", "Where the position takes its profit. The unit decides what the number beside it means, and two of the four do not use it at all.",
                    P.Enum("unit", "r", "Unit", "What the target is measured in.",
                        P.Choice("r", "Multiples of the risk (R)", "The target sits this many entry-to-stop distances beyond the entry: 2 aims to make twice what the stop stands to lose.", "\u00d7 risk (R)", "value", step: "0.1"),
                        P.Choice("percent", "Percent from the entry", "The target sits this far beyond the entry, as a percentage of it.", "% from the entry", "value", step: "0.1"),
                        P.Choice("level", "A price from elsewhere", "The target goes where the targetLevel input puts it, and the number beside it is not used. Without that input the position has no target."),
                        P.Choice("none", "No target", "Nothing is placed to take the profit: the position leaves on the stop, the trail or the time stop. The number beside it is not used.")),
                    P.Dec("value", 2m, 0m, null, null, "Value")),
                P.Obj("trail", false, "Trail", "Tightens the stop once the trade is far enough in profit, and never loosens it.",
                    P.Bool("enabled", true, "Enabled"),
                    P.Dec("afterR", 1m, 0m, null, null, "After R", "\u00d7 risk (R)", "Start trailing once the trade is this many R in profit."),
                    P.Enum("to", "breakeven", "Move to", "Where the stop is moved to once trailing starts.",
                        P.Choice("breakeven", "The entry, plus a lock-in", "The stop moves to the entry price, plus this percentage of it locked in: 0 leaves it exactly at the entry.", "% of the entry locked in", "value", step: "0.1"),
                        P.Choice("atr", "Behind the close, in ATR", "The stop trails this many multiples of the atr input behind the bar close; 0 is read as one multiple. Needs the atr input.", "\u00d7 ATR", "value", step: "0.1"),
                        P.Choice("percent", "Behind the close, in percent", "The stop trails this far behind the bar close, as a percentage of it.", "% of the close", "value", step: "0.1")),
                    P.Dec("value", 0m, 0m, null, null, "Value")),
                P.Enum("anchor", "entry", "When the position is added to", "What happens to the stop and the target when the position grows.",
                    P.Choice("entry", "Leave them where they are", "The stop and the target stay at the prices worked out from the first fill, however much is added to the position afterwards."),
                    P.Choice("averageEntry", "Re-price them from the average", "Every time the position is added to, the stop and the target are worked out again from the new average entry, which is what an averaging or safety-order strategy needs.")),
                P.Int("timeStopBars", 0, 0, 100_000, "Time stop (bars)", null, "Close the position after this many bars; 0 disables."),
            ],
            AmendsOrders = true,
            Factory = ctx => new ExitNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.sizing",
            Kind = NodeKind.Risk,
            DisplayName = "Position size",
            Description = "Computes a quantity from a sizing rule, for order nodes.",
            FaceTemplate = "Size: {sizing}",
            Inputs = [new PortSpec("entry", ValueKind.Price, Description: "defaults to the close"), new PortSpec("stop", ValueKind.Price)],
            Outputs = [new PortSpec("quantity", ValueKind.Quantity), new PortSpec("ok", ValueKind.Bool)],
            Params = [ActionNodes.SizingParam],
            Factory = ctx => new SizingNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.maxPositions",
            Kind = NodeKind.Risk,
            DisplayName = "Max positions",
            Description = "True while this strategy has fewer than N open positions.",
            FaceTemplate = "Fewer than {max} open positions",
            Outputs = [new PortSpec("ok", ValueKind.Bool), new PortSpec("count", ValueKind.Series)],
            Params = [P.Int("max", 1, 1, 100), P.Bool("thisInstrumentOnly", false, "This instrument only")],
            Factory = ctx => new MaxPositionsNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.maxOrders",
            Kind = NodeKind.Risk,
            DisplayName = "Max working orders",
            Description = "True while this strategy has fewer than N working orders on the instrument.",
            FaceTemplate = "Fewer than {max} working orders",
            Outputs = [new PortSpec("ok", ValueKind.Bool), new PortSpec("count", ValueKind.Series)],
            Params = [P.Int("max", 2, 1, 500)],
            Factory = ctx => new MaxOrdersNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.dailyLoss",
            Kind = NodeKind.Risk,
            DisplayName = "Daily loss limit",
            Description = "True until the day's realised loss on the instrument reaches the limit (UTC day).",
            FaceTemplate = "Daily loss under {limit}",
            Outputs = [new PortSpec("ok", ValueKind.Bool), new PortSpec("todayPnl", ValueKind.Series)],
            Params =
            [
                P.Dec("limit", 100m, 0m, null, null, "Limit"),
                P.Enum("unit", "quote", "Unit", "What the limit is counted in.",
                    P.Choice("quote", "Quote currency", "An amount in the quote currency: 100 on BTCUSDT is 100 USDT of realised loss on the day.", "quote currency", "limit", min: "0"),
                    P.Choice("percentOfBalance", "Percent of the free balance", "A share of the free quote balance: 2 stops the strategy once the day has realised a loss of two percent of it.", "% of the free balance", "limit", min: "0", max: "100", step: "0.1")),
            ],
            Factory = ctx => new DailyLossNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.cooldown",
            Kind = NodeKind.Risk,
            DisplayName = "Cooldown after loss",
            Description = "False for N bars after a losing position closes.",
            FaceTemplate = "No entry for {bars} bars after a loss",
            Outputs = [new PortSpec("ok", ValueKind.Bool), new PortSpec("barsLeft", ValueKind.Series)],
            Params = [P.Int("bars", 12, 1, 100_000), P.Bool("afterAnyClose", false, "After any close", "Cool down after every closed position, not only losers.")],
            Factory = ctx => new CooldownNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.exposure",
            Kind = NodeKind.Risk,
            DisplayName = "Max exposure",
            Description = "True while the net exposure on the instrument stays under a notional cap.",
            FaceTemplate = "Exposure under {maxNotional}",
            Outputs = [new PortSpec("ok", ValueKind.Bool), new PortSpec("exposure", ValueKind.Series)],
            Params = [P.Dec("maxNotional", 10_000m, 0m, null, null, "Max notional", "quote")],
            Factory = ctx => new ExposureNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "risk.noEntryNear",
            Kind = NodeKind.Risk,
            DisplayName = "No entry near events",
            Description = "False inside a window around scheduled events that match the instrument's scope. Deterministic feeds only unless the strategy opts in.",
            FaceTemplate = "No entry {before} min before to {after} min after {categories} events",
            Outputs = [new PortSpec("ok", ValueKind.Bool), new PortSpec("minutesToNext", ValueKind.Series)],
            Params =
            [
                P.Int("before", 30, 0, 1440, "Minutes before"), P.Int("after", 30, 0, 1440, "Minutes after"),
                EventNodes.MinSeverityParam,
                P.Str("categories", "", false, "Categories", "Comma-separated; empty means all."),
                P.Bool("includeAiClassified", false, "Include AI-classified events", "In Live this also needs the document's opt-in (modes.live.allowAiAnnotationConditions)."),
            ],
            Factory = ctx => new NoEntryNearNode(ctx),
        };
    }

    private sealed class ExitNode : NodeBase, IPositionEventObserver, IOrderEventObserver, IStatefulNode
    {
        private PositionId? _managed;
        private decimal _entry;
        private decimal _risk;
        private decimal? _stop;
        private decimal? _target;
        private ClientOrderId? _stopOrder;
        private ClientOrderId? _targetOrder;
        private long _openedAt;
        private bool _pendingClosed;
        private bool _trailing;
        private bool _resumed;
        private UnixNanos _resumedAt;

        public ExitNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPositionEvent(PositionEvent e)
        {
            if (_managed is { } id && e is PositionClosed closed && closed.PositionId == id)
            {
                _pendingClosed = true;
            }
        }

        /// <summary>
        /// A protective order whose resize the venue refused would otherwise be left guarding less than the position
        /// it is there for - a stop still open, still at the right price, and sized for a position that has since
        /// grown. Nothing looks wrong, which is what makes it worth reacting to.
        /// <para>
        /// This was found on KuCoin's perpetual futures, where the venue cannot amend an order at all, so every
        /// resize after a scale-in was refused. But it is not a venue quirk: a resize refused for any reason at all -
        /// a rate limit, a venue having a moment, an order that filled while the amend was in flight - left the same
        /// half-guarded position on every venue, and nothing here was watching for it.
        /// </para>
        /// </summary>
        public void OnOrderEvent(OrderEvent e)
        {
            if (e is not OrderModifyRejected rejected)
            {
                return;
            }

            bool stop = _stopOrder == rejected.ClientOrderId;
            if (!stop && _targetOrder != rejected.ClientOrderId)
            {
                return;
            }

            ReplaceUndersizedExit(rejected.ClientOrderId, stop, rejected.Reason);
        }

        /// <summary>
        /// Puts a correctly sized protective order in place of one the venue would not resize.
        /// <para>
        /// The new order is placed BEFORE the old one is cancelled, so the position is briefly over-covered rather
        /// than ever under-covered. Both orders reduce only, so the venue closes at most the position however many
        /// are live - which makes the overlap harmless, where a gap would not be. Cancelling first and placing
        /// second would leave a leveraged position unguarded for a round trip to fix a partial guard, which is not
        /// obviously an improvement on the partial guard.
        /// </para>
        /// <para>
        /// If the replacement is refused too, the old order stays. That is still under-covering, but it is no worse
        /// than before and it is said out loud rather than discovered.
        /// </para>
        /// </summary>
        private void ReplaceUndersizedExit(ClientOrderId rejectedId, bool stop, string reason)
        {
            if (Services.Cache.Order(rejectedId) is not { IsOpen: true } order
                || _managed is not { } positionId
                || Services.Cache.Position(positionId) is not { IsOpen: true } position
                || Services.Cache.Instrument(order.InstrumentId) is not { } instrument)
            {
                return;
            }

            Quantity wanted = new(order.FilledQuantity.Value + position.Quantity.Value, position.Quantity.Precision);
            if (wanted.Value <= order.Quantity.Value)
            {
                // Over-covering rather than under-covering, which reduce-only already caps: the venue will not close
                // more than the position. Nothing to repair, and replacing it would risk the position for tidiness.
                Services.Log.LogInformation(
                    "Node {Node}: the venue would not resize the {Which} down to {Wanted}; it covers more than the position and reduce-only caps it",
                    NodeId,
                    stop ? "stop" : "target",
                    F(wanted.Value));
                return;
            }

            OrderSide exitSide = position.IsLong ? OrderSide.Sell : OrderSide.Buy;
            string[] tags = [stop ? "exit:stop" : "exit:target", "node:" + NodeId];

            Order replacement;
            if (stop)
            {
                Price trigger = order.TriggerPrice ?? instrument.MakePrice(_stop ?? position.AvgPxOpen);
                replacement = Services.OrderFactory.StopMarket(instrument.Id, exitSide, wanted, trigger, reduceOnly: true, tags: tags);
            }
            else
            {
                Price limit = order.Price ?? instrument.MakePrice(_target ?? position.AvgPxOpen);
                replacement = Services.OrderFactory.Limit(instrument.Id, exitSide, wanted, limit, reduceOnly: true, tags: tags);
            }

            Services.SubmitOrder(replacement);

            if (stop)
            {
                _stopOrder = replacement.ClientOrderId;
            }
            else
            {
                _targetOrder = replacement.ClientOrderId;
            }

            Services.CancelOrder(order);

            // A warning rather than a note: the venue refused something this node needed, and although the position
            // is covered again a person tuning a strategy on such a venue should see that it is happening.
            Services.Log.LogWarning(
                "Node {Node}: the venue refused to resize the {Which} {Refused} to {Wanted} ({Reason}); {Replacement} was placed at that size and the refused one cancelled, so the whole position stays covered",
                NodeId,
                stop ? "stop" : "target",
                rejectedId.Value,
                F(wanted.Value),
                reason,
                replacement.ClientOrderId.Value);
        }

        public JsonElement SaveState() => JsonSerializer.SerializeToElement(new
        {
            managed = _managed?.Value,
            entry = _entry,
            risk = _risk,
            stop = _stop,
            target = _target,
            stopOrder = _stopOrder?.Value,
            targetOrder = _targetOrder?.Value,
            openedAt = _openedAt,
            trailing = _trailing,
        });

        public void LoadState(JsonElement s)
        {
            _managed = s.TryGetProperty("managed", out JsonElement m) && m.ValueKind == JsonValueKind.String ? new PositionId(m.GetString()!) : null;
            _entry = s.TryGetProperty("entry", out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : 0m;
            _risk = s.TryGetProperty("risk", out JsonElement r) && r.ValueKind == JsonValueKind.Number ? r.GetDecimal() : 0m;
            _stop = s.TryGetProperty("stop", out JsonElement st) && st.ValueKind == JsonValueKind.Number ? st.GetDecimal() : null;
            _target = s.TryGetProperty("target", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetDecimal() : null;
            _stopOrder = s.TryGetProperty("stopOrder", out JsonElement so) && so.ValueKind == JsonValueKind.String ? new ClientOrderId(so.GetString()!) : null;
            _targetOrder = s.TryGetProperty("targetOrder", out JsonElement to) && to.ValueKind == JsonValueKind.String ? new ClientOrderId(to.GetString()!) : null;
            _openedAt = s.TryGetProperty("openedAt", out JsonElement oa) && oa.ValueKind == JsonValueKind.Number ? oa.GetInt64() : 0;
            _trailing = s.TryGetProperty("trailing", out JsonElement tr) && tr.ValueKind == JsonValueKind.True;
            _resumed = _managed is not null;
            _resumedAt = Services.Clock.Timestamp;
        }

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            if (_resumed)
            {
                Resume(ctx, instrument);
            }

            PositionId? input = ctx.Position("position");
            Position? position = input is { } pid ? Services.Cache.Position(pid) : null;

            if (_managed is null && position is { IsOpen: true })
            {
                Attach(ctx, instrument, position);
            }

            if (_managed is { } managedId)
            {
                Position? managed = Services.Cache.Position(managedId);
                if (managed is null || managed.IsClosed || _pendingClosed)
                {
                    Release(ctx, instrument);
                }
                else
                {
                    Manage(ctx, instrument, managed);
                }
            }

            ctx.Set("managing", _managed is not null);
            ctx.Set("closed", _pendingClosed);
            if (_stop is { } stop)
            {
                ctx.Set("stop", stop);
            }

            if (_target is { } target)
            {
                ctx.Set("target", target);
            }

            _pendingClosed = false;
        }

        /// <summary>
        /// The node was managing a position when the process stopped. What it comes back to is not what it saved: the
        /// exit orders resting at the venue are reported under their own ids, and a position rebuilt from a venue report
        /// may carry a new id. Both are found again here, by instrument and by the node's own tag, so the stop goes on
        /// trailing. A position that has come back without a stop at the venue is protected again at once, because an
        /// unprotected position is the one thing this node exists to prevent.
        /// </summary>
        private void Resume(EvalContext ctx, Instrument instrument)
        {
            _resumed = false;
            // Only a position that was already open when the state was read is the one this node was protecting. One that
            // this very run opened belongs to the entry node that opened it, and gets its own stop from its own fill.
            Position? position = Services.Cache.PositionsOpen(instrumentId: instrument.Id, strategyId: Services.StrategyId)
                .FirstOrDefault(p => p.TsOpened <= _resumedAt);
            if (position is null)
            {
                ctx.Emit("exit", $"the position {_managed} it was protecting is gone; nothing to resume");
                _managed = null;
                _stopOrder = null;
                _targetOrder = null;
                _stop = null;
                _target = null;
                _trailing = false;
                return;
            }

            _managed = position.Id;
            if (_entry <= 0m)
            {
                _entry = position.AvgPxOpen;
            }

            List<Order> exits = Services.Cache.OrdersOpen(instrumentId: instrument.Id, strategyId: Services.StrategyId)
                .Where(o => o.IsReduceOnly && (o.Tags.Count == 0 || o.Tags.Contains("node:" + NodeId, StringComparer.Ordinal))).ToList();
            if (_stopOrder is not { } savedStop || Services.Cache.Order(savedStop) is not { IsOpen: true })
            {
                _stopOrder = exits.FirstOrDefault(o => o.Tags.Contains("exit:stop", StringComparer.Ordinal) || o.Type is OrderType.StopMarket or OrderType.StopLimit)?.ClientOrderId;
            }

            if (_targetOrder is not { } savedTarget || Services.Cache.Order(savedTarget) is not { IsOpen: true })
            {
                _targetOrder = exits.FirstOrDefault(o => o.Tags.Contains("exit:target", StringComparer.Ordinal) || o.Type == OrderType.Limit)?.ClientOrderId;
            }

            if (_stopOrder is { } stopId && Services.Cache.Order(stopId)?.TriggerPrice is { } trigger)
            {
                _stop = trigger.Value;
            }

            if (_targetOrder is { } targetId && Services.Cache.Order(targetId)?.Price is { } limit)
            {
                _target = limit.Value;
            }

            if (_risk <= 0m && _stop is { } stopPrice)
            {
                _risk = Math.Abs(_entry - stopPrice);
            }

            _openedAt = ctx.Index;
            OrderSide exitSide = position.IsLong ? OrderSide.Sell : OrderSide.Buy;
            bool replaced = false;
            if (_stopOrder is null && _stop is { } wanted)
            {
                StopMarketOrder stop = Services.OrderFactory.StopMarket(instrument.Id, exitSide, position.Quantity, instrument.MakePrice(wanted), reduceOnly: true, tags: ["exit:stop", "node:" + NodeId]);
                Services.SubmitOrder(stop);
                _stopOrder = stop.ClientOrderId;
                replaced = true;
            }

            if (_targetOrder is null && _target is { } wantedTarget)
            {
                LimitOrder target = Services.OrderFactory.Limit(instrument.Id, exitSide, position.Quantity, instrument.MakePrice(wantedTarget), reduceOnly: true, tags: ["exit:target", "node:" + NodeId]);
                Services.SubmitOrder(target);
                _targetOrder = target.ClientOrderId;
                replaced = true;
            }

            ctx.Emit("exit", $"resumed protecting {position.Id}: entry {F(_entry)}{(_stop is { } s ? $", stop {F(s)}" : ", no stop")}{(_target is { } t ? $", target {F(t)}" : string.Empty)}{(replaced ? ", missing exit orders placed again" : string.Empty)}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stopOrder"] = _stopOrder?.Value ?? string.Empty,
                    ["targetOrder"] = _targetOrder?.Value ?? string.Empty,
                    ["risk"] = F(_risk),
                });
        }

        private void Attach(EvalContext ctx, Instrument instrument, Position position)
        {
            _managed = position.Id;
            _entry = position.AvgPxOpen;
            _openedAt = ctx.Index;
            _trailing = false;
            Levels(ctx, instrument, position);

            OrderSide exitSide = position.IsLong ? OrderSide.Sell : OrderSide.Buy;
            Quantity quantity = position.Quantity;
            StopMarketOrder stop = Services.OrderFactory.StopMarket(instrument.Id, exitSide, quantity, instrument.MakePrice(_stop!.Value), reduceOnly: true, tags: ["exit:stop", "node:" + NodeId]);
            Services.SubmitOrder(stop);
            _stopOrder = stop.ClientOrderId;
            if (_target is { } targetPrice)
            {
                LimitOrder target = Services.OrderFactory.Limit(instrument.Id, exitSide, quantity, instrument.MakePrice(targetPrice), reduceOnly: true, tags: ["exit:target", "node:" + NodeId]);
                Services.SubmitOrder(target);
                _targetOrder = target.ClientOrderId;
            }

            ctx.Emit("exit", $"protecting {position.Id}: stop {F(_stop.Value)}{(_target is { } t ? $", target {F(t)}" : string.Empty)}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["entry"] = F(_entry), ["risk"] = F(_risk) });
        }

        /// <summary>
        /// Where the stop and the target belong for the entry the node currently holds. Called when a position opens
        /// and again when its average moves, so a re-priced level is computed exactly as the first one was.
        /// </summary>
        private void Levels(EvalContext ctx, Instrument instrument, Position position)
        {
            bool isLong = position.IsLong;
            int away = isLong ? -1 : 1;

            NodeParams stopParams = P.Obj("stop");
            decimal anchor = stopParams.Str("anchor", "level") switch
            {
                "entry" => _entry,
                "percent" => _entry * (1m + away * stopParams.Obj("offset").Dec("value", 1m) / 100m),
                _ => ctx.Dec("stopLevel") ?? _entry,
            };
            decimal stopValue = stopParams.Str("anchor", "level") == "percent" ? anchor : ActionNodes.ApplyOffset(stopParams.Obj("offset"), instrument, anchor, ctx.Dec("atr"), away);
            if (isLong ? stopValue >= _entry : stopValue <= _entry)
            {
                // A stop on the wrong side of the entry would fill at once; fall back to one ATR (or 1%) away.
                decimal fallback = ctx.Dec("atr") ?? _entry * 0.01m;
                ctx.Emit("exit", $"stop {F(instrument.MakePrice(stopValue).Value)} is not {(isLong ? "below" : "above")} the entry {F(_entry)}; placed {F(fallback)} away from the entry instead");
                stopValue = _entry + away * fallback;
            }

            _stop = instrument.MakePrice(stopValue).Value;
            _risk = Math.Abs(_entry - _stop.Value);

            NodeParams targetParams = P.Obj("target");
            string unit = targetParams.Str("unit", "r");
            decimal value = targetParams.Dec("value", 2m);
            decimal? targetValue = unit switch
            {
                "none" => null,
                "percent" => _entry * (1m - away * value / 100m),
                "level" => ctx.Dec("targetLevel"),
                _ => _entry - away * _risk * value,
            };
            _target = targetValue is { } tv ? instrument.MakePrice(tv).Value : null;
            if (_target is { } wanted && (isLong ? wanted <= _entry : wanted >= _entry))
            {
                // A target that is not beyond the entry would fill at once, at the entry price: a round trip that only pays commission.
                ctx.Emit("exit", $"target {F(wanted)} is not {(isLong ? "above" : "below")} the entry {F(_entry)}; no target placed, the stop stays");
                _target = null;
            }

        }

        /// <summary>
        /// The position was added to and the node is anchored to the average: both levels are computed again from the
        /// new average and the resting orders are moved to them. Resizing alone is not enough - a stop left at the
        /// first fill's distance is further away in R than the document asked for, and a target left behind the new
        /// average can no longer be reached, or is reached at a loss.
        /// </summary>
        private void Reprice(EvalContext ctx, Instrument instrument, Position position)
        {
            decimal previous = _entry;
            _entry = position.AvgPxOpen;
            _trailing = false;
            Levels(ctx, instrument, position);

            if (_stopOrder is { } stopId && Services.Cache.Order(stopId) is { IsOpen: true } stopOrder && _stop is { } stopPrice)
            {
                Services.ModifyOrder(stopOrder, triggerPrice: instrument.MakePrice(stopPrice));
            }

            if (_targetOrder is { } targetId && Services.Cache.Order(targetId) is { IsOpen: true } targetOrder)
            {
                if (_target is { } targetPrice)
                {
                    Services.ModifyOrder(targetOrder, price: instrument.MakePrice(targetPrice));
                }
                else
                {
                    // The recomputed target is no longer beyond the average, so it would fill at once: it goes.
                    Services.CancelOrder(targetOrder);
                    _targetOrder = null;
                }
            }
            else if (_target is { } placed && _targetOrder is null)
            {
                OrderSide exitSide = position.IsLong ? OrderSide.Sell : OrderSide.Buy;
                LimitOrder target = Services.OrderFactory.Limit(instrument.Id, exitSide, position.Quantity, instrument.MakePrice(placed), reduceOnly: true, tags: ["exit:target", "node:" + NodeId]);
                Services.SubmitOrder(target);
                _targetOrder = target.ClientOrderId;
            }

            ctx.Emit("exit", $"average entry moved {F(previous)} to {F(_entry)}: stop {F(_stop!.Value)}{(_target is { } t2 ? $", target {F(t2)}" : ", no target")}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["entry"] = F(_entry), ["risk"] = F(_risk) });
        }

        /// <summary>After a scale-out or a partial exit the stop and the target are resized to what is left of the position.</summary>
        private void Resize(EvalContext ctx, Position position)
        {
            bool resized = false;
            foreach (ClientOrderId? id in new[] { _stopOrder, _targetOrder })
            {
                if (id is not { } orderId || Services.Cache.Order(orderId) is not { IsOpen: true } order)
                {
                    continue;
                }

                Quantity wanted = new(order.FilledQuantity.Value + position.Quantity.Value, position.Quantity.Precision);
                if (wanted != order.Quantity)
                {
                    Services.ModifyOrder(order, quantity: wanted);
                    resized = true;
                }
            }

            if (resized)
            {
                ctx.Emit("exit", $"exit orders resized to {F(position.Quantity.Value)} after the position changed");
            }
        }

        private void Manage(EvalContext ctx, Instrument instrument, Position position)
        {
            Resize(ctx, position);
            if (P.Str("anchor", "entry") == "averageEntry" && position.AvgPxOpen != _entry)
            {
                Reprice(ctx, instrument, position);
            }

            decimal close = ctx.Bar.Close.Value;
            bool isLong = position.IsLong;
            decimal profit = isLong ? close - _entry : _entry - close;
            decimal r = _risk == 0m ? 0m : profit / _risk;
            ctx.Set("rMultiple", r);

            int timeStop = P.Int("timeStopBars", 0);
            if (timeStop > 0 && ctx.Index - _openedAt >= timeStop)
            {
                Services.CloseAllPositions(instrument.Id);
                ctx.Emit("exit", $"time stop after {timeStop} bars");
                return;
            }

            NodeParams trail = P.Obj("trail");
            if (!trail.Bool("enabled", true) || _stopOrder is not { } stopId)
            {
                return;
            }

            if (r < trail.Dec("afterR", 1m))
            {
                return;
            }

            decimal value = trail.Dec("value", 0m);
            decimal candidate = trail.Str("to", "breakeven") switch
            {
                "atr" => close - (isLong ? 1 : -1) * (ctx.Dec("atr") ?? 0m) * (value == 0m ? 1m : value),
                "percent" => close * (1m - (isLong ? 1 : -1) * value / 100m),
                _ => _entry * (1m + (isLong ? 1 : -1) * value / 100m),
            };
            candidate = instrument.MakePrice(candidate).Value;
            bool improves = _stop is { } current && (isLong ? candidate > current : candidate < current);
            if (!improves)
            {
                return;
            }

            Order? stopOrder = Services.Cache.Order(stopId);
            if (stopOrder is { IsOpen: true })
            {
                Services.ModifyOrder(stopOrder, triggerPrice: instrument.MakePrice(candidate));
                _stop = candidate;
                _trailing = true;
                ctx.Emit("trail", $"stop moved to {F(candidate)} at {F(r)}R");
            }
        }

        private void Release(EvalContext ctx, Instrument instrument)
        {
            foreach (ClientOrderId? id in new[] { _stopOrder, _targetOrder })
            {
                if (id is { } orderId && Services.Cache.Order(orderId) is { IsOpen: true } order)
                {
                    Services.CancelOrder(order);
                }
            }

            ctx.Emit("exit", $"position {_managed} closed; remaining exit orders cancelled");
            _pendingClosed = true;
            _managed = null;
            _stopOrder = null;
            _targetOrder = null;
            _stop = null;
            _target = null;
            _trailing = false;
        }
    }

    private sealed class SizingNode : NodeBase
    {
        public SizingNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            Instrument instrument = RequireInstrument();
            NodeParams sizing = P.Obj("sizing");
            decimal entry = ctx.Dec("entry") ?? ctx.Bar.Close.Value;
            Quantity? quantity = Sizing.Compute(Services, instrument, sizing, entry, ctx.Dec("stop"), m => ctx.Emit("sizing", m));
            ctx.Set("ok", quantity is not null);
            if (quantity is { } q)
            {
                ctx.Set("quantity", q.Value);
            }
        }
    }

    private sealed class MaxPositionsNode : NodeBase
    {
        public MaxPositionsNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            int count = P.Bool("thisInstrumentOnly", false)
                ? Services.Cache.PositionsOpenCount(instrumentId: Ctx.InstrumentId, strategyId: Services.StrategyId)
                : Services.Cache.PositionsOpenCount(strategyId: Services.StrategyId);
            ctx.Set("count", (decimal)count);
            ctx.Set("ok", count < P.Int("max", 1));
        }
    }

    private sealed class MaxOrdersNode : NodeBase
    {
        public MaxOrdersNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            int count = Services.Cache.OrdersOpenCount(instrumentId: Ctx.InstrumentId, strategyId: Services.StrategyId);
            ctx.Set("count", (decimal)count);
            ctx.Set("ok", count < P.Int("max", 2));
        }
    }

    private sealed class DailyLossNode : NodeBase
    {
        private DateOnly? _day;
        private decimal _baseline;

        public DailyLossNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            decimal realized = Services.Portfolio.RealizedPnl(Ctx.InstrumentId)?.Amount ?? 0m;
            DateOnly day = DateOnly.FromDateTime(BarMath.Time(ctx.Bar).UtcDateTime);
            if (_day != day)
            {
                _day = day;
                _baseline = realized;
            }

            decimal today = realized - _baseline;
            decimal limit = P.Dec("limit", 100m);
            if (P.Str("unit", "quote") == "percentOfBalance" && Instrument is { } instrument && Sizing.FreeBalance(Services, instrument) is { } balance)
            {
                limit = balance * limit / 100m;
            }

            ctx.Set("todayPnl", today);
            ctx.Set("ok", today > -limit);
        }
    }

    private sealed class CooldownNode : NodeBase, IPositionEventObserver
    {
        private long? _until;
        private bool _pendingTrigger;

        public CooldownNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPositionEvent(PositionEvent e)
        {
            if (e is PositionClosed closed && closed.InstrumentId == Ctx.InstrumentId && (P.Bool("afterAnyClose", false) || closed.RealizedPnl.Amount < 0m))
            {
                _pendingTrigger = true;
            }
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (_pendingTrigger)
            {
                _until = ctx.Index + P.Int("bars", 12);
                _pendingTrigger = false;
                ctx.Emit("cooldown", $"cooling down for {P.Int("bars", 12)} bars");
            }

            long left = _until is { } u ? Math.Max(0, u - ctx.Index) : 0;
            ctx.Set("barsLeft", (decimal)left);
            ctx.Set("ok", left == 0);
        }
    }

    private sealed class ExposureNode : NodeBase
    {
        public ExposureNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            decimal exposure = Math.Abs(Services.Portfolio.NetExposure(Ctx.InstrumentId)?.Amount ?? 0m);
            ctx.Set("exposure", exposure);
            ctx.Set("ok", exposure < P.Dec("maxNotional", 10_000m));
        }
    }

    internal static AnnotationSeverity Severity(string s) => s switch
    {
        "info" => AnnotationSeverity.Info,
        "warning" => AnnotationSeverity.Warning,
        "critical" => AnnotationSeverity.Critical,
        _ => AnnotationSeverity.Notice,
    };

    internal static bool AllowAi(NodeBuildContext ctx, bool includeAi) =>
        includeAi && (ctx.Services.Environment != TradingEnvironment.Live || ctx.Document.Modes.Live.AllowAiAnnotationConditions);

    private sealed class NoEntryNearNode : NodeBase
    {
        public NoEntryNearNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            Instrument? instrument = Instrument;
            long now = ctx.Bar.TsEvent.Value;
            long before = P.Int("before", 30) * UnixNanos.NanosPerMinute;
            long after = P.Int("after", 30) * UnixNanos.NanosPerMinute;
            AnnotationSeverity minSeverity = Severity(P.Str("minSeverity", "notice"));
            HashSet<string> categories = P.Str("categories", string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool allowAi = AllowAi(Ctx, P.Bool("includeAiClassified", false));

            bool near = false;
            decimal? minutesToNext = null;
            foreach (Annotation a in Services.Annotations)
            {
                if (a.Severity < minSeverity || (categories.Count > 0 && !categories.Contains(a.Category)) || (a.IsAiClassified && !allowAi))
                {
                    continue;
                }

                if (instrument is not null && !a.AppliesTo(instrument))
                {
                    continue;
                }

                long start = a.TsEvent.Value - before;
                long end = (a.TsEnd?.Value ?? a.TsEvent.Value) + after;
                if (now >= start && now <= end)
                {
                    near = true;
                }

                if (a.TsEvent.Value >= now)
                {
                    decimal minutes = (decimal)(a.TsEvent.Value - now) / UnixNanos.NanosPerMinute;
                    minutesToNext = minutesToNext is { } m ? Math.Min(m, minutes) : minutes;
                }
            }

            ctx.Set("ok", !near);
            if (minutesToNext is { } mtn)
            {
                ctx.Set("minutesToNext", mtn);
            }
        }
    }
}
