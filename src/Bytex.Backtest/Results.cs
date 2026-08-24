using System.Globalization;
using System.Text;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
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
    decimal ProfitFactor,
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

    public required IReadOnlyList<CurrencyStatistics> Currencies { get; init; }

    public required TradeStatistics Trades { get; init; }

    public required IReadOnlyDictionary<Currency, IReadOnlyList<EquityPoint>> EquityCurves { get; init; }

    public required IReadOnlyList<OrderReportRow> Orders { get; init; }

    public required IReadOnlyList<FillReportRow> Fills { get; init; }

    public required IReadOnlyList<PositionReportRow> Positions { get; init; }

    public required IReadOnlyList<AccountReportRow> Accounts { get; init; }

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
        foreach (Currency currency in positions.Select(p => p.SettlementCurrency).Concat(starting.Keys).Distinct())
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

            decimal commissions = byCurrency.Sum(p => p.Commissions.TryGetValue(currency, out Money c) ? c.Amount : 0m);
            decimal start = starting.GetValueOrDefault(currency);
            List<EquityPoint> curve = BuildEquityCurve(byCurrency, start);
            curves[currency] = curve;
            (decimal maxDd, decimal maxDdPct) = MaxDrawdown(curve);
            List<decimal> closedPnls = byCurrency.Where(p => p.IsClosed).Select(p => p.RealizedPnl.Amount).ToList();
            decimal grossProfit = closedPnls.Where(x => x > 0m).Sum();
            decimal grossLoss = Math.Abs(closedPnls.Where(x => x < 0m).Sum());
            decimal profitFactor = grossLoss == 0m ? (grossProfit > 0m ? decimal.MaxValue : 0m) : grossProfit / grossLoss;
            decimal expectancy = closedPnls.Count == 0 ? 0m : closedPnls.Average();
            List<double> dailyReturns = DailyReturns(byCurrency, start);
            double sharpe = Sharpe(dailyReturns);
            double sortino = Sortino(dailyReturns);
            decimal total = realized + unrealized;
            decimal endBalance = ending.TryGetValue(currency, out decimal e) ? e : start + realized;
            currencyStats.Add(new CurrencyStatistics(currency, start, endBalance, realized, unrealized, total, commissions,
                start == 0m ? 0m : total / start * 100m, maxDd, maxDdPct, sharpe, sortino, profitFactor, expectancy));
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
            Currencies = currencyStats,
            Trades = trades,
            EquityCurves = curves,
            Orders = orders.Select(OrderReportRow.From).ToList(),
            Fills = orders.SelectMany(o => o.Events.OfType<OrderFilled>()).OrderBy(f => f.TsEvent).Select(FillReportRow.From).ToList(),
            Positions = positions.Select(p => PositionReportRow.From(p, cache)).ToList(),
            Accounts = accounts.SelectMany(a => a.Events.SelectMany(e => e.Balances.Select(b => new AccountReportRow(a.Id, e.TsEvent, b.Currency, b.Total.Amount, b.Locked.Amount, b.Free.Amount)))).OrderBy(r => r.Timestamp).ToList(),
        };
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

    private static (decimal, decimal) MaxDrawdown(IReadOnlyList<EquityPoint> curve)
    {
        decimal peak = decimal.MinValue;
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
                maxDdPct = peak == 0m ? 0m : dd / peak * 100m;
            }
        }

        return (maxDd, maxDdPct);
    }

    private static List<double> DailyReturns(IReadOnlyList<Position> positions, decimal starting)
    {
        if (starting == 0m)
        {
            return [];
        }

        IEnumerable<IGrouping<DateOnly, decimal>> byDay = positions
            .Where(p => p.IsClosed && p.TsClosed is not null)
            .GroupBy(p => DateOnly.FromDateTime(p.TsClosed!.Value.ToDateTimeUtc()), p => p.RealizedPnl.Amount)
            .OrderBy(g => g.Key);
        return byDay.Select(g => (double)(g.Sum() / starting)).ToList();
    }

    private static double Sharpe(IReadOnlyList<double> returns)
    {
        if (returns.Count < 2)
        {
            return 0d;
        }

        double mean = returns.Average();
        double std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1));
        return std == 0d ? 0d : mean / std * Math.Sqrt(252d);
    }

    private static double Sortino(IReadOnlyList<double> returns)
    {
        if (returns.Count < 2)
        {
            return 0d;
        }

        double mean = returns.Average();
        double downside = Math.Sqrt(returns.Where(r => r < 0d).Sum(r => r * r) / returns.Count);
        return downside == 0d ? 0d : mean / downside * Math.Sqrt(252d);
    }

    public string Summary()
    {
        StringBuilder sb = new();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Backtest {RunId} ({TraderId})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Period:      {BacktestStart} -> {BacktestEnd}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Iterations:  {Iterations} in {Elapsed.TotalSeconds:F3}s");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Orders:      {TotalOrders}   Positions: {TotalPositions} (closed {Trades.ClosedPositions}, open {Trades.OpenPositions})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Win rate:    {Trades.WinRate:P1}   Winners: {Trades.Winners}   Losers: {Trades.Losers}   Avg duration: {Trades.AverageDuration}");
        foreach (CurrencyStatistics c in Currencies)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{c.Currency}] start {c.StartingBalance:N2}  end {c.EndingBalance:N2}  pnl {c.TotalPnl:N2} ({c.ReturnPercent:F2}%)  realized {c.RealizedPnl:N2}  unrealized {c.UnrealizedPnl:N2}  fees {c.TotalCommissions:N2}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"           max drawdown {c.MaxDrawdown:N2} ({c.MaxDrawdownPercent:F2}%)  sharpe {c.SharpeRatio:F2}  sortino {c.SortinoRatio:F2}  profit factor {(c.ProfitFactor == decimal.MaxValue ? "inf" : c.ProfitFactor.ToString("F2", CultureInfo.InvariantCulture))}  expectancy {c.Expectancy:N2}");
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
                ProfitFactor = c.ProfitFactor == decimal.MaxValue ? (decimal?)null : c.ProfitFactor,
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
        }, options);
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
        foreach ((Currency currency, IReadOnlyList<EquityPoint> curve) in result.EquityCurves)
        {
            File.WriteAllText(Path.Combine(directory, $"equity_{currency.Code}.csv"), ToCsv(curve));
        }
    }
}
