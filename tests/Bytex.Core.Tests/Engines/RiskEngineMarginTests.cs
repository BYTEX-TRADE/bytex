using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.7 - the engine asked a cash account whether it could pay for an order and never asked a margin account
// whether it could hold one, so a node could submit a position its account had no margin for and learn about it from
// the venue, or from a position nobody could carry. What is pinned here is the question it now asks: the initial
// margin at the account's own leverage against the balance that is free of the margin already committed, with
// anything that reduces let through, and the figures named in the denial.
public class RiskEngineMarginTests
{
    private const decimal Price = 50_000m;

    /// <summary>A perpetual on a margin account: 5% initial margin, 2.5% maintenance, leverage as the test says.</summary>
    private static CryptoPerpetual Perp() => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), TestIds.Binance),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.05m,
        MarginMaint = 0.025m,
        MakerFee = 0m,
        TakerFee = 0m,
    });

    private static RiskHarness Harness(decimal total, decimal locked, decimal leverage = 1m, RiskEngineConfig? config = null)
    {
        CryptoPerpetual instrument = Perp();
        RiskHarness h = new(config);
        h.Cache.AddInstrument(instrument);
        MarginAccount account = new(TestEvents.MarginState(TestIds.BinanceAccount, (Currencies.USDT, total, locked)));
        account.SetDefaultLeverage(leverage);
        h.Cache.AddAccount(account);
        return h;
    }

    private static LimitOrder Buy(string id, string quantity) =>
        TestOrders.Limit(id, Perp().Id, OrderSide.Buy, quantity, "50000.00");

    [Theory]
    // 1 BTC at 50,000 is 50,000 of notional. What has to be posted is the larger of the leverage share and the
    // instrument's 5% floor: the whole 50,000 at 1x, 5,000 at 10x, 2,500 at 20x - and still 2,500 at 50x, because
    // the floor binds and leverage past 20x on this instrument buys nothing.
    [InlineData(1, 60_000, true)]
    [InlineData(1, 40_000, false)]
    [InlineData(10, 6_000, true)]
    [InlineData(10, 4_000, false)]
    [InlineData(20, 3_000, true)]
    [InlineData(20, 2_000, false)]
    [InlineData(50, 3_000, true)]
    [InlineData(50, 2_000, false)]
    public void An_order_is_judged_against_the_margin_it_needs_at_the_accounts_leverage(decimal leverage, decimal free, bool allowed)
    {
        RiskHarness h = Harness(total: free, locked: 0m, leverage: leverage);

        h.Submit(Buy("O-1", "1.000"));

        if (allowed)
        {
            Assert.Single(h.Forwarded);
            Assert.Equal(0, h.Engine.MarginDeniedCount);
        }
        else
        {
            Assert.Empty(h.Forwarded);
            OrderDenied denied = Assert.Single(h.Denied);
            Assert.StartsWith("INSUFFICIENT_MARGIN", denied.Reason, StringComparison.Ordinal);
            Assert.Contains(new Money(Price * Math.Max(1m / leverage, 0.05m), Currencies.USDT).ToString(), denied.Reason, StringComparison.Ordinal);
            Assert.Contains(new Money(free, Currencies.USDT).ToString(), denied.Reason, StringComparison.Ordinal);
            Assert.Contains($"{leverage}x", denied.Reason, StringComparison.Ordinal);
            Assert.Equal(1, h.Engine.MarginDeniedCount);
        }
    }

    [Fact]
    public void The_margin_already_committed_is_not_free_for_another_order()
    {
        // 3,000 in the account with 2,500 posted against a position it already holds: 500 free, and this order needs
        // 2,500. The venue reports committed margin as the locked part of the balance, which is what "free" means.
        RiskHarness h = Harness(total: 3_000m, locked: 2_500m);

        h.Submit(Buy("O-1", "1.000"));

        Assert.StartsWith("INSUFFICIENT_MARGIN", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_order_that_reduces_is_never_denied_for_margin()
    {
        // Nothing is free, and both of these still go: one says reduce-only, the other is the other side of a
        // position the account holds. Closing a position gives margin back rather than asking for more.
        RiskHarness h = Harness(total: 2_500m, locked: 2_500m);
        h.AddPosition(Perp(), OrderSide.Buy, "1.000", "50000.00");

        h.Submit(TestOrders.Market("O-REDUCE-ONLY", Perp().Id, OrderSide.Buy, "0.100", reduceOnly: true));
        h.Submit(TestOrders.Limit("O-CLOSING", Perp().Id, OrderSide.Sell, "1.000", "50000.00"));

        Assert.Equal(2, h.Forwarded.Count);
        Assert.Empty(h.Denied);
        Assert.Equal(0, h.Engine.MarginDeniedCount);
    }

    [Fact]
    public void A_quote_quantity_order_is_judged_on_the_size_it_converts_to()
    {
        // 50,000 USDT of a 50,000 instrument is 1 BTC, so it needs the same 2,500 at 20x as the order above and not
        // 2,500 times a price.
        RiskHarness h = Harness(total: 2_000m, locked: 0m, leverage: 20m);

        h.Submit(TestOrders.Limit("O-1", Perp().Id, OrderSide.Buy, "50000.000", "50000.00", quoteQuantity: true));

        Assert.Contains(new Money(2_500m, Currencies.USDT).ToString(), Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cash_account_is_judged_the_way_it_always_was()
    {
        // The margin check is for margin accounts: a cash account still answers for what the order costs, which is
        // the whole notional and not a margin fraction of it.
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 3_000m, 0m))));

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.StartsWith("INSUFFICIENT_BALANCE", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
        Assert.Equal(0, h.Engine.MarginDeniedCount);
    }

    [Fact]
    public void An_engine_told_not_to_check_margin_does_not()
    {
        RiskHarness h = Harness(total: 1m, locked: 0m, config: new RiskEngineConfig { CheckMargin = false });

        h.Submit(Buy("O-1", "1.000"));

        Assert.Single(h.Forwarded);
        Assert.Empty(h.Denied);
    }

    [Fact]
    public void A_reset_engine_starts_its_count_of_refused_margin_again()
    {
        RiskHarness h = Harness(total: 1m, locked: 0m);
        h.Submit(Buy("O-1", "1.000"));
        Assert.Equal(1, h.Engine.MarginDeniedCount);

        h.Engine.Reset();

        Assert.Equal(0, h.Engine.MarginDeniedCount);
    }

    [Fact]
    public void A_leverage_the_margin_arithmetic_cannot_use_is_refused_where_it_is_set()
    {
        MarginAccount account = new(TestEvents.MarginState(TestIds.BinanceAccount, (Currencies.USDT, 1_000m, 0m)));

        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetDefaultLeverage(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetDefaultLeverage(0.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetLeverage(Perp().Id, 0m));
    }
}
