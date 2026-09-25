using System.Text.Json;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;

namespace Bytex.Backtest.Tests;

// Why: the report tables and the JSON document are what leaves the process. Their rows are checked against the
// scripted trades of StatisticsScenario, and the JSON is parsed back so that its shape is pinned.
public sealed class ReportsAndSerializationTests
{
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Every_field_of_a_result_reaches_the_file_it_is_written_to()
    {
        // The written report is a hand-listed projection rather than the result serialised, so a field added to
        // BacktestResult is absent from result.json until somebody remembers to list it. That failure is silent in
        // the worst way: the run is correct, the report is complete-looking, and the number is just not there.
        //
        // It has happened. A charge made by an added venue behaviour was missing from the file, and the only reason
        // it was caught is that reading the file back into the type it came from failed on a required property -
        // which is luck, not a test. This is the test.
        using SimHarness sim = StatisticsScenario.Run();
        BacktestResult result = sim.Engine.GetResult();

        using JsonDocument written = JsonDocument.Parse(ReportWriter.ToJson(result));
        string[] present = [.. written.RootElement.EnumerateObject().Select(p => p.Name)];

        // Renamed on the way out, because a file says what a reader needs rather than what a type happens to call
        // it: a TimeSpan becomes seconds, and a currency-keyed dictionary becomes codes.
        string[] renamed = [nameof(BacktestResult.Elapsed)];

        foreach (string field in typeof(BacktestResult).GetProperties().Select(p => p.Name).Except(renamed, StringComparer.Ordinal))
        {
            Assert.Contains(field, present, StringComparer.OrdinalIgnoreCase);
        }

        // And the one that is renamed is still there under the name the file uses, so this test cannot be satisfied
        // by simply adding names to the list above.
        Assert.Contains("elapsedSeconds", present, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Orders_table_has_one_row_per_order_with_its_final_state()
    {
        using SimHarness sim = StatisticsScenario.Run();

        IReadOnlyList<OrderReportRow> orders = sim.Engine.GetResult().Orders;

        Assert.Equal(7, orders.Count);
        OrderReportRow first = orders.Single(o => o.ClientOrderId.Value == "O-20250101-000002-001-001-1");
        Assert.Equal("V-1", first.VenueOrderId!.Value.Value);
        Assert.Equal(SimHarness.StrategyId, first.StrategyId);
        Assert.Equal(sim.Id, first.InstrumentId);
        Assert.Equal(OrderSide.Buy, first.Side);
        Assert.Equal(OrderType.Market, first.Type);
        Assert.Equal(sim.Qty(1m), first.Quantity);
        Assert.Equal(sim.Qty(1m), first.FilledQuantity);
        Assert.Null(first.Price);
        Assert.Null(first.TriggerPrice);
        Assert.Equal(TimeInForce.Gtc, first.TimeInForce);
        Assert.Equal(OrderStatus.Filled, first.Status);
        Assert.Equal(100m, first.AvgPx);
        Assert.Equal(Scripted.Ms(2000), first.TsInit);
        Assert.Equal(Scripted.Ms(2000), first.TsLast);
    }

    [Fact]
    public void Orders_table_reports_adverse_slippage_against_the_orders_reference_price()
    {
        // Stop trigger 101.00 (the reference), filled at the 101.50 ask after a gap: 0.50 adverse.
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.00m))))
            .Quote(2000, 101.40m, 101.50m)
            .Run();

        OrderReportRow row = Assert.Single(sim.Engine.GetResult().Orders);
        Assert.Equal(0.50m, row.Slippage);
        Assert.Equal(sim.Px(101.00m), row.TriggerPrice);
    }

    [Fact]
    public void Fills_table_lists_every_fill_in_time_order_with_price_quantity_and_commission()
    {
        using SimHarness sim = StatisticsScenario.Run();

        IReadOnlyList<FillReportRow> fills = sim.Engine.GetResult().Fills;

        Assert.Equal(["T-1", "T-2", "T-3", "T-4", "T-5", "T-6", "T-7"], fills.Select(f => f.TradeId.Value));
        Assert.Equal([100.0m, 110.0m, 110.0m, 104.0m, 104.0m, 100.0m, 100.0m], fills.Select(f => f.LastPx.Value));
        Assert.Equal(
            [OrderSide.Buy, OrderSide.Sell, OrderSide.Buy, OrderSide.Sell, OrderSide.Sell, OrderSide.Buy, OrderSide.Buy],
            fills.Select(f => f.Side));
        Assert.All(fills, f => Assert.Equal(new Money(1m, Currencies.USDT), f.Commission));
        Assert.All(fills, f => Assert.Equal(LiquiditySide.Taker, f.LiquiditySide));
        Assert.All(fills, f => Assert.Equal(sim.Qty(1m), f.LastQty));
        Assert.Equal(Scripted.Ms(62_000), fills[1].TsEvent);
    }

    [Fact]
    public void Positions_table_shows_closed_positions_with_realised_figures_and_the_open_one_marked_to_market()
    {
        using SimHarness sim = StatisticsScenario.Run();

        IReadOnlyList<PositionReportRow> positions = sim.Engine.GetResult().Positions;

        PositionReportRow first = positions.Single(p => p.PositionId.Value == "BTCUSDT-PERP.SIM-Scripted-001");
        Assert.Equal(PositionSide.Long, first.EntrySide);
        Assert.Equal(PositionSide.Flat, first.Side);
        Assert.Equal(sim.Qty(0m), first.Quantity);
        Assert.Equal(sim.Qty(1m), first.PeakQuantity);
        Assert.Equal(100m, first.AvgPxOpen);
        Assert.Equal(110m, first.AvgPxClose);
        Assert.Equal(new Money(8m, Currencies.USDT), first.RealizedPnl);
        Assert.Null(first.UnrealizedPnl);
        Assert.Equal(0.08m, first.RealizedReturn);
        Assert.Equal(Scripted.Ms(2000), first.TsOpened);
        Assert.Equal(Scripted.Ms(62_000), first.TsClosed);
        Assert.Equal(TimeSpan.FromSeconds(60), first.Duration);

        PositionReportRow shortTrade = positions.Single(p => p.EntrySide == PositionSide.Short);
        Assert.Equal("BTCUSDT-PERP.SIM-Scripted-001-3", shortTrade.PositionId.Value);
        Assert.Equal(new Money(2m, Currencies.USDT), shortTrade.RealizedPnl);

        PositionReportRow open = positions.Single(p => p.Side != PositionSide.Flat);
        Assert.Equal(PositionSide.Long, open.Side);
        Assert.Equal(sim.Qty(1m), open.Quantity);
        Assert.Equal(new Money(-1m, Currencies.USDT), open.RealizedPnl);
        Assert.Equal(new Money(3m, Currencies.USDT), open.UnrealizedPnl);
        Assert.Null(open.TsClosed);
        Assert.Null(open.Duration);
    }

    [Fact]
    public void Account_table_tracks_the_balance_when_money_is_committed_and_when_it_moves()
    {
        using SimHarness sim = StatisticsScenario.Run();

        IReadOnlyList<AccountReportRow> rows = sim.Engine.GetResult().Accounts;

        // Three rows a trade: the entry order posts its margin, the fill pays the commission out of the balance and
        // the position takes over the same hold, and closing gives it all back. The balance not moving when a hold is
        // taken is the point of the row - what is free moved, and a strategy sizing its next order reads that.
        Assert.Equal(
            [
                1_000_000m,
                1_000_000m, 999_999m, 1_000_008m,
                1_000_008m, 1_000_007m, 1_000_000m,
                1_000_000m, 999_999m, 1_000_002m,
                1_000_002m, 1_000_001m,
            ],
            rows.Select(r => r.Total));
        Assert.Equal(
            [
                0m,
                5m, 5m, 0m,
                5.5m, 5.5m, 0m,
                5.2m, 5.2m, 0m,
                5m, 5m,
            ],
            rows.Select(r => r.Locked));
        Assert.All(rows, r => Assert.Equal("SIM-001", r.AccountId.Value));
        Assert.All(rows, r => Assert.Equal(Currencies.USDT, r.Currency));
        Assert.All(rows, r => Assert.Equal(r.Total - r.Locked, r.Free));
        Assert.Equal(Scripted.Ms(1000), rows[0].Timestamp);
        Assert.Equal(Scripted.Ms(2000), rows[1].Timestamp);
    }

    [Fact]
    public void Csv_writer_emits_a_header_of_column_names_and_one_invariant_culture_line_per_row()
    {
        using SimHarness sim = StatisticsScenario.Run();

        string[] lines = Lines(ReportWriter.ToCsv(sim.Engine.GetResult().Fills));

        Assert.Equal(8, lines.Length);
        Assert.Equal("TradeId,ClientOrderId,StrategyId,InstrumentId,Side,LastQty,LastPx,Commission,LiquiditySide,TsEvent", lines[0]);
        Assert.Equal("T-1,O-20250101-000002-001-001-1,Scripted-001,BTCUSDT-PERP.SIM,Buy,1.000,100.0,1.00000000 USDT,Taker,2025-01-01T00:00:02.0000000Z", lines[1]);
    }

    [Fact]
    public void Csv_writer_quotes_values_that_contain_commas_or_quotes_and_leaves_nulls_empty()
    {
        string[] lines = Lines(ReportWriter.ToCsv([new CsvProbe("a,b", "say \"hi\"", null, 1.5m)]));

        Assert.Equal("WithComma,WithQuote,Missing,Number", lines[0]);
        Assert.Equal("\"a,b\",\"say \"\"hi\"\"\",,1.5", lines[1]);
    }

    [Fact]
    public void Json_document_has_a_stable_shape_with_camel_case_names()
    {
        using SimHarness sim = StatisticsScenario.Run();

        using JsonDocument document = JsonDocument.Parse(ReportWriter.ToJson(sim.Engine.GetResult()));
        JsonElement root = document.RootElement;

        Assert.Equal(
            [
                "runId", "traderId", "runStarted", "runFinished", "backtestStart", "backtestEnd", "elapsedSeconds", "iterations", "totalEvents",
                "totalOrders", "totalPositions", "faultedStrategies", "currencies", "trades", "equityCurves", "orders", "fills", "positions", "accounts", "funding", "liquidations", "moduleCharges", "simulation", "applied", "participation",
            ],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("statistics", root.GetProperty("runId").GetString());
        Assert.Equal(Scripted.Ms(1000).Value, root.GetProperty("backtestStart").GetInt64());

        JsonElement usdt = Assert.Single(root.GetProperty("currencies").EnumerateArray());
        Assert.Equal("USDT", usdt.GetProperty("currency").GetString());
        Assert.Equal(1_000_001m, usdt.GetProperty("endingBalance").GetDecimal());
        Assert.Equal(1.25m, usdt.GetProperty("profitFactor").GetDecimal());
        Assert.Equal(7m, usdt.GetProperty("totalCommissions").GetDecimal());

        JsonElement trades = root.GetProperty("trades");
        Assert.Equal(2, trades.GetProperty("winners").GetInt32());
        Assert.Equal(120d, trades.GetProperty("averageDurationSeconds").GetDouble());

        JsonElement curve = root.GetProperty("equityCurves").GetProperty("USDT");
        // A point per timestamp of data, starting at the balance the run began with.
        Assert.Equal(8, curve.GetArrayLength());
        Assert.Equal(Scripted.Ms(1000).Value, curve[0].GetProperty("timestamp").GetInt64());
        Assert.Equal(1_000_000m, curve[0].GetProperty("equity").GetDecimal());

        JsonElement firstFill = root.GetProperty("fills")[0];
        Assert.Equal("T-1", firstFill.GetProperty("tradeId").GetString());
        Assert.Equal("buy", firstFill.GetProperty("side").GetString());
        Assert.Equal("100.0", firstFill.GetProperty("lastPx").GetString());
        Assert.Equal("1.00000000 USDT", firstFill.GetProperty("commission").GetString());
    }

    [Fact]
    public void Report_tables_survive_a_json_round_trip_unchanged()
    {
        using SimHarness sim = StatisticsScenario.Run();
        BacktestResult result = sim.Engine.GetResult();

        using JsonDocument document = JsonDocument.Parse(ReportWriter.ToJson(result, indented: false));
        JsonElement root = document.RootElement;

        Assert.Equal(result.Fills, root.GetProperty("fills").Deserialize<List<FillReportRow>>(BytexJson.Options));
        Assert.Equal(result.Orders, root.GetProperty("orders").Deserialize<List<OrderReportRow>>(BytexJson.Options));
        Assert.Equal(result.Accounts, root.GetProperty("accounts").Deserialize<List<AccountReportRow>>(BytexJson.Options));
        Assert.Equal(result.Positions, root.GetProperty("positions").Deserialize<List<PositionReportRow>>(BytexJson.Options));
        Assert.Equal(result.EquityCurves[Currencies.USDT], root.GetProperty("equityCurves").GetProperty("USDT").Deserialize<List<EquityPoint>>(BytexJson.Options));
    }

    [Fact]
    public void Unbounded_profit_factor_is_left_out_of_the_json_instead_of_being_written_as_a_huge_number()
    {
        using SimHarness sim = SimHarness.Perp();
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_000.0m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(3000, 51_000.0m, 51_000.0m)
            .Run();

        using JsonDocument document = JsonDocument.Parse(ReportWriter.ToJson(sim.Engine.GetResult()));

        JsonElement usdt = Assert.Single(document.RootElement.GetProperty("currencies").EnumerateArray());
        Assert.False(usdt.TryGetProperty("profitFactor", out _));
        Assert.Equal(949.5m, usdt.GetProperty("realizedPnl").GetDecimal()); // +1000 - 25 - 25.5
    }

    [Fact]
    public void Summary_prints_counts_and_the_per_currency_line_in_invariant_culture()
    {
        using SimHarness sim = StatisticsScenario.Run();

        string[] lines = Lines(sim.Engine.GetResult().Summary());

        Assert.Equal("Backtest statistics (TRADER-001)", lines[0]);
        Assert.Contains("  Orders:      7   Positions: 4 (closed 3, open 1)", lines);
        Assert.Contains("  [USDT] start 1,000,000.00  end 1,000,001.00  pnl 4.00 (0.00%)  realized 1.00  unrealized 3.00  fees 7.00", lines);
        Assert.Contains(lines, l => l.Contains("max drawdown 9.00 (0.00%)  sharpe 2.28  sortino 3.97  profit factor 1.25  expectancy 0.67", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_all_creates_every_report_file_in_the_target_directory()
    {
        using SimHarness sim = StatisticsScenario.Run();
        string directory = Path.Combine(Path.GetTempPath(), "bytex-backtest-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            ReportWriter.WriteAll(sim.Engine.GetResult(), directory);

            Assert.Equal(
                ["accounts.csv", "equity_USDT.csv", "fills.csv", "orders.csv", "positions.csv", "result.json", "summary.txt"],
                Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(8, Lines(File.ReadAllText(Path.Combine(directory, "fills.csv"))).Length);
            Assert.Equal(9, Lines(File.ReadAllText(Path.Combine(directory, "equity_USDT.csv"))).Length); // header and eight points
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json")));
            Assert.Equal(7, document.RootElement.GetProperty("totalOrders").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed record CsvProbe(string WithComma, string WithQuote, string? Missing, decimal Number);
}
