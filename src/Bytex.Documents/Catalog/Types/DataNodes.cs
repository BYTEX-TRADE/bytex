using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class DataNodes
{
    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "data.bars",
            Kind = NodeKind.Data,
            DisplayName = "Bars",
            Description = "A bar stream from the document's bar types. Every bar of the type updates the node's outputs.",
            FaceTemplate = "Bars {candleSeries}",
            Params = [P.CandleSeries()],
            Outputs =
            [
                new PortSpec("bars", ValueKind.Bars, Label: "bars"),
                new PortSpec("open", ValueKind.Price), new PortSpec("high", ValueKind.Price), new PortSpec("low", ValueKind.Price),
                new PortSpec("close", ValueKind.Price), new PortSpec("volume", ValueKind.Quantity), new PortSpec("typical", ValueKind.Price),
            ],
            Factory = ctx => new BarsNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.position",
            Kind = NodeKind.Data,
            DisplayName = "Position",
            Description = "What this strategy holds in the instrument, and where it last got out. Readable while flat, so a re-entry rule can measure from the previous exit.",
            FaceTemplate = "The position this strategy holds",
            Inputs = [new PortSpec("stop", ValueKind.Price, Label: "Risk per unit from this level")],
            Outputs =
            [
                new PortSpec("open", ValueKind.Bool, Label: "A position is open"),
                new PortSpec("isLong", ValueKind.Bool), new PortSpec("isShort", ValueKind.Bool),
                new PortSpec("quantity", ValueKind.Quantity), new PortSpec("avgEntry", ValueKind.Price),
                new PortSpec("unrealizedPct", ValueKind.Series, Label: "Unrealized, percent"),
                new PortSpec("unrealizedR", ValueKind.Series, Label: "Unrealized, in R"),
                new PortSpec("adds", ValueKind.Series, Label: "Fills that grew it"),
                new PortSpec("barsHeld", ValueKind.Series),
                new PortSpec("lastExitPrice", ValueKind.Price), new PortSpec("lastExitBarsAgo", ValueKind.Series),
                new PortSpec("lastExitSecondsAgo", ValueKind.Series, Label: "Seconds since the last exit", Description: "How long ago this strategy last got out, in seconds. Bars-ago counts evaluations and so says nothing across a gap in the data or on a document evaluated on ticks; this counts time."),
            ],
            Factory = ctx => new PositionNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.price",
            Kind = NodeKind.Data,
            DisplayName = "Price now",
            Description = "The price this evaluation is about: the close of the bar that just closed, or - on a document evaluated on ticks - the price of the tick that started the frame. Reading it is how a condition compares a live price against figures that only move at the bar close.",
            FaceTemplate = "The price now",
            Params = [],
            Outputs =
            [
                new PortSpec("value", ValueKind.Price, Label: "price"),
                new PortSpec("isTick", ValueKind.Bool, Description: "true while this frame was started by a tick rather than by a bar closing"),
            ],
            Factory = ctx => new PriceNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.quotes",
            Kind = NodeKind.Data,
            DisplayName = "Quotes",
            Description = "Top of book for the instrument, read from the cache at each evaluation.",
            FaceTemplate = "Quotes for {instrument}",
            Params = [P.Instrument()],
            Outputs = [new PortSpec("bid", ValueKind.Price), new PortSpec("ask", ValueKind.Price), new PortSpec("mid", ValueKind.Price), new PortSpec("spread", ValueKind.Series)],
            Factory = ctx => new QuotesNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.trades",
            Kind = NodeKind.Data,
            DisplayName = "Trades",
            Description = "The last trade for the instrument.",
            FaceTemplate = "Last trade for {instrument}",
            Params = [P.Instrument()],
            Outputs = [new PortSpec("last", ValueKind.Price), new PortSpec("size", ValueKind.Quantity)],
            Factory = ctx => new TradesNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.book",
            Kind = NodeKind.Data,
            DisplayName = "Order book",
            Description = "Best bid and ask from the maintained order book, plus the size imbalance at the top.",
            FaceTemplate = "Order book for {instrument}",
            Params = [P.Instrument()],
            Outputs = [new PortSpec("bestBid", ValueKind.Price), new PortSpec("bestAsk", ValueKind.Price), new PortSpec("imbalance", ValueKind.Series, Description: "(bidSize − askSize) / (bidSize + askSize)")],
            Factory = ctx => new BookNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.markPrice",
            Kind = NodeKind.Data,
            DisplayName = "Mark price",
            Description = "The venue's mark price for a derivative.",
            FaceTemplate = "Mark price of {instrument}",
            Params = [P.Instrument()],
            Outputs = [new PortSpec("mark", ValueKind.Price)],
            Factory = ctx => new MarkPriceNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "data.funding",
            Kind = NodeKind.Data,
            DisplayName = "Funding rate",
            Description = "The current funding rate of a perpetual and the hours until the next settlement.",
            FaceTemplate = "Funding rate of {instrument}",
            Params = [P.Instrument()],
            Outputs = [new PortSpec("rate", ValueKind.Series, Description: "rate as a percentage, e.g. 0.05"), new PortSpec("hoursToNext", ValueKind.Series)],
            Factory = ctx => new FundingNode(ctx),
        };
    }

    /// <summary>
    /// The strategy's own position in the instrument, and the exit before it. Everything here is read from the cache
    /// each bar except what the cache cannot remember once a position is closed - the price it closed at and when -
    /// which the node keeps in its own state, because a rule that re-enters after an exit has to measure from it.
    /// </summary>
    private sealed class PositionNode : NodeBase, IPositionEventObserver, IStatefulNode
    {
        private decimal? _lastExitPrice;
        private long _lastExitIndex = -1;
        private long _lastExitTsNanos = -1;
        private long _openedAtIndex = -1;
        private int _adds;
        private PositionId? _tracked;
        private decimal? _pendingExitPrice;
        private long? _pendingExitTsNanos;

        public PositionNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnPositionEvent(PositionEvent e)
        {
            if (e.MarketKey != Ctx.MarketKey || e.StrategyId != Services.StrategyId)
            {
                return;
            }

            switch (e)
            {
                case PositionOpened:
                    _tracked = e.PositionId;
                    _adds = 1;
                    break;
                case PositionChanged changed when changed.PositionId == _tracked:
                    // A fill that grew the position; one that reduced it leaves the count alone.
                    if (changed.Quantity.Value > changed.PeakQuantity.Value - changed.Quantity.Value)
                    {
                        _adds++;
                    }

                    break;
                case PositionClosed closed when closed.PositionId == _tracked:
                    _pendingExitPrice = closed.AvgPxClose ?? closed.LastPx.Value;

                    // The venue's own closing time when it gave one, rather than the moment this node heard about it.
                    _pendingExitTsNanos = (closed.TsClosed ?? closed.EventTime).Value;
                    _tracked = null;
                    break;
            }
        }

        public JsonElement SaveState() => JsonSerializer.SerializeToElement(new
        {
            lastExitPrice = _lastExitPrice,
            lastExitIndex = _lastExitIndex,
            lastExitTs = _lastExitTsNanos,
            openedAt = _openedAtIndex,
            adds = _adds,
        });

        public void LoadState(JsonElement state)
        {
            if (state.TryGetProperty("lastExitPrice", out JsonElement price) && price.ValueKind == JsonValueKind.Number)
            {
                _lastExitPrice = price.GetDecimal();
            }

            _lastExitIndex = state.TryGetProperty("lastExitIndex", out JsonElement exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt64() : -1;
            _lastExitTsNanos = state.TryGetProperty("lastExitTs", out JsonElement exitTs) && exitTs.ValueKind == JsonValueKind.Number ? exitTs.GetInt64() : -1;
            _openedAtIndex = state.TryGetProperty("openedAt", out JsonElement opened) && opened.ValueKind == JsonValueKind.Number ? opened.GetInt64() : -1;
            _adds = state.TryGetProperty("adds", out JsonElement adds) && adds.ValueKind == JsonValueKind.Number ? adds.GetInt32() : 0;
        }

        public override void Evaluate(EvalContext ctx)
        {
            if (_pendingExitPrice is { } exited)
            {
                _lastExitPrice = exited;
                _lastExitIndex = ctx.Index;
                _lastExitTsNanos = _pendingExitTsNanos ?? Services.Clock.Timestamp.Value;
                _openedAtIndex = -1;
                _adds = 0;
                _pendingExitPrice = null;
                _pendingExitTsNanos = null;
            }

            Position? position = Services.Cache.PositionsOpen(marketKey: Ctx.MarketKey, strategyId: Services.StrategyId).FirstOrDefault();
            bool open = position is { IsOpen: true };
            ctx.Set("open", open);
            ctx.Set("isLong", open && position!.IsLong);
            ctx.Set("isShort", open && position!.IsShort);

            if (open)
            {
                if (_openedAtIndex < 0)
                {
                    // A position this node never saw open: adopted at start, or restored with the node's state gone.
                    _openedAtIndex = ctx.Index;
                    _adds = Math.Max(_adds, 1);
                    _tracked ??= position!.Id;
                }

                Price last = ctx.Bar.Close;
                decimal entry = position!.AvgPxOpen;
                ctx.Set("quantity", position.Quantity);
                ctx.Set("avgEntry", Ctx.Instrument?.MakePrice(entry) ?? last);
                ctx.Set("adds", (decimal)_adds);
                ctx.Set("barsHeld", (decimal)(ctx.Index - _openedAtIndex));
                if (entry != 0m)
                {
                    decimal move = (last.Value - entry) / entry * Scales.Percent;
                    ctx.Set("unrealizedPct", position.IsLong ? move : -move);
                }

                if (ctx.Dec("stop") is { } stop && Math.Abs(entry - stop) > 0m)
                {
                    decimal risk = Math.Abs(entry - stop);
                    decimal gain = position.IsLong ? last.Value - entry : entry - last.Value;
                    ctx.Set("unrealizedR", gain / risk);
                }
            }

            if (_lastExitPrice is { } exitPrice)
            {
                ctx.Set("lastExitPrice", Ctx.Instrument?.MakePrice(exitPrice) ?? ctx.Bar.Close);
                ctx.Set("lastExitBarsAgo", (decimal)(ctx.Index - _lastExitIndex));

                // Read from the clock and not from the bar, so a frame started by a tick counts the time that has
                // actually passed rather than the age of the last bar to close. Never negative: a state restored
                // from a run that ended later than this one would otherwise read as an exit in the future.
                if (_lastExitTsNanos >= 0)
                {
                    long elapsed = Services.Clock.Timestamp.Value - _lastExitTsNanos;
                    ctx.Set("lastExitSecondsAgo", elapsed <= 0 ? 0m : (decimal)elapsed / UnixNanos.NanosPerSecond);
                }
            }
        }
    }

    /// <summary>
    /// The price the frame is about (R13.7). Deliberately not "the last trade" or "the mid": it is whatever started
    /// this evaluation, which on a bar close is that bar's close and inside a bar is the tick itself. A document
    /// evaluated at the bar close therefore reads exactly the bar's close from it, every time.
    /// </summary>
    private sealed class PriceNode : NodeBase
    {
        public PriceNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            ctx.Set("value", Ctx.Instrument?.MakePrice(ctx.Last).Value ?? ctx.Last);
            ctx.Set("isTick", !ctx.IsBarClose);
        }
    }

    private sealed class BarsNode : NodeBase, IBarObserver
    {
        private Bar? _last;

        public BarsNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public void OnBar(Bar bar) => _last = bar;

        public override void Evaluate(EvalContext ctx)
        {
            Bar? bar = _last ?? (Ctx.SourceCandleSeries is { } bt && ctx.Bar.CandleSeries == bt ? ctx.Bar : null);
            if (bar is null)
            {
                return;
            }

            Bar b = bar.Value;
            ctx.Set("bars", b);
            ctx.Set("open", b.Open.Value);
            ctx.Set("high", b.High.Value);
            ctx.Set("low", b.Low.Value);
            ctx.Set("close", b.Close.Value);
            ctx.Set("volume", b.Volume.Value);
            ctx.Set("typical", BarMath.Typical(b));
        }
    }

    private sealed class QuotesNode : NodeBase
    {
        public QuotesNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            QuoteTick? q = Services.Cache.QuoteTick(Ctx.MarketKey);
            if (q is null)
            {
                return;
            }

            decimal bid = q.Value.Bid.Value, ask = q.Value.Ask.Value;
            ctx.Set("bid", bid);
            ctx.Set("ask", ask);
            ctx.Set("mid", (bid + ask) / 2m);
            ctx.Set("spread", ask - bid);
        }
    }

    private sealed class TradesNode : NodeBase
    {
        public TradesNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            TradeTick? t = Services.Cache.TradeTick(Ctx.MarketKey);
            if (t is null)
            {
                return;
            }

            ctx.Set("last", t.Value.Price.Value);
            ctx.Set("size", t.Value.Size.Value);
        }
    }

    private sealed class BookNode : NodeBase
    {
        public BookNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            OrderBook? book = Services.Cache.OrderBook(Ctx.MarketKey);
            if (book is null)
            {
                return;
            }

            if (book.BestBidPrice is { } bid)
            {
                ctx.Set("bestBid", bid.Value);
            }

            if (book.BestAskPrice is { } ask)
            {
                ctx.Set("bestAsk", ask.Value);
            }

            if (book.BestBidSize is { } bidSize && book.BestAskSize is { } askSize)
            {
                decimal total = bidSize.Value + askSize.Value;
                ctx.Set("imbalance", total == 0m ? 0m : (bidSize.Value - askSize.Value) / total);
            }
        }
    }

    private sealed class MarkPriceNode : NodeBase
    {
        public MarkPriceNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            MarkPriceUpdate? m = Services.Cache.MarkPrice(Ctx.MarketKey);
            if (m is not null)
            {
                ctx.Set("mark", m.Value.Value);
            }
        }
    }

    private sealed class FundingNode : NodeBase
    {
        public FundingNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            FundingRateUpdate? f = Services.Cache.FundingRate(Ctx.MarketKey);
            if (f is null)
            {
                return;
            }

            ctx.Set("rate", f.Rate * 100m);
            if (f.NextFundingTime is { } next)
            {
                decimal hours = (decimal)(next.Value - ctx.Bar.EventTime.Value) / 3_600_000_000_000m;
                ctx.Set("hoursToNext", hours);
            }
        }
    }
}
