using System.Globalization;
using System.Text.Json;
using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a sizing mode says how to size and cannot say how much of the account may be in one order, and riskPercent is
// where that bites: the nearer the stop, the larger the position it buys, so "risk one percent" with a stop a tenth of
// a percent away is the whole account. Eight shipped templates did exactly that. The ceiling is the missing half of
// the rule - a bound the mode is not allowed past - and what is pinned here is that it holds at the value set, that
// the smaller of the two ceilings wins, that it says so in the run log, and that a document without one sizes exactly
// as it did before the ceiling existed.
public sealed class SizingCeilingTests
{
    private const decimal Balance = 10_000m;

    /// <summary>
    /// One entry, sized as the test asks, with a three-bar average as its stop input - close to the price, which is
    /// what makes riskPercent ask for a position the size of the account. Nothing else: what is being measured is the
    /// quantity the order went in with.
    /// </summary>
    private static string Document(string sizing) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "sizing-ceiling",
          "name": "Size one entry",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "avg", "type": "ind.ema", "params": { "period": 3 } },
            { "id": "gate", "type": "cond.compare", "params": { "op": "gt", "value": "0" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "onlyWhenFlat": true, "sizing": {{sizing}} } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "avg:bars" },
            { "from": "avg:value", "to": "gate:a" },
            { "from": "gate:out", "to": "buy:trigger" },
            { "from": "avg:value", "to": "buy:stop" }
          ]
        }
        """;

    private static (decimal Quantity, decimal Notional, DocumentStrategy Strategy) Run(string sizing)
    {
        StrategyDocument document = DocumentJson.Deserialize(Document(sizing));
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "sizing-ceiling" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(Balance, Currencies.USDT)],
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        });
        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, 40).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new Core.Model.Identifiers.StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();

        Position position = Assert.Single(engine.Kernel.Cache.Positions());
        decimal quantity = position.PeakQuantity.Value;
        return (quantity, quantity * position.AvgPxOpen, strategy);
    }

    /// <summary>What share of the account the position was worth when it opened.</summary>
    private static decimal Share(decimal notional) => notional / Balance * 100m;

    [Fact]
    public void Without_a_ceiling_a_near_stop_still_stakes_the_account()
    {
        // The case of record, and the reason the ceiling exists: risk one percent, and the position is the account.
        (_, decimal notional, _) = Run("""{ "mode": "riskPercent", "value": "1" }""");

        Assert.InRange(Share(notional), 95m, 100m);
    }

    [Theory]
    [InlineData("25", 25)]
    [InlineData("10", 10)]
    [InlineData("5", 5)]
    public void A_ceiling_as_a_share_of_the_balance_holds_at_the_value_it_was_set_to(string percent, decimal expected)
    {
        (_, decimal notional, _) = Run($$"""{ "mode": "riskPercent", "value": "1", "maxPercentOfBalance": "{{percent}}" }""");

        // Within a tenth of a percent of the account: the rest is the instrument's size step.
        Assert.InRange(Share(notional), expected - 0.1m, expected);
    }

    [Fact]
    public void A_ceiling_as_an_amount_holds_in_the_quote_currency()
    {
        (_, decimal notional, _) = Run("""{ "mode": "riskPercent", "value": "1", "maxNotional": "2500" }""");

        Assert.InRange(notional, 2_450m, 2_500m);
    }

    [Fact]
    public void With_both_set_the_smaller_one_holds()
    {
        // 2,500 USDT against 10% of a 10,000 balance: the thousand wins, whichever way round they are written.
        (_, decimal byPercent, _) = Run("""{ "mode": "riskPercent", "value": "1", "maxNotional": "2500", "maxPercentOfBalance": "10" }""");
        (_, decimal byAmount, _) = Run("""{ "mode": "riskPercent", "value": "1", "maxNotional": "800", "maxPercentOfBalance": "50" }""");

        Assert.InRange(Share(byPercent), 9.9m, 10m);
        Assert.InRange(byAmount, 750m, 800m);
    }

    [Fact]
    public void A_ceiling_no_mode_reaches_changes_nothing()
    {
        // The ceiling is a bound, not a target: a mode asking for less than it allows is left alone.
        (decimal capped, decimal cappedNotional, _) = Run("""{ "mode": "notional", "value": "1000", "maxPercentOfBalance": "50" }""");
        (decimal plain, decimal plainNotional, _) = Run("""{ "mode": "notional", "value": "1000" }""");

        Assert.Equal(plain, capped);
        Assert.Equal(plainNotional, cappedNotional);
        Assert.InRange(Share(cappedNotional), 9m, 10m);
    }

    [Theory]
    [InlineData("""{ "mode": "fixed", "value": "0.1" }""")]
    [InlineData("""{ "mode": "notional", "value": "2000" }""")]
    [InlineData("""{ "mode": "percentOfBalance", "value": "30" }""")]
    [InlineData("""{ "mode": "riskPercent", "value": "1" }""")]
    public void A_document_written_before_the_ceiling_existed_sizes_exactly_as_it_did(string sizing)
    {
        // Zero is no ceiling, and an absent field is zero: every mode with the fields written out as zeros has to give
        // the same quantity as the same mode without them. A default that changed a shipped document would be a defect.
        (decimal without, _, _) = Run(sizing);
        (decimal withZeros, _, _) = Run(sizing.TrimEnd('}', ' ') + """, "maxNotional": "0", "maxPercentOfBalance": "0" }""");

        Assert.Equal(without, withZeros);
    }

    [Fact]
    public void The_run_log_says_the_ceiling_held_it_back()
    {
        // A position smaller than the mode asked for, with nothing to say why, is the kind of surprise this engine
        // does not leave behind: the decision log names what was asked for and what was allowed.
        (_, _, DocumentStrategy strategy) = Run("""{ "mode": "riskPercent", "value": "1", "maxPercentOfBalance": "5" }""");

        StrategyEvent capped = Assert.Single(strategy.Decisions, d => d.Kind == "sizing");
        Assert.Contains("sizing capped", capped.Message, StringComparison.Ordinal);
        Assert.Contains("riskPercent asked for", capped.Message, StringComparison.Ordinal);
        Assert.Contains("the ceiling allows", capped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ceiling_of_ten_dollars_buys_ten_dollars()
    {
        // The ceiling is the ceiling however small it is: the position is worth what was allowed and not a step more,
        // down to whatever the instrument can express. Below that, sizing produces nothing and the order is not sent -
        // which is the same rule that has always applied to a size under the venue's minimum.
        (decimal quantity, decimal notional, _) = Run("""{ "mode": "riskPercent", "value": "1", "maxNotional": "10" }""");

        Assert.InRange(notional, 9m, 10m);
        Assert.True(quantity > 0m, "nothing was bought at all");
    }

    [Fact]
    public void The_catalog_offers_the_ceiling_wherever_it_offers_sizing()
    {
        // Four node types share the sizing parameter, and a ceiling that existed on one of them would be a trap.
        foreach (string type in new[] { "act.order", "act.bracket", "act.ladder", "risk.sizing" })
        {
            Catalog.ParamSpec sizing = Assert.IsType<Catalog.ParamSpec>(Catalog.NodeCatalog.Default.Find(type)!.Param("sizing"));
            foreach (string field in new[] { "maxNotional", "maxPercentOfBalance" })
            {
                Catalog.ParamSpec spec = Assert.Single(sizing.Fields!, f => f.Name == field);
                Assert.Equal("0", spec.Default);
                Assert.False(string.IsNullOrWhiteSpace(spec.Unit), $"{type} {field} does not say what it is counted in");
                Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{type} {field} does not say what it does");
            }
        }
    }

    [Fact]
    public void The_mode_that_needs_the_ceiling_points_at_it()
    {
        // R13.13's rule applied to the thing that prompted R13.13: riskPercent used to say nothing could bound it.
        Catalog.ParamSpec mode = Assert.Single(
            Catalog.NodeCatalog.Default.Find("act.order")!.Param("sizing")!.Fields!, f => f.Name == "mode");
        Catalog.ChoiceSpec risk = Assert.Single(mode.Choices!, c => c.Value == "riskPercent");

        Assert.Contains("maxPercentOfBalance", risk.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("the only ceiling is", risk.Description, StringComparison.Ordinal);
    }
}
