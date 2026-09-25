using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Bytex.Indicators;
using Xunit;

namespace Bytex.Documents.Tests;

public sealed class RegressionTests
{
    [Fact]
    public void Risk_percent_sizing_without_a_stop_is_a_blocking_finding()
    {
        string json = DocumentJson.Serialize(Fixtures.Example("ema-cross")).Replace("\"mode\": \"fixed\"", "\"mode\": \"riskPercent\"", StringComparison.Ordinal);
        StrategyDocument doc = DocumentJson.Deserialize(json);

        ValidationReport report = new DocumentValidator().Validate(doc);

        Assert.False(report.IsValid);
        Finding finding = Assert.Single(report.Findings, f => f.Code == Codes.SizingNeedsStop);
        Assert.Equal(FindingLevel.Block, finding.Level);
        Assert.Equal("buy", finding.NodeId);
        Assert.DoesNotContain(new DocumentValidator().Validate(Fixtures.Example("ema-cross")).Findings, f => f.Code == Codes.SizingNeedsStop);
    }

    [Fact]
    public void Donchian_can_exclude_the_current_bar_so_a_close_can_break_the_channel()
    {
        DonchianChannel including = new(3);
        DonchianChannel excluding = new(3, excludeCurrent: true);
        foreach ((decimal high, decimal low) in new[] { (10m, 9m), (11m, 9.5m), (12m, 10m) })
        {
            including.Update(high, low);
            excluding.Update(high, low);
        }

        Assert.True(including.IsInitialized);
        Assert.False(excluding.IsInitialized);

        including.Update(15m, 11m);
        excluding.Update(15m, 11m);

        Assert.Equal(15m, including.Upper);
        Assert.True(excluding.IsInitialized);
        Assert.Equal(12m, excluding.Upper);
        Assert.Equal(9m, excluding.Lower);
        Assert.Equal(10.5m, excluding.Middle);
    }

    [Fact]
    public void The_order_decision_is_recorded_before_its_fill()
    {
        (_, DocumentStrategy strategy) = Fixtures.RunBacktest(Fixtures.Example("ema-cross"));
        List<StrategyEvent> decisions = strategy.Decisions.ToList();
        List<StrategyEvent> fills = decisions.Where(d => d.Kind == "fill" && d.Values.ContainsKey("clientOrderId")).ToList();
        Assert.NotEmpty(fills);
        foreach (StrategyEvent fill in fills)
        {
            string id = fill.Values["clientOrderId"];
            int order = decisions.FindIndex(d => d.Kind == "order" && d.Values.TryGetValue("clientOrderId", out string? v) && v == id);
            if (order >= 0)
            {
                Assert.True(order < decisions.IndexOf(fill), $"fill for {id} was recorded before its order");
            }
        }

        Assert.Contains(fills, f => decisions.Any(d => d.Kind == "order" && d.Values.TryGetValue("clientOrderId", out string? v) && v == f.Values["clientOrderId"]));
    }

    private const string ScaleOutDocument = """
        {
          "schemaVersion": "1.0",
          "id": "scale-out",
          "name": "Scale out at 1R, target at 3R",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 10 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 30 } },
            { "id": "atr", "type": "ind.atr", "params": { "period": 14 } },
            { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.4 }, "onlyWhenFlat": true } },
            { "id": "exit", "type": "risk.exit", "params": { "stop": { "anchor": "entry", "offset": { "unit": "atr", "value": 2 } }, "target": { "unit": "r", "value": 3 }, "trail": { "enabled": false } } },
            { "id": "oneR", "type": "cond.compare", "params": { "op": "gte", "value": 1 } },
            { "id": "firstTime", "type": "cond.once" },
            { "id": "takeHalf", "type": "act.scaleOut", "params": { "fraction": 50 } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "bars:bars", "to": "atr:bars" },
            { "from": "fast:value", "to": "crossUp:value" },
            { "from": "slow:value", "to": "crossUp:reference" },
            { "from": "crossUp:out", "to": "buy:trigger" },
            { "from": "buy:position", "to": "exit:position" },
            { "from": "atr:value", "to": "exit:atr" },
            { "from": "exit:rMultiple", "to": "oneR:a" },
            { "from": "oneR:out", "to": "firstTime:in" },
            { "from": "exit:closed", "to": "firstTime:reset" },
            { "from": "firstTime:out", "to": "takeHalf:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    [Fact]
    public void Exit_orders_follow_a_scale_out_and_never_open_the_opposite_side()
    {
        StrategyDocument doc = DocumentJson.Deserialize(ScaleOutDocument);
        Assert.True(new DocumentValidator().Validate(doc).IsValid);

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 6000);

        Assert.Contains(strategy.Decisions, d => d.Kind == "scaleOut");
        Assert.Contains(strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("exit orders resized", StringComparison.Ordinal));
        Assert.True(result.Positions.Count > 3, $"expected several round trips, got {result.Positions.Count} positions");
        Assert.DoesNotContain(result.Positions, p => p.EntrySide == PositionSide.Short);
        Assert.DoesNotContain(result.Positions, p => p.PeakQuantity.Value > 0.4m);
    }

    [Fact]
    public void A_cash_account_refuses_a_derivative_and_a_margin_account_takes_it()
    {
        CryptoPerpetual perp = new(new InstrumentSpec
        {
            Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), Fixtures.Sim),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.001m, 3),
            MarginInit = 0.05m,
            MarginMaint = 0.025m,
        });

        using BacktestEngine cash = new(new BacktestEngineConfig { RunId = "cash" });
        cash.AddInstrument(perp);
        ArgumentException refused = Assert.Throws<ArgumentException>(() => cash.AddVenue(new SimulatedVenueConfig { Venue = Fixtures.Sim, AccountType = AccountType.Cash, StartingBalances = [new Money(100_000m, Currencies.USDT)] }));
        Assert.Contains("margin", refused.Message, StringComparison.OrdinalIgnoreCase);

        using BacktestEngine margin = new(new BacktestEngineConfig { RunId = "margin" });
        margin.AddInstrument(perp);
        margin.AddVenue(new SimulatedVenueConfig { Venue = Fixtures.Sim, AccountType = AccountType.Margin, StartingBalances = [new Money(100_000m, Currencies.USDT)] });
    }
}
