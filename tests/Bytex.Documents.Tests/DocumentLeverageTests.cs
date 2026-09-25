using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: leverage is what a strategy is written for, and until now a document could not say so - it was set on the
// venue in whatever run happened to be assembled. A strategy written for ten times leverage and run at one is not the
// same strategy sized smaller; it is a different one, and a run that quietly did that would be read as evidence about
// the strategy somebody wrote. What is pinned here is that a document can state it, that a venue granting less
// refuses to trade it rather than trading something else, and that a document saying nothing behaves as it always
// did.
public sealed class DocumentLeverageTests
{
    private const decimal Balance = 10_000m;

    private static CryptoPerpetual Perp() => new(new InstrumentSpec
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

    private static string Document(string? account) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "document-leverage",
          "name": "Size one entry",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT-PERP.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          {{(account is null ? "" : $"\"account\": {account},")}}
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "avg", "type": "ind.ema", "params": { "period": 3 } },
            { "id": "gate", "type": "cond.compare", "params": { "op": "gt", "value": "0" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "onlyWhenFlat": true, "sizing": { "mode": "notional", "value": "10000000" } } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "avg:bars" },
            { "from": "avg:value", "to": "gate:a" },
            { "from": "gate:out", "to": "buy:trigger" }
          ]
        }
        """;

    private static (IReadOnlyList<Position> Positions, int Orders) Run(string? account, decimal venueLeverage)
    {
        (IReadOnlyList<Position> positions, int orders, _) = RunFully(account, venueLeverage);
        return (positions, orders);
    }

    private static (IReadOnlyList<Position> Positions, int Orders, BacktestResult Result) RunFully(string? account, decimal venueLeverage)
    {
        StrategyDocument document = DocumentJson.Deserialize(Document(account));
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument traded = Perp();
        BarType barType = Fixtures.MinuteBars(traded);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "document-leverage" });
        engine.AddInstrument(traded);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(Balance, Currencies.USDT)],
            DefaultLeverage = venueLeverage,
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        });
        engine.AddData(Fixtures.RandomWalkBars(traded, barType, 40).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();

        return (engine.Kernel.Cache.Positions(), engine.Kernel.Cache.Orders().Count, engine.GetResult());
    }

    [Fact]
    public void A_document_says_what_leverage_it_is_written_for()
    {
        StrategyDocument document = DocumentJson.Deserialize(Document("""{ "leverage": 10 }"""));

        Assert.Equal(10m, document.Account.Leverage);
    }

    [Fact]
    public void A_document_that_says_nothing_is_written_for_none()
    {
        // The default has to be 1, or every document written before the field existed would change what it does.
        StrategyDocument document = DocumentJson.Deserialize(Document(null));

        Assert.Equal(1m, document.Account.Leverage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    public void Leverage_below_one_is_refused_by_the_validator(string leverage)
    {
        StrategyDocument document = DocumentJson.Deserialize(Document($$"""{ "leverage": {{leverage}} }"""));

        ValidationReport report = new DocumentValidator().Validate(document);

        Assert.False(report.IsValid);
        Assert.Contains(Codes.LeverageInvalid, report.Findings.Select(f => f.Code));
    }

    [Fact]
    public void The_leverage_a_document_asks_for_is_the_leverage_it_is_sized_at()
    {
        // The venue grants what the document asks: ten times the balance in notional, less a little for commission.
        (IReadOnlyList<Position> positions, _) = Run("""{ "leverage": 10 }""", venueLeverage: 10m);

        Position position = Assert.Single(positions);
        decimal multiple = position.PeakQuantity.Value * position.AvgPxOpen / Balance;
        Assert.InRange(multiple, 9.5m, 10m);
    }

    [Fact]
    public void A_venue_that_grants_less_than_the_document_asks_will_not_trade_it()
    {
        // The case the field exists for. Sized at 1x this is not the strategy the document describes, and a result
        // from it would be read as evidence about a strategy nobody wrote - so nothing is traded at all.
        (IReadOnlyList<Position> positions, int orders) = Run("""{ "leverage": 10 }""", venueLeverage: 1m);

        Assert.Empty(positions);
        Assert.Equal(0, orders);
    }

    [Fact]
    public void A_refusal_is_on_the_record_and_not_only_in_the_log()
    {
        // The defect: the run ended with no orders, exit code 0 and a log line. That is indistinguishable from a
        // strategy whose conditions never came true, and the difference is the whole point - one found nothing to do,
        // the other would not run at all.
        (_, _, BacktestResult result) = RunFully("""{ "leverage": 10 }""", venueLeverage: 1m);

        Assert.Equal(["Doc-001"], result.FaultedStrategies);
        Assert.Contains("STOPPED:", result.Summary(), StringComparison.Ordinal);
        Assert.Contains("Doc-001", result.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_went_through_says_nothing_about_faults()
    {
        (_, _, BacktestResult result) = RunFully("""{ "leverage": 10 }""", venueLeverage: 10m);

        Assert.Empty(result.FaultedStrategies);
        Assert.DoesNotContain("STOPPED:", result.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_venue_that_grants_more_than_the_document_asks_is_no_objection()
    {
        // The document is a requirement, not a ceiling: a venue with room to spare is not a reason to refuse. What it
        // is sized at is what the account holds, which is the venue's business.
        (IReadOnlyList<Position> positions, int orders) = Run("""{ "leverage": 5 }""", venueLeverage: 20m);

        Assert.Single(positions);
        Assert.True(orders > 0, "nothing was sent");
    }

    [Fact]
    public void A_document_written_before_the_field_existed_runs_on_any_venue()
    {
        (IReadOnlyList<Position> positions, int orders) = Run(null, venueLeverage: 1m);

        Assert.Single(positions);
        Assert.True(orders > 0, "nothing was sent");
    }
}
