using System.Globalization;
using System.Text;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest;

/// <summary>
/// Performance statistics for one settlement currency.
/// </summary>
public sealed record CurrencyStatistics(
    Currency Currency,
    decimal StartingBalance,
    decimal EndingBalance,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    decimal TotalPnl,
    decimal TotalCommissions,
    decimal ReturnPercent,
    decimal MaxDrawdown,
    decimal MaxDrawdownPercent,
    double SharpeRatio,
    double SortinoRatio,
    decimal? ProfitFactor,
    decimal Expectancy);

/// <summary>
/// Trade-level statistics over closed positions.
/// </summary>
public sealed record TradeStatistics(
    int TotalPositions,
    int ClosedPositions,
    int OpenPositions,
    int Winners,
    int Losers,
    int Breakeven,
    decimal WinRate,
    decimal AverageWinner,
    decimal AverageLoser,
    decimal LargestWinner,
    decimal LargestLoser,
    decimal AverageReturn,
    TimeSpan AverageDuration,
    int LongPositions,
    int ShortPositions);

public sealed record EquityPoint(UnixNanos Timestamp, decimal Equity);

/// <summary>
/// What the run measured at one timestamp: realised plus unrealised profit in one currency, before the starting
/// balance is added. <see cref="BacktestResult"/> turns these into the equity curve.
/// </summary>
internal sealed record EquitySample(UnixNanos Timestamp, Currency Currency, decimal Total);

/// <summary>
/// The outcome of a backtest run: identity, timing, counts, statistics, and tabular reports.
/// </summary>
public sealed class BacktestResult
{
    public required string RunId { get; init; }

    public required TraderId TraderId { get; init; }

    public UnixNanos? RunStarted { get; init; }

    public UnixNanos? RunFinished { get; init; }

    public UnixNanos? BacktestStart { get; init; }

    public UnixNanos? BacktestEnd { get; init; }

    public TimeSpan Elapsed { get; init; }

    public long Iterations { get; init; }

    public int TotalEvents { get; init; }

    public int TotalOrders { get; init; }

    public int TotalPositions { get; init; }

    /// <summary>
    /// The strategies that faulted during the run, by id. Empty is the ordinary case. A run with a name in here did
    /// not trade because that strategy stopped, which is a different thing from a strategy whose conditions never
    /// came true, and the two are indistinguishable from the numbers alone.
    /// </summary>
    public IReadOnlyList<string> FaultedStrategies { get; init; } = [];

    public required IReadOnlyList<CurrencyStatistics> Currencies { get; init; }

    public required TradeStatistics Trades { get; init; }

    public required IReadOnlyDictionary<Currency, IReadOnlyList<EquityPoint>> EquityCurves { get; init; }

    public required IReadOnlyList<OrderReportRow> Orders { get; init; }

    public required IReadOnlyList<FillReportRow> Fills { get; init; }

    public required IReadOnlyList<PositionReportRow> Positions { get; init; }

    public required IReadOnlyList<AccountReportRow> Accounts { get; init; }

    /// <summary>
    /// Every funding payment the run made or took, oldest first. Empty for a run with no perpetual position or no
    /// funding data: a run that paid nothing and a run that was never charged look the same here, which is why the
    /// data a run was given is part of what a report says.
    /// </summary>
    public required IReadOnlyList<FundingReportRow> Funding { get; init; }

    /// <summary>
    /// Every position the venue closed itself for want of margin, oldest first. A row here is a trade the strategy
    /// did not choose to end, and the reason a result looks the way it does.
    /// </summary>
    public required IReadOnlyList<LiquidationReportRow> Liquidations { get; init; }

    /// <summary>
    /// Every charge a venue behaviour the run added made, oldest first (R8.19). Separate from <see cref="Funding"/>
    /// because the simulator does not know what these behaviours are - it knows a module moved money and said why -
    /// and a reader who finds a result smaller than they expected has to be able to see that a behaviour took it
    /// rather than concluding the strategy lost it.
    /// </summary>
    public required IReadOnlyList<ModuleChargeReportRow> ModuleCharges { get; init; }

    /// <summary>
    /// What the venues in this run model, by the names in <see cref="SimulationCapabilities"/>. A reader can tell
    /// from the result itself whether these numbers came out of a simulator that bounds fills by the size on offer,
    /// charges funding and liquidates - rather than having to know which version produced them.
    /// </summary>
    public required IReadOnlyList<string> Simulation { get; init; }

    /// <summary>
    /// What the simulator actually applied in this run, by the same names. A venue configured for something it never
    /// did - partial fills on data that says nothing about size, funding with no rates in the run - is in
    /// <see cref="Simulation"/> and not here. This is the list to read when telling somebody what their result
    /// accounts for.
    /// </summary>
    public required IReadOnlyList<string> Applied { get; init; }

    /// <summary>
    /// Where the margin requirement came from for the instruments this run actually held a position in, by the names
    /// of <see cref="MarginSource"/>, and empty for a run that held none.
    ///
    /// <para>
    /// <b>Why a result says this at all.</b> Margin decides how much of the account a leveraged position ties up and
    /// the price at which it is liquidated, and until 0.7.0 two of the three venues supplied that figure from a
    /// constant in the adapter - 0.05 for every contract, where Bybit's own risk limits give 0.0066 on BTCUSDT. Every
    /// result produced in that time is a real result computed against an invented requirement, and nothing in it says
    /// so. A person opening a saved report beside a fresh one on the same strategy, the same period and the same
    /// venue, and finding they disagree, has no way to tell a corrected input from a broken engine.
    /// </para>
    ///
    /// <para>
    /// So the provenance travels with the result, not just the figure. A run whose catalog entries predate the marker
    /// reports <see cref="MarginSource.Unrecorded"/> - which is the honest answer and the one that explains the
    /// disagreement. Deliberately not a date: a workspace restored from a backup would carry the date it was written
    /// rather than the state of what it holds, and the first person to restore one would be told something false.
    /// </para>
    ///
    /// <para>
    /// Positions rather than every instrument loaded, because margin is a property of holding something. A run given
    /// twelve instruments and trading one would otherwise report eleven provenances that changed nothing about its
    /// numbers.
    /// </para>
    /// </summary>
    public required IReadOnlyList<MarginSource> MarginSources { get; init; }

    /// <summary>
    /// What leverage this run asked for on each instrument it worked an order in, and what it actually got.
    ///
    /// <para>
    /// <b>Why a run has to say this.</b> A live run is refused outright when the venue grants less leverage than was
    /// asked for - that is <c>LeverageGuard</c>, and it names both figures. A backtest is not refused, because
    /// nothing calls that guard here; it is CLAMPED, by <see cref="Instrument.InitialMarginRate"/> taking the larger
    /// of 1/leverage and the instrument's own margin. An instrument carrying 0.05 therefore supports 20x however
    /// much a document asked for, and a strategy written for 50x is measured at 20x - smaller positions, a
    /// different drawdown, orders denied for margin that would have passed. A result for a strategy nobody wrote,
    /// with nothing in it to say so.
    /// </para>
    ///
    /// <para>
    /// It is reported rather than refused on purpose. A backtest is cheap to run again, and stopping one at the
    /// moment somebody presses go, over a figure they can only discover by reading a venue's margin schedule, buys
    /// less than telling them what the run did. The refusal belongs where real money does.
    /// </para>
    ///
    /// <para>
    /// Orders rather than positions, unlike <see cref="MarginSources"/>: the clamp binds when an order's margin is
    /// held, so it can change a result through orders that never filled - including denying them - and a run whose
    /// orders were all refused for margin is exactly the one whose reader needs this.
    /// </para>
    /// </summary>
    public required IReadOnlyList<LeverageReportRow> Leverages { get; init; }

    /// <summary>
    /// What the bounding of fills came to: the share of a bar's volume one participant was allowed, how many fills
    /// were held back by the size on offer, and by how much in total. A result can then say what it assumed rather
    /// than asking to be trusted.
    /// </summary>
    public required ParticipationSummary Participation { get; init; }

    public static BacktestResult From(BacktestEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Cache cache = engine.Cache;
        IReadOnlyList<Position> positions = cache.Positions();
        IReadOnlyList<Order> orders = cache.Orders();
        IReadOnlyList<Account> accounts = cache.Accounts();

        Dictionary<Currency, decimal> starting = new();
        foreach (SimulatedExchange exchange in engine.Exchanges.Values)
        {
            foreach (Money balance in exchange.Config.StartingBalances)
            {
                starting[balance.Currency] = starting.GetValueOrDefault(balance.Currency) + balance.Amount;
            }
        }

        Dictionary<Currency, decimal> ending = new();
        foreach (Account account in accounts)
        {
            foreach (AccountBalance balance in account.Balances.Values)
            {
                ending[balance.Currency] = ending.GetValueOrDefault(balance.Currency) + balance.Total.Amount;
            }
        }

        Dictionary<Currency, IReadOnlyList<EquityPoint>> curves = new();
        List<CurrencyStatistics> currencyStats = new();
        IEnumerable<Currency> touched = positions.Select(p => p.SettlementCurrency)
            .Concat(positions.SelectMany(p => p.Commissions.Keys))
            .Concat(starting.Keys)
            .Concat(ending.Keys)
            .Distinct();
        foreach (Currency currency in touched)
        {
            List<Position> byCurrency = positions.Where(p => p.SettlementCurrency.Equals(currency)).ToList();
            decimal realized = byCurrency.Sum(p => p.RealizedPnl.Amount);
            decimal unrealized = 0m;
            foreach (Position open in byCurrency.Where(p => p.IsOpen))
            {
                Price? last = cache.Price(open.InstrumentId, open.IsLong ? PriceType.Bid : PriceType.Ask) ?? cache.Price(open.InstrumentId, PriceType.Last);
                if (last is { } px)
                {
                    unrealized += open.UnrealizedPnl(px).Amount;
                }
            }

            // Every position that paid a fee in this currency, not only the ones that settle in it: a fee paid in a
            // third currency used to be reported nowhere.
            decimal commissions = positions.Sum(p => p.Commissions.TryGetValue(currency, out Money c) ? c.Amount : 0m);
            decimal start = starting.GetValueOrDefault(currency);
            List<EquityPoint> curve = Curve(engine, currency, start, byCurrency);
            curves[currency] = curve;
            (decimal maxDd, decimal maxDdPct) = MaxDrawdown(curve, start);
            List<decimal> closedPnls = byCurrency.Where(p => p.IsClosed).Select(p => p.RealizedPnl.Amount).ToList();
            decimal grossProfit = closedPnls.Where(x => x > 0m).Sum();
            decimal grossLoss = Math.Abs(closedPnls.Where(x => x < 0m).Sum());
            // No losing trade at all leaves the ratio undefined rather than enormous: decimal.MaxValue read as a
            // number put a 29-digit integer in front of a client, because a host holding this record sees the field.
            decimal? profitFactor = grossLoss == 0m ? (grossProfit > 0m ? null : 0m) : grossProfit / grossLoss;
            decimal expectancy = closedPnls.Count == 0 ? 0m : closedPnls.Average();
            List<double> dailyReturns = DailyReturns(curve, start);
            double sharpe = Sharpe(dailyReturns);
            double sortino = Sortino(dailyReturns);
            decimal total = realized + unrealized;
            decimal endBalance = ending.TryGetValue(currency, out decimal e) ? e : start + realized;
            currencyStats.Add(new CurrencyStatistics(currency, start, endBalance, realized, unrealized, total, commissions,
                start == 0m ? 0m : total / start * Scales.Percent, maxDd, maxDdPct, sharpe, sortino, profitFactor, expectancy));
        }

        List<Position> closed = positions.Where(p => p.IsClosed).ToList();
        List<decimal> pnls = closed.Select(p => p.RealizedPnl.Amount).ToList();
        int winners = pnls.Count(x => x > 0m);
        int losers = pnls.Count(x => x < 0m);
        TradeStatistics trades = new(
            positions.Count,
            closed.Count,
            positions.Count - closed.Count,
            winners,
            losers,
            pnls.Count - winners - losers,
            pnls.Count == 0 ? 0m : (decimal)winners / pnls.Count,
            winners == 0 ? 0m : pnls.Where(x => x > 0m).Average(),
            losers == 0 ? 0m : pnls.Where(x => x < 0m).Average(),
            pnls.Count == 0 ? 0m : pnls.Max(),
            pnls.Count == 0 ? 0m : pnls.Min(),
            closed.Count == 0 ? 0m : closed.Average(p => p.RealizedReturn),
            closed.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)closed.Average(p => (p.Duration ?? TimeSpan.Zero).Ticks)),
            positions.Count(p => p.EntrySide == PositionSide.Long),
            positions.Count(p => p.EntrySide == PositionSide.Short));

        return new BacktestResult
        {
            RunId = engine.Config.RunId,
            TraderId = engine.Kernel.TraderId,
            RunStarted = engine.RunStarted,
            RunFinished = engine.RunFinished,
            BacktestStart = engine.BacktestStart,
            BacktestEnd = engine.BacktestEnd,
            Elapsed = engine.Elapsed,
            Iterations = engine.Iteration,
            TotalEvents = (int)engine.Kernel.ExecutionEngine.EventCount,
            TotalOrders = orders.Count,
            TotalPositions = positions.Count,
            FaultedStrategies = engine.Kernel.Trader.Strategies.Where(s => s.IsFaulted).Select(s => s.StrategyId.Value).ToList(),
            Currencies = currencyStats,
            Trades = trades,
            EquityCurves = curves,
            Orders = orders.Select(OrderReportRow.From).ToList(),
            Fills = orders.SelectMany(o => o.Events.OfType<OrderFilled>()).OrderBy(f => f.TsEvent).Select(FillReportRow.From).ToList(),
            Positions = positions.Select(p => PositionReportRow.From(p, cache)).ToList(),
            Accounts = accounts.SelectMany(a => a.Events.SelectMany(e => e.Balances.Select(b => new AccountReportRow(a.Id, e.TsEvent, b.Currency, b.Total.Amount, b.Locked.Amount, b.Free.Amount)))).OrderBy(r => r.Timestamp).ToList(),
            Funding = engine.Exchanges.Values
                .SelectMany(x => x.FundingPayments)
                .OrderBy(p => p.TsEvent.Value)
                .Select(FundingReportRow.From)
                .ToList(),
            Liquidations = engine.Exchanges.Values
                .SelectMany(x => x.Liquidations)
                .OrderBy(l => l.TsEvent.Value)
                .Select(LiquidationReportRow.From)
                .ToList(),
            ModuleCharges = engine.Exchanges.Values
                .SelectMany(x => x.ModuleCharges)
                .OrderBy(c => c.TsEvent.Value)
                .Select(ModuleChargeReportRow.From)
                .ToList(),
            Simulation = engine.Exchanges.Values
                .SelectMany(x => SimulationCapabilities.Of(x.Config))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList(),
            Applied = engine.Exchanges.Values
                .SelectMany(x => x.Applied)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList(),
            MarginSources = positions
                .Select(p => cache.Instrument(p.InstrumentId))
                .OfType<Instrument>()
                .Select(i => i.MarginSource)
                .Distinct()
                .OrderBy(source => source)
                .ToList(),
            Leverages = orders
                .Select(o => o.InstrumentId)
                .Distinct()
                .Select(id => (Id: id, Instrument: cache.Instrument(id)))
                .Where(x => x.Instrument is not null)
                .Select(x => LeverageReportRow.For(
                    x.Instrument!,
                    engine.Exchanges.TryGetValue(x.Id.Venue, out SimulatedExchange? exchange)
                        ? exchange.Config.Leverages.GetValueOrDefault(x.Id, exchange.Config.DefaultLeverage)
                        : 1m))
                .OfType<LeverageReportRow>()
                .OrderBy(row => row.InstrumentId.Value, StringComparer.Ordinal)
                .ToList(),
            Participation = new ParticipationSummary(
                engine.Exchanges.Values.Select(x => x.Config.BarVolumeShare).FirstOrDefault(s => s is not null),
                engine.Exchanges.Values.Sum(x => x.Bounded.Fills),
                engine.Exchanges.Values.Sum(x => x.Bounded.Quantity)),
        };
    }

    /// <summary>
    /// The equity curve of one currency: what the run sampled per timestamp, each point the starting balance plus the
    /// realised and unrealised profit at that moment. A run that sampled nothing - no data, or a result assembled
    /// without a run - falls back to the old reconstruction from fills, which is all that can be known then.
    /// </summary>
    private static List<EquityPoint> Curve(BacktestEngine engine, Currency currency, decimal starting, IReadOnlyList<Position> positions)
    {
        List<EquityPoint> sampled = engine.EquitySamples
            .Where(s => s.Currency.Equals(currency))
            .Select(s => new EquityPoint(s.Timestamp, starting + s.Total))
            .ToList();

        return sampled.Count > 0 ? sampled : BuildEquityCurve(positions, starting);
    }

    private static List<EquityPoint> BuildEquityCurve(IReadOnlyList<Position> positions, decimal starting)
    {
        List<EquityPoint> curve = new();
        decimal equity = starting;
        IEnumerable<(UnixNanos Ts, decimal Pnl)> events = positions
            .SelectMany(p => p.Events.Select((f, i) => (f.TsEvent, PnlDelta(p, i))))
            .OrderBy(x => x.TsEvent);
        foreach ((UnixNanos ts, decimal pnl) in events)
        {
            equity += pnl;
            curve.Add(new EquityPoint(ts, equity));
        }

        return curve;
    }

    /// <summary>Realized P&amp;L contributed by the i-th fill of a position (difference from replaying fills).</summary>
    private static decimal PnlDelta(Position position, int fillIndex)
    {
        Position replay = new(position.Instrument, position.Events[0]);
        decimal before = 0m;
        for (int i = 1; i <= fillIndex; i++)
        {
            before = replay.RealizedPnl.Amount;
            replay.Apply(position.Events[i]);
        }

        return fillIndex == 0 ? replay.RealizedPnl.Amount : replay.RealizedPnl.Amount - before;
    }

    /// <summary>
    /// Peak to trough of the equity curve. The first peak is the balance the run started with: taking it from the first
    /// equity point instead hid whatever the first fill cost, so a run that only ever lost money reported no drawdown
    /// until it dipped below its first fill.
    /// </summary>
    private static (decimal, decimal) MaxDrawdown(IReadOnlyList<EquityPoint> curve, decimal starting)
    {
        decimal peak = starting;
        decimal maxDd = 0m;
        decimal maxDdPct = 0m;
        foreach (EquityPoint point in curve)
        {
            if (point.Equity > peak)
            {
                peak = point.Equity;
            }

            decimal dd = peak - point.Equity;
            if (dd > maxDd)
            {
                maxDd = dd;
                maxDdPct = peak == 0m ? 0m : dd / peak * Scales.Percent;
            }
        }

        return (maxDd, maxDdPct);
    }

    /// <summary>
    /// The return of each day, from the last equity the curve holds for that day. Taking them from closed positions
    /// instead meant a day on which nothing closed had no return at all, however far the open position moved, and the
    /// Sharpe and Sortino ratios were computed over the days that happened to close a trade.
    /// </summary>
    private static List<double> DailyReturns(IReadOnlyList<EquityPoint> curve, decimal starting)
    {
        if (starting == 0m || curve.Count == 0)
        {
            return [];
        }

        List<double> returns = new();
        decimal previous = starting;
        foreach (IGrouping<DateOnly, EquityPoint> day in curve
            .GroupBy(p => DateOnly.FromDateTime(p.Timestamp.ToDateTimeUtc()))
            .OrderBy(g => g.Key))
        {
            decimal close = day.Last().Equity;
            if (previous != 0m)
            {
                returns.Add((double)((close - previous) / previous));
            }

            previous = close;
        }

        return returns;
    }

    private static List<double> DailyReturnsFromPositions(IReadOnlyList<Position> positions, decimal starting)
    {
        if (starting == 0m)
        {
            return [];
        }

        Dictionary<DateOnly, decimal> byDay = positions
            .Where(p => p.IsClosed && p.TsClosed is not null)
            .GroupBy(p => DateOnly.FromDateTime(p.TsClosed!.Value.ToDateTimeUtc()), p => p.RealizedPnl.Amount)
            .ToDictionary(g => g.Key, g => g.Sum());
        if (byDay.Count == 0)
        {
            return [];
        }

        // The series is annualised with sqrt(252), which says every element is one day. Dropping the days that closed
        // nothing made an idle stretch look like a run of trading days and flattered both ratios.
        DateOnly first = byDay.Keys.Min();
        DateOnly last = byDay.Keys.Max();
        List<double> returns = new();
        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            returns.Add((double)(byDay.GetValueOrDefault(day) / starting));
        }

        return returns;
    }

    private static double Sharpe(IReadOnlyList<double> returns)
    {
        if (returns.Count < 2)
        {
            return 0d;
        }

        double mean = returns.Average();
        double std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1));
        return std == 0d ? 0d : mean / std * Math.Sqrt(Scales.TradingDaysPerYear);
    }

    private static double Sortino(IReadOnlyList<double> returns)
    {
        if (returns.Count < 2)
        {
            return 0d;
        }

        double mean = returns.Average();
        double downside = Math.Sqrt(returns.Where(r => r < 0d).Sum(r => r * r) / returns.Count);
        return downside == 0d ? 0d : mean / downside * Math.Sqrt(Scales.TradingDaysPerYear);
    }

    public string Summary()
    {
        StringBuilder sb = new();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Backtest {RunId} ({TraderId})");
        if (FaultedStrategies.Count > 0)
        {
            // First line after the name, because every number under it is the number of a run that stopped.
            sb.AppendLine(CultureInfo.InvariantCulture, $"  STOPPED:     {string.Join(", ", FaultedStrategies)} faulted; the log says why");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Period:      {BacktestStart} -> {BacktestEnd}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Iterations:  {Iterations} in {Elapsed.TotalSeconds:F3}s");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Orders:      {TotalOrders}   Positions: {TotalPositions} (closed {Trades.ClosedPositions}, open {Trades.OpenPositions})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Win rate:    {Trades.WinRate:P1}   Winners: {Trades.Winners}   Losers: {Trades.Losers}   Avg duration: {Trades.AverageDuration}");
        foreach (CurrencyStatistics c in Currencies)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{c.Currency}] start {c.StartingBalance:N2}  end {c.EndingBalance:N2}  pnl {c.TotalPnl:N2} ({c.ReturnPercent:F2}%)  realized {c.RealizedPnl:N2}  unrealized {c.UnrealizedPnl:N2}  fees {c.TotalCommissions:N2}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"           max drawdown {c.MaxDrawdown:N2} ({c.MaxDrawdownPercent:F2}%)  sharpe {c.SharpeRatio:F2}  sortino {c.SortinoRatio:F2}  profit factor {(c.ProfitFactor is null ? "inf" : c.ProfitFactor.Value.ToString("F2", CultureInfo.InvariantCulture))}  expectancy {c.Expectancy:N2}");
        }

        return sb.ToString();
    }
}

public sealed record OrderReportRow(
    ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId, StrategyId StrategyId, InstrumentId InstrumentId, OrderSide Side, OrderType Type,
    Quantity Quantity, Quantity FilledQuantity, Price? Price, Price? TriggerPrice, TimeInForce TimeInForce, OrderStatus Status, decimal? AvgPx, decimal Slippage,
    UnixNanos TsInit, UnixNanos TsLast)
{
    public static OrderReportRow From(Order o) => new(o.ClientOrderId, o.VenueOrderId, o.StrategyId, o.InstrumentId, o.Side, o.Type, o.Quantity, o.FilledQuantity,
        o.Price, o.TriggerPrice, o.TimeInForce, o.Status, o.AvgPx, o.Slippage, o.TsInit, o.TsLast);
}

public sealed record FillReportRow(
    TradeId TradeId, ClientOrderId ClientOrderId, StrategyId StrategyId, InstrumentId InstrumentId, OrderSide Side, Quantity LastQty, Price LastPx,
    Money Commission, LiquiditySide LiquiditySide, UnixNanos TsEvent)
{
    public static FillReportRow From(OrderFilled f) => new(f.TradeId, f.ClientOrderId, f.StrategyId, f.InstrumentId, f.OrderSide, f.LastQty, f.LastPx, f.Commission, f.LiquiditySide, f.TsEvent);
}

public sealed record PositionReportRow(
    PositionId PositionId, StrategyId StrategyId, InstrumentId InstrumentId, PositionSide EntrySide, PositionSide Side, Quantity Quantity, Quantity PeakQuantity,
    decimal AvgPxOpen, decimal? AvgPxClose, Money RealizedPnl, Money? UnrealizedPnl, decimal RealizedReturn, UnixNanos TsOpened, UnixNanos? TsClosed, TimeSpan? Duration)
{
    public static PositionReportRow From(Position p, Cache cache)
    {
        Money? unrealized = null;
        if (p.IsOpen)
        {
            Price? last = cache.Price(p.InstrumentId, p.IsLong ? PriceType.Bid : PriceType.Ask) ?? cache.Price(p.InstrumentId, PriceType.Last);
            if (last is { } px)
            {
                unrealized = p.UnrealizedPnl(px);
            }
        }

        return new PositionReportRow(p.Id, p.StrategyId, p.InstrumentId, p.EntrySide, p.Side, p.Quantity, p.PeakQuantity, p.AvgPxOpen, p.AvgPxClose, p.RealizedPnl, unrealized, p.RealizedReturn, p.TsOpened, p.TsClosed, p.Duration);
    }
}

public sealed record AccountReportRow(AccountId AccountId, UnixNanos Timestamp, Currency Currency, decimal Total, decimal Locked, decimal Free);

/// <summary>
/// What one instrument's leverage came to in a run: what was asked for, what the simulator could give, and the two
/// facts that decide the difference.
///
/// <para>
/// <c>Applied</c> is the reciprocal of what <see cref="Instrument.InitialMarginRate"/> returned, which is the
/// leverage the run's arithmetic really used. It equals <c>Requested</c> whenever the instrument's margin leaves
/// room for it, and is lower when the margin is a floor - so <c>Capped</c> is not a separate judgement, it is the
/// two numbers disagreeing.
/// </para>
///
/// <para>
/// <c>MarginInit</c> and <c>MarginSource</c> travel with the row because the cap is only as trustworthy as the
/// figure that caused it. A 0.05 read from this contract's own bracket is a fact about this contract; a 0.05 held
/// because no credential could read the brackets is a placeholder that happens to be a number, and it caps a run
/// just as hard. A reader deciding whether to go and fetch the real figure needs to know which one capped them.
/// </para>
/// </summary>
public sealed record LeverageReportRow(
    InstrumentId InstrumentId,
    decimal Requested,
    decimal Applied,
    decimal MarginInit,
    MarginSource MarginSource)
{
    /// <summary>Whether this run used less leverage than it was asked to.</summary>
    public bool Capped => Applied < Requested;

    /// <summary>
    /// The row for one instrument at the leverage its venue was configured with, or null where leverage has nothing
    /// to say: an unleveraged request on an instrument that borrows nothing, which is every spot order and would
    /// otherwise put a row saying "1 and 1" beside every real one.
    /// </summary>
    public static LeverageReportRow? For(Instrument instrument, decimal requested)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (instrument.MarginInit <= 0m && requested <= 1m)
        {
            return null;
        }

        decimal rate = instrument.InitialMarginRate(requested);

        return new LeverageReportRow(
            instrument.Id,
            requested,
            rate <= 0m ? requested : 1m / rate,
            instrument.MarginInit,
            instrument.MarginSource);
    }
}

/// <summary>
/// What bounding fills came to over a run: the share of a bar's volume one participant was allowed (null where no
/// venue was told to bound by volume), how many fills were bounded by the size on offer, and how much went in under a
/// bound. Read as "this run assumed it could take that share of the market, and did so this many times".
/// </summary>
public sealed record ParticipationSummary(decimal? BarVolumeShare, int BoundedFills, decimal BoundedQuantity);

/// <summary>
/// One funding payment in a report: negative is what the account paid, positive what it was paid. The quantity is
/// signed, so the row says which way the position was pointing when the rate was applied.
/// </summary>
public sealed record FundingReportRow(UnixNanos Timestamp, InstrumentId InstrumentId, decimal Rate, decimal Quantity, decimal Price, Currency Currency, decimal Amount)
{
    public static FundingReportRow From(FundingPayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new FundingReportRow(payment.TsEvent, payment.InstrumentId, payment.Rate, payment.SignedQuantity, payment.Price.Value, payment.Amount.Currency, payment.Amount.Amount);
    }
}

/// <summary>
/// One charge an added venue behaviour made: which behaviour, what moved - negative is what the account paid - and
/// the behaviour's own account of what it was for. The reason is the behaviour's words rather than a code, because
/// the simulator has no vocabulary for something it does not know about.
/// </summary>
public sealed record ModuleChargeReportRow(UnixNanos Timestamp, string Module, Currency Currency, decimal Amount, string Reason)
{
    public static ModuleChargeReportRow From(ModuleCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        return new ModuleChargeReportRow(charge.TsEvent, charge.Module, charge.Amount.Currency, charge.Amount.Amount, charge.Reason);
    }
}

/// <summary>
/// One position the venue closed itself: what it held, what the account was worth against what it owed, and the price
/// the venue valued it at when it stepped in.
/// </summary>
public sealed record LiquidationReportRow(UnixNanos Timestamp, InstrumentId InstrumentId, decimal Quantity, decimal Price, Currency Currency, decimal Equity, decimal MaintenanceMargin)
{
    public static LiquidationReportRow From(Liquidation liquidation)
    {
        ArgumentNullException.ThrowIfNull(liquidation);
        return new LiquidationReportRow(
            liquidation.TsEvent,
            liquidation.InstrumentId,
            liquidation.SignedQuantity,
            liquidation.Price.Value,
            liquidation.Equity.Currency,
            liquidation.Equity.Amount,
            liquidation.MaintenanceMargin.Amount);
    }
}

/// <summary>
/// Writes result tables as CSV.
/// </summary>
public static class ReportWriter
{
    public static string ToCsv<T>(IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        System.Reflection.PropertyInfo[] properties = typeof(T).GetProperties();
        StringBuilder sb = new();
        sb.AppendLine(string.Join(',', properties.Select(p => p.Name)));
        foreach (T row in rows)
        {
            sb.AppendLine(string.Join(',', properties.Select(p => Format(p.GetValue(row)))));
        }

        return sb.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? string.Empty),
    };

    private static string Quote(string text) => text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;

    /// <summary>
    /// Serialises the full result (statistics, curves, and tables) as JSON.
    /// </summary>
    public static string ToJson(BacktestResult result, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        System.Text.Json.JsonSerializerOptions options = Core.Serialization.BytexJson.Create(indented);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            result.RunId,
            result.TraderId,
            result.RunStarted,
            result.RunFinished,
            result.BacktestStart,
            result.BacktestEnd,
            ElapsedSeconds = result.Elapsed.TotalSeconds,
            result.Iterations,
            result.TotalEvents,
            result.TotalOrders,
            result.TotalPositions,

            // Never written until 0.7, which meant a run where a strategy threw produced a report that did not say
            // so anywhere a program could read: the numbers were simply those of however much of the run happened
            // before it stopped trading. Found by the test that every field of a result reaches the file.
            result.FaultedStrategies,
            Currencies = result.Currencies.Select(c => new
            {
                Currency = c.Currency.Code,
                c.StartingBalance,
                c.EndingBalance,
                c.RealizedPnl,
                c.UnrealizedPnl,
                c.TotalPnl,
                c.TotalCommissions,
                c.ReturnPercent,
                c.MaxDrawdown,
                c.MaxDrawdownPercent,
                c.SharpeRatio,
                c.SortinoRatio,
                ProfitFactor = c.ProfitFactor,
                c.Expectancy,
            }),
            Trades = new
            {
                result.Trades.TotalPositions,
                result.Trades.ClosedPositions,
                result.Trades.OpenPositions,
                result.Trades.Winners,
                result.Trades.Losers,
                result.Trades.Breakeven,
                result.Trades.WinRate,
                result.Trades.AverageWinner,
                result.Trades.AverageLoser,
                result.Trades.LargestWinner,
                result.Trades.LargestLoser,
                result.Trades.AverageReturn,
                AverageDurationSeconds = result.Trades.AverageDuration.TotalSeconds,
                result.Trades.LongPositions,
                result.Trades.ShortPositions,
            },
            EquityCurves = result.EquityCurves.ToDictionary(kv => kv.Key.Code, kv => kv.Value),
            result.Orders,
            result.Fills,
            result.Positions,
            result.Accounts,
            result.Funding,
            result.Liquidations,
            result.ModuleCharges,
            result.Simulation,
            result.Applied,
            result.MarginSources,
            result.Leverages,
            result.Participation,
        }, options);
    }

    /// <summary>
    /// Writes the batch as one table a person or a host can sort: a line per run, in a file per currency, beside the
    /// per-run reports. A run that failed is a line with its reason and no figures, so what could not be run is as
    /// visible as what could.
    /// </summary>
    public static void WriteBatch(BacktestBatch batch, string directory, string name = "batch")
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(directory);
        IReadOnlyList<Currency> currencies = [.. batch.Runs
            .Where(r => r.Result is not null)
            .SelectMany(r => r.Result!.Currencies.Select(c => c.Currency))
            .Distinct()
            .OrderBy(c => c.Code, StringComparer.Ordinal)];

        if (currencies.Count == 0)
        {
            // Nothing ran, so there is no currency to name a file after; the runs are still written, with the reasons
            // they failed, because a batch that produced nothing is something a host has to be able to read.
            File.WriteAllText(Path.Combine(directory, $"{name}.csv"), ToCsv(batch.Rows(Currencies.USD)));
            return;
        }

        foreach (Currency currency in currencies)
        {
            File.WriteAllText(Path.Combine(directory, $"{name}_{currency.Code}.csv"), ToCsv(batch.Rows(currency)));
        }
    }

    /// <summary>
    /// What a search did, beside the generations' own tables: one row a candidate, in the order they were proposed,
    /// with the generation that proposed it, whether it was run or taken from an earlier generation, what it scored,
    /// and the figures of the run it became. A search whose record is one table per generation is a search nobody
    /// can read end to end.
    /// </summary>
    public static void WriteSearch(BacktestSearch search, string directory)
    {
        ArgumentNullException.ThrowIfNull(search);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "search.txt"), search.Summary());

        IReadOnlyList<BacktestSearchCandidate> candidates = search.Candidates;
        BacktestBatch asOneBatch = new() { Runs = [.. candidates.Select(c => c.Run)] };
        IReadOnlyList<Currency> currencies = [.. candidates
            .Where(c => c.Run.Result is not null)
            .SelectMany(c => c.Run.Result!.Currencies.Select(s => s.Currency))
            .Distinct()
            .OrderBy(c => c.Code, StringComparer.Ordinal)];

        foreach (Currency currency in currencies.Count == 0 ? [Currencies.USD] : currencies)
        {
            IReadOnlyList<BacktestBatchRow> rows = asOneBatch.Rows(currency);
            StringBuilder csv = new();
            csv.AppendLine("Generation,Candidate,Reused,Fitness,Point,RunId,Trades,TotalPnl,ReturnPercent,MaxDrawdownPercent,SharpeRatio,ProfitFactor,Failure");
            for (int i = 0; i < candidates.Count; i++)
            {
                BacktestSearchCandidate candidate = candidates[i];
                BacktestBatchRow row = rows[i];
                csv.Append(candidate.Generation.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(candidate.Index.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(candidate.Reused ? "yes" : "no").Append(',')
                    .Append(candidate.Fitness?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                    .Append(Quote(candidate.PointText)).Append(',')
                    .Append(Quote(row.RunId)).Append(',')
                    .Append(row.Trades.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.TotalPnl.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.ReturnPercent.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.MaxDrawdownPercent.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.SharpeRatio.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.ProfitFactor?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                    .Append(Quote(row.Failure));
                csv.AppendLine();
            }

            File.WriteAllText(Path.Combine(directory, $"search_{currency.Code}.csv"), csv.ToString());
        }
    }

    public static void WriteAll(BacktestResult result, string directory)
    {
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "summary.txt"), result.Summary());
        File.WriteAllText(Path.Combine(directory, "result.json"), ToJson(result));
        File.WriteAllText(Path.Combine(directory, "orders.csv"), ToCsv(result.Orders));
        File.WriteAllText(Path.Combine(directory, "fills.csv"), ToCsv(result.Fills));
        File.WriteAllText(Path.Combine(directory, "positions.csv"), ToCsv(result.Positions));
        File.WriteAllText(Path.Combine(directory, "accounts.csv"), ToCsv(result.Accounts));
        if (result.Funding.Count > 0)
        {
            File.WriteAllText(Path.Combine(directory, "funding.csv"), ToCsv(result.Funding));
        }

        if (result.ModuleCharges.Count > 0)
        {
            File.WriteAllText(Path.Combine(directory, "module_charges.csv"), ToCsv(result.ModuleCharges));
        }

        if (result.Liquidations.Count > 0)
        {
            File.WriteAllText(Path.Combine(directory, "liquidations.csv"), ToCsv(result.Liquidations));
        }

        foreach ((Currency currency, IReadOnlyList<EquityPoint> curve) in result.EquityCurves)
        {
            File.WriteAllText(Path.Combine(directory, $"equity_{currency.Code}.csv"), ToCsv(curve));
        }
    }
}
