using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Xunit;

namespace Bytex.Documents.Tests;

// A node warms its indicators from history before it starts. Those per-bar values existed inside the runtime and
// were discarded, so anything drawing the indicator could only begin its line where the node began - on a
// five-minute bar, an hour of nothing. An indicator nobody can check by eye is precisely what a person is trying
// to see when they ask what the strategy is waiting on.
//
// Recomputing them outside the engine is not an answer: a second implementation cannot verify the first. If the
// two agreed it would prove nothing, and if they disagreed nobody could say which was right. So the engine hands
// over what it already computed.
public sealed class WarmupValuesTests
{
    private const string EmaCross = """
        {
          "schemaVersion": "2.0",
          "id": "warmup-values",
          "name": "Warm up and watch",
          "instruments": [ { "ref": "primary", "marketKey": "bx-market:v2/SIM/BTCUSDT" } ],
          "candleSeriesDefinitions": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "provider" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "candleSeries": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
            { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "fast:value", "to": "crossUp:value" },
            { "from": "slow:value", "to": "crossUp:reference" },
            { "from": "crossUp:out", "to": "buy:trigger" }
          ]
        }
        """;

    private static DocumentStrategy Run(int warmupBars, int liveBars)
    {
        Instrument instrument = Fixtures.BtcUsdt();
        CandleSeries candleSeries = Fixtures.MinuteBars(instrument);
        IReadOnlyList<Bar> all = Fixtures.RandomWalkBars(instrument, candleSeries, warmupBars + liveBars);

        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "warmup-values" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT)],
        });

        // The first bars are handed over as warm-up history; the rest arrive as the run.
        engine.AddData(all.Skip(warmupBars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig
        {
            Document = DocumentJson.Deserialize(EmaCross),
            StrategyId = new StrategyId("Doc-001"),
            WarmupHistory = [.. all.Take(warmupBars)],
        });
        engine.AddStrategy(strategy);
        engine.Run();
        return strategy;
    }

    /// <summary><b>The warm-up hands over a value for each bar it was computed on.</b></summary>
    [Fact]
    public void Every_warmup_bar_carries_what_the_nodes_produced_on_it()
    {
        DocumentStrategy strategy = Run(warmupBars: 120, liveBars: 10);

        Assert.NotEmpty(strategy.WarmupValues);

        // Oldest first, and each point is a distinct bar.
        long[] stamps = [.. strategy.WarmupValues.Select(p => p.BarTs)];
        Assert.Equal(stamps.OrderBy(s => s), stamps);
        Assert.Equal(stamps.Length, stamps.Distinct().Count());

        // And the indicator is actually in there, not just the bar.
        Assert.Contains(strategy.WarmupValues, p => p.Values.ContainsKey("fast:value"));
        Assert.Contains(strategy.WarmupValues, p => p.Values.ContainsKey("slow:value"));
    }

    /// <summary>
    /// The series ends where the live stream begins - every warm-up bar is older than the bar the run finished on.
    /// A line drawn from this and continued from the live values is one continuous line, with no overlap.
    /// </summary>
    [Fact]
    public void The_series_ends_where_the_live_run_begins()
    {
        DocumentStrategy strategy = Run(warmupBars: 120, liveBars: 20);
        DocumentMonitorView view = Assert.IsType<DocumentMonitorView>(strategy.MonitorView());

        long lastWarmup = strategy.WarmupValues[^1].BarTs;
        long lastLive = Assert.IsType<DocumentMonitorFrame>(view.Frame).BarTs;

        Assert.True(lastWarmup < lastLive, $"the warm-up ends at {lastWarmup}, which is not before the run's last bar at {lastLive}");
    }

    /// <summary>And the numbers are numbers: every value parses, so nothing hands over a formatted placeholder.</summary>
    [Fact]
    public void Every_value_handed_over_parses_as_a_number()
    {
        DocumentStrategy strategy = Run(warmupBars: 120, liveBars: 5);
        int checked_ = 0;

        foreach ((long _, IReadOnlyDictionary<string, string> values) in strategy.WarmupValues)
        {
            foreach (string key in new[] { "fast:value", "slow:value" })
            {
                if (values.TryGetValue(key, out string? text))
                {
                    Assert.True(
                        decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
                        $"{key} handed over '{text}', which is not a number");
                    checked_++;
                }
            }
        }

        Assert.True(checked_ > 0, "no indicator values were handed over at all, so this test checked nothing");
    }

    /// <summary>
    /// It is the warm-up, not a rolling history: the run that follows does not add to it, so a node left running
    /// for a week does not accumulate a week of values here.
    /// </summary>
    [Fact]
    public void The_run_does_not_keep_adding_to_it()
    {
        DocumentStrategy shortRun = Run(warmupBars: 120, liveBars: 5);
        DocumentStrategy longRun = Run(warmupBars: 120, liveBars: 200);

        Assert.Equal(shortRun.WarmupValues.Count, longRun.WarmupValues.Count);
    }

    /// <summary>A node given no history has nothing to hand over, and says so by being empty rather than by throwing.</summary>
    [Fact]
    public void Without_history_there_is_nothing_to_hand_over()
    {
        DocumentStrategy strategy = Run(warmupBars: 0, liveBars: 50);

        Assert.Empty(strategy.WarmupValues);
    }
}
