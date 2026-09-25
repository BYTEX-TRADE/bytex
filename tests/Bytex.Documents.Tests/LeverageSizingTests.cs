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

// Why: a document could never ask for more than its free balance could buy outright, whatever leverage the venue
// granted. That made the setting mean nothing a user could see and put liquidation out of reach of anything a
// document does. What is pinned here is that leverage now moves the size a document can take, and that a cash
// account is left exactly where it was.
public sealed class LeverageSizingTests
{
    private const decimal Balance = 10_000m;

    /// <summary>Linear perpetual settled in USDT, 5% initial margin: the instrument leverage is about.</summary>
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

    /// <summary>One entry that asks for far more than the balance could buy outright, so only the cap decides.</summary>
    private static string Document(string instrumentId) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "leverage-sizing",
          "name": "Size one entry",
          "instruments": [ { "ref": "primary", "instrumentId": "{{instrumentId}}" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
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

    /// <summary>The notional the position opened with, which is what the cap decides.</summary>
    private static decimal Notional(decimal leverage, AccountType accountType = AccountType.Margin, Instrument? instrument = null)
    {
        Instrument traded = instrument ?? Perp();
        StrategyDocument document = DocumentJson.Deserialize(Document(traded.Id.ToString()));
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        BarType barType = Fixtures.MinuteBars(traded);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "leverage-sizing" });
        engine.AddInstrument(traded);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = accountType,
            StartingBalances = [new Money(Balance, Currencies.USDT)],
            DefaultLeverage = leverage,
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        });
        engine.AddData(Fixtures.RandomWalkBars(traded, barType, 40).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();

        Position position = Assert.Single(engine.Kernel.Cache.Positions());
        return position.PeakQuantity.Value * position.AvgPxOpen;
    }

    [Fact]
    public void Without_leverage_a_document_cannot_ask_for_more_than_its_balance()
    {
        // 5% initial margin at 1x still means the whole balance backs the position outright: a leverage of one is not
        // a licence to post a twentieth of the notional.
        Assert.InRange(Notional(1m) / Balance, 0.95m, 1m);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public void Leverage_moves_what_a_document_can_take(decimal leverage)
    {
        // The balance carries the leverage it was granted, less a little left for commission. Before this the answer
        // was the same at every leverage, which is what made the setting meaningless.
        decimal multiple = Notional(leverage) / Balance;

        Assert.InRange(multiple, leverage * 0.95m, leverage);
    }

    [Fact]
    public void A_cash_account_is_left_exactly_where_it_was()
    {
        // Nothing lends on a cash account, whatever a venue's default leverage says, so a spot document sizes as it
        // always did.
        decimal one = Notional(1m, AccountType.Cash, Fixtures.BtcUsdt());
        decimal ten = Notional(10m, AccountType.Cash, Fixtures.BtcUsdt());

        Assert.Equal(one, ten);
        Assert.InRange(one / Balance, 0.95m, 1m);
    }
}
