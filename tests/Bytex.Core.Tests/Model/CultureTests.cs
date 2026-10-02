using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using Bytex.Core.Tests.Support;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Why: this library is consumed by hosts, and a host chooses its own culture. Price, Quantity, Money and UnixNanos
// format invariantly of their own accord, so most text the engine produces is safe by construction - but a raw decimal
// interpolated into a string is not, and the engine puts plenty of them into descriptions, denial reasons and
// exceptions that a host stores, matches on, or shows to somebody.
//
// The repository builds with InvariantGlobalization, which means the current culture IS the invariant culture here and
// no ordinary test can tell the difference. CommaDecimalCulture builds the hostile culture by hand and proves it took;
// everything below then asserts that the text did not move.
public sealed class CultureTests
{
    private static CurrencyPair Btc() => new(BtcUsdtSpec());

    [Fact]
    public void An_orders_own_description_reads_the_same_under_any_culture()
    {
        // The trailing offset is a bare decimal - the only number in an order that is not a Price or a Quantity.
        TrailingStopMarketOrder order = TrailingStopMarketOrder.Create(
            TestOrders.Params("O-1", MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT"), OrderSide.Sell, "0.500"),
            D("25.5"),
            TrailingOffsetType.Price);

        string invariant = order.Info();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, order.Info());
        }

        Assert.Contains("25.5", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void A_positions_own_description_reads_the_same_under_any_culture()
    {
        // avg_px_open is a bare decimal, and this string is what a log line and a debugger show.
        CurrencyPair btc = Btc();
        Position position = new(btc, Fill(btc, OrderSide.Buy, "0.500000", "50123.45", "T-1"));

        string invariant = position.ToString();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, position.ToString());
        }

        Assert.Contains("avg_px_open=50123.45", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void Market_data_reads_the_same_under_any_culture()
    {
        CurrencyPair btc = Btc();
        QuoteTick quote = new(btc.Id, Price.Parse("50000.10"), Price.Parse("50000.20"), Quantity.Parse("1.500000"), Quantity.Parse("2.250000"), T0, T0);
        TradeTick trade = new(btc.Id, Price.Parse("50000.15"), Quantity.Parse("0.750000"), AggressorSide.Buyer, new TradeId("T-1"), T0, T0);
        Bar bar = new(new CandleSeries(btc.Id, new SamplingRule(1, SamplingMethod.Minute, PriceType.Last)),
            Price.Parse("1.10"), Price.Parse("2.20"), Price.Parse("0.90"), Price.Parse("1.50"), Quantity.Parse("1234.500000"), T0, T0);

        string[] invariant = [quote.ToString(), trade.ToString(), bar.ToString()];

        using (new CommaDecimalCulture())
        {
            string[] hostile = [quote.ToString(), trade.ToString(), bar.ToString()];

            Assert.Equal(invariant, hostile);
        }

        Assert.Contains("50000.10", invariant[0], StringComparison.Ordinal);
        Assert.Contains("1234.500000", invariant[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_a_host_reads_is_the_same_under_any_culture()
    {
        // The wire format. System.Text.Json writes numbers invariantly itself; what this pins is everything the engine
        // hands it as a STRING - a price, a quantity, a money amount - each of which formats itself on the way in.
        CurrencyPair btc = Btc();
        Position position = new(btc, Fill(btc, OrderSide.Buy, "0.500000", "50123.45", "T-1"));
        Bar bar = new(new CandleSeries(btc.Id, new SamplingRule(1, SamplingMethod.Minute, PriceType.Last)),
            Price.Parse("1.10"), Price.Parse("2.20"), Price.Parse("0.90"), Price.Parse("1.50"), Quantity.Parse("1234.500000"), T0, T0);

        string invariantBar = BytexJson.Serialize(bar);
        string invariantMoney = BytexJson.Serialize(position.RealizedPnl);
        string invariantPrice = BytexJson.Serialize(Price.Parse("50000.10"));

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariantBar, BytexJson.Serialize(bar));
            Assert.Equal(invariantMoney, BytexJson.Serialize(position.RealizedPnl));
            Assert.Equal(invariantPrice, BytexJson.Serialize(Price.Parse("50000.10")));

            // And it reads back as the same number, which is the half that would corrupt data rather than look odd.
            Assert.Equal(Price.Parse("50000.10"), BytexJson.Deserialize<Price>(invariantPrice));
        }
    }

    [Fact]
    public void A_refusal_that_names_a_number_names_the_same_number_under_any_culture()
    {
        // Two messages a person acts on: the leverage a venue grants, and a margin tier that cannot be right. Both put
        // bare decimals in front of somebody who has to compare them with a venue's own documentation.
        CryptoPerpetual perp = new(EthPerpSpec() with { MaxLeverage = 12.5m });
        string leverage = Assert.Throws<ArgumentOutOfRangeException>(
            () => LeverageGuard.EnsureGranted(50m, [perp], "TEST")).Message;
        string tiers = Assert.Throws<ArgumentException>(
            () => new TieredMarginModel([new MarginTier(0m, 0.5m, 0.75m)])).Message;
        string unordered = Assert.Throws<ArgumentException>(
            () => new TieredMarginModel([new MarginTier(0m, 0.1m, 0.05m), new MarginTier(-1.5m, 0.2m, 0.1m)])).Message;

        using (new CommaDecimalCulture())
        {
            Assert.Equal(leverage, Assert.Throws<ArgumentOutOfRangeException>(() => LeverageGuard.EnsureGranted(50m, [perp], "TEST")).Message);
            Assert.Equal(tiers, Assert.Throws<ArgumentException>(() => new TieredMarginModel([new MarginTier(0m, 0.5m, 0.75m)])).Message);
            Assert.Equal(unordered, Assert.Throws<ArgumentException>(() => new TieredMarginModel([new MarginTier(0m, 0.1m, 0.05m), new MarginTier(-1.5m, 0.2m, 0.1m)])).Message);
        }

        Assert.Contains("12.5x", leverage, StringComparison.Ordinal);
        Assert.Contains("0.75", tiers, StringComparison.Ordinal);
        Assert.Contains("starts at -1.5", unordered, StringComparison.Ordinal);
    }

    [Fact]
    public void Why_an_order_was_denied_reads_the_same_under_any_culture()
    {
        // OrderDenied.Reason is a field a host stores, shows and sometimes matches on. The leverage in the margin
        // refusal is a bare decimal; the amounts beside it are Money and format themselves.
        string invariant = MarginRefusal();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, MarginRefusal());
        }

        Assert.StartsWith("INSUFFICIENT_MARGIN", invariant, StringComparison.Ordinal);
        Assert.Contains("12.5x leverage", invariant, StringComparison.Ordinal);
    }

    private static string MarginRefusal()
    {
        CryptoPerpetual perp = new(EthPerpSpec());
        RiskHarness harness = new();
        harness.Cache.AddInstrument(perp);
        MarginAccount account = new(TestEvents.MarginState(TestIds.BinanceAccount, (Currencies.USDT, 10m, 0m)));
        account.SetDefaultLeverage(12.5m);
        harness.Cache.AddAccount(account);

        harness.Submit(TestOrders.Limit("O-1", perp.Id, OrderSide.Buy, "1.000", "2000.00"));

        return Assert.Single(harness.Denied).Reason;
    }
}
