using Bytex.Core.Caching;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Portfolios;

// Why: R6.4 - the portfolio is what a strategy (and the risk engine) asks for exposure and P&L. Longs are
// marked at the bid, shorts at the ask, exposure at the mid. Every figure below is computed by hand.
public class PortfolioTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            Bus = new MessageBus(TestIds.Trader);
            Cache = new Cache();
            Portfolio = new Portfolio(Cache, Bus);
            Cache.AddInstrument(TestInstruments.BtcUsdt());
            Cache.AddInstrument(TestInstruments.EthUsdt());
            Cache.AddInstrument(TestInstruments.EthBtc());
            Cache.AddInstrument(TestInstruments.BtcPerp());
        }

        public MessageBus Bus { get; }

        public Cache Cache { get; }

        public Portfolio Portfolio { get; }

        private int _sequence;

        public Position Open(InstrumentId instrumentId, OrderSide side, string qty, string px, string commission = "0", Currency? currency = null)
        {
            int n = ++_sequence;
            Instrument instrument = Cache.Instrument(instrumentId)!;
            MarketOrder order = TestOrders.Market($"O-{n}", instrumentId, side, qty);
            Position position = new(instrument, TestEvents.Filled(order, $"T-{n}", qty, px, commission, currency, new PositionId($"P-{n}")));
            Cache.AddPosition(position);
            return position;
        }

        public void Reduce(Position position, OrderSide side, string qty, string px, string commission = "0")
        {
            int n = ++_sequence;
            MarketOrder order = TestOrders.Market($"O-{n}", position.InstrumentId, side, qty);
            position.Apply(TestEvents.Filled(order, $"T-{n}", qty, px, commission, positionId: position.Id));
            Cache.UpdatePosition(position);
        }

        public void Quote(InstrumentId id, string bid, string ask) =>
            Cache.AddQuoteTick(new QuoteTick(id, Price.Parse(bid), Price.Parse(ask), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0));

        public T OpenOrder<T>(T order) where T : Order
        {
            Cache.AddOrder(order);
            order.Apply(TestEvents.Submitted(order));
            order.Apply(TestEvents.Accepted(order, "V-" + order.ClientOrderId.Value));
            Cache.UpdateOrder(order);
            return order;
        }
    }

    [Fact]
    public void First_account_state_creates_a_cash_or_margin_account_by_type()
    {
        Fixture f = new();

        f.Portfolio.UpdateAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 10_000m, 1_000m)));
        f.Portfolio.UpdateAccount(TestEvents.MarginState(TestIds.BybitAccount, (Currencies.USDT, 5_000m, 0m)));

        Account cash = Assert.IsType<CashAccount>(f.Portfolio.Account(TestIds.Binance));
        Assert.IsType<MarginAccount>(f.Portfolio.Account(TestIds.Bybit));
        Assert.Equal(Money.Parse("10000 USDT"), cash.BalanceTotal(Currencies.USDT));
        Assert.Equal(Money.Parse("1000 USDT"), cash.BalanceLocked(Currencies.USDT));
        Assert.Equal(Money.Parse("9000 USDT"), cash.BalanceFree(Currencies.USDT));
        Assert.Null(f.Portfolio.Account(new Venue("KRAKEN")));
    }

    [Fact]
    public void Later_account_states_update_the_same_account_and_each_state_is_published()
    {
        Fixture f = new();
        BusRecorder published = new();
        f.Bus.Subscribe(Topics.AccountEvents(TestIds.BinanceAccount), published.Handle);
        AccountState first = TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 10_000m, 0m));
        AccountState second = TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 9_500m, 500m), (Currencies.BTC, 0.01m, 0m));

        f.Portfolio.UpdateAccount(first);
        Account account = f.Portfolio.Account(TestIds.Binance)!;
        f.Bus.Send(Endpoints.PortfolioUpdateAccount, second);

        Assert.Same(account, f.Portfolio.Account(TestIds.Binance));
        Assert.Equal(Money.Parse("9000 USDT"), account.BalanceFree(Currencies.USDT));
        Assert.Equal(Money.Parse("0.01 BTC"), account.BalanceTotal(Currencies.BTC));
        Assert.Equal(2, account.EventCount);
        Assert.Equal(new object[] { first, second }, published.Messages);
    }

    [Fact]
    public void Locked_balances_sum_the_open_orders_of_a_cash_account_per_currency()
    {
        Fixture f = new();
        f.Portfolio.UpdateAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100_000m, 0m)));
        f.OpenOrder(TestOrders.Limit("O-A", TestIds.BtcUsdt, OrderSide.Buy, "0.500", "40000.00")); // 0.5 * 40,000 = 20,000 USDT
        LimitOrder partly = f.OpenOrder(TestOrders.Limit("O-B", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "30000.00"));
        partly.Apply(TestEvents.Filled(partly, "T-B", "0.250", "30000.00")); // 0.75 left * 30,000 = 22,500 USDT
        f.Cache.UpdateOrder(partly);
        f.OpenOrder(TestOrders.Limit("O-C", TestIds.EthUsdt, OrderSide.Sell, "2.000", "3000.00")); // sells lock the base: 2 ETH
        LimitOrder canceled = f.OpenOrder(TestOrders.Limit("O-D", TestIds.BtcUsdt, OrderSide.Buy, "9.000", "10000.00"));
        canceled.Apply(TestEvents.Canceled(canceled));
        f.Cache.UpdateOrder(canceled);

        IReadOnlyDictionary<Currency, Money> locked = f.Portfolio.BalancesLocked(TestIds.Binance);

        Assert.Equal(2, locked.Count);
        Assert.Equal(Money.Parse("42500 USDT"), locked[Currencies.USDT]);
        Assert.Equal(Money.Parse("2 ETH"), locked[Currencies.ETH]);
    }

    [Fact]
    public void Locked_balance_of_an_open_stop_order_uses_its_trigger_price()
    {
        Fixture f = new();
        f.Portfolio.UpdateAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100_000m, 0m)));
        f.OpenOrder(TestOrders.StopMarket("O-A", TestIds.BtcUsdt, OrderSide.Buy, "0.100", "55000.00"));

        // 0.1 * 55,000 = 5,500
        Assert.Equal(Money.Parse("5500 USDT"), f.Portfolio.BalancesLocked(TestIds.Binance)[Currencies.USDT]);
    }

    [Fact]
    public void Locked_balances_are_empty_for_margin_accounts_and_unknown_venues()
    {
        Fixture f = new();
        f.Portfolio.UpdateAccount(TestEvents.MarginState(TestIds.BybitAccount, (Currencies.USDT, 5_000m, 0m)));
        f.OpenOrder(TestOrders.Limit("O-A", TestIds.BtcPerp, OrderSide.Buy, "1.000", "50000.0"));

        Assert.Empty(f.Portfolio.BalancesLocked(TestIds.Bybit));
        Assert.Empty(f.Portfolio.BalancesLocked(TestIds.Binance));
    }

    [Fact]
    public void Margins_are_summed_across_instruments_of_a_margin_account()
    {
        Fixture f = new();
        AccountState state = new(
            TestIds.BybitAccount, AccountType.Margin, null, true,
            [AccountBalance.Unlocked(Money.Parse("5000 USDT"))],
            [
                new MarginBalance(Money.Parse("100 USDT"), Money.Parse("40 USDT"), TestIds.BtcPerp),
                new MarginBalance(Money.Parse("50 USDT"), Money.Parse("20 USDT"), TestIds.BtcUsdtBybit),
            ],
            new Dictionary<string, string>(), Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

        f.Portfolio.UpdateAccount(state);

        Assert.Equal(Money.Parse("150 USDT"), f.Portfolio.MarginsInit(TestIds.Bybit)[Currencies.USDT]);
        Assert.Equal(Money.Parse("60 USDT"), f.Portfolio.MarginsMaint(TestIds.Bybit)[Currencies.USDT]);
        Assert.Empty(f.Portfolio.MarginsInit(TestIds.Binance));
    }

    [Fact]
    public void Long_is_marked_at_the_bid_and_short_at_the_ask()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "2.000", "50000.00");
        f.Open(TestIds.EthUsdt, OrderSide.Sell, "1.000", "3000.00");
        f.Quote(TestIds.BtcUsdt, "51000.00", "51010.00");
        f.Quote(TestIds.EthUsdt, "2890.00", "2900.00");

        // Long: (51,000 bid - 50,000) * 2 = 2,000. Short: (3,000 - 2,900 ask) * 1 = 100.
        Assert.Equal(Money.Parse("2000 USDT"), f.Portfolio.UnrealizedPnl(TestIds.BtcUsdt));
        Assert.Equal(Money.Parse("100 USDT"), f.Portfolio.UnrealizedPnl(TestIds.EthUsdt));
    }

    [Fact]
    public void Unrealized_pnl_follows_each_new_quote()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "2.000", "50000.00");
        f.Quote(TestIds.BtcUsdt, "51000.00", "51010.00");
        Money? before = f.Portfolio.UnrealizedPnl(TestIds.BtcUsdt);

        f.Quote(TestIds.BtcUsdt, "49500.00", "49510.00");

        Assert.Equal(Money.Parse("2000 USDT"), before);
        Assert.Equal(Money.Parse("-1000 USDT"), f.Portfolio.UnrealizedPnl(TestIds.BtcUsdt));
    }

    [Fact]
    public void Unrealized_pnl_falls_back_to_the_last_trade_without_quotes()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "2.000", "50000.00");
        f.Cache.AddTradeTick(new TradeTick(TestIds.BtcUsdt, Price.Parse("50500.00"), Quantity.Parse("1.000"), AggressorSide.Seller, new TradeId("X"), TestOrders.T0, TestOrders.T0));

        Assert.Equal(Money.Parse("1000 USDT"), f.Portfolio.UnrealizedPnl(TestIds.BtcUsdt));
    }

    [Fact]
    public void Unrealized_pnl_is_unknown_without_a_price_zero_without_positions_and_null_for_unknown_instruments()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "2.000", "50000.00");

        Assert.Null(f.Portfolio.UnrealizedPnl(TestIds.BtcUsdt));
        Assert.Equal(Money.Parse("0 USDT"), f.Portfolio.UnrealizedPnl(TestIds.EthUsdt));
        Assert.Null(f.Portfolio.UnrealizedPnl(TestIds.BtcUsdtBybit));
    }

    [Fact]
    public void Unrealized_pnls_of_a_venue_are_grouped_by_settlement_currency()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "2.000", "50000.00");
        f.Open(TestIds.EthUsdt, OrderSide.Sell, "1.000", "3000.00");
        f.Open(TestIds.EthBtc, OrderSide.Buy, "10.000", "0.05000", currency: Currencies.BTC);
        f.Open(TestIds.BtcPerp, OrderSide.Buy, "1.000", "50000.0");
        f.Quote(TestIds.BtcUsdt, "51000.00", "51010.00");
        f.Quote(TestIds.EthUsdt, "2890.00", "2900.00");
        f.Cache.AddQuoteTick(new QuoteTick(TestIds.EthBtc, Price.Parse("0.05200"), Price.Parse("0.05210"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0));
        f.Cache.AddQuoteTick(new QuoteTick(TestIds.BtcPerp, Price.Parse("49000.0"), Price.Parse("49001.0"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0));

        IReadOnlyDictionary<Currency, Money> binance = f.Portfolio.UnrealizedPnls(TestIds.Binance);
        IReadOnlyDictionary<Currency, Money> bybit = f.Portfolio.UnrealizedPnls(TestIds.Bybit);

        // USDT: 2,000 + 100. BTC: (0.052 - 0.05) * 10 = 0.02. BYBIT: (49,000 - 50,000) * 1 = -1,000.
        Assert.Equal(Money.Parse("2100 USDT"), binance[Currencies.USDT]);
        Assert.Equal(Money.Parse("0.02 BTC"), binance[Currencies.BTC]);
        Assert.Equal(Money.Parse("-1000 USDT"), Assert.Single(bybit).Value);
    }

    [Fact]
    public void Realized_pnl_covers_open_and_closed_positions_net_of_commissions()
    {
        Fixture f = new();
        Position closed = f.Open(TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00", commission: "5");
        f.Reduce(closed, OrderSide.Sell, "1.000", "50500.00", commission: "5"); // 500 - 5 - 5 = 490
        Position open = f.Open(TestIds.BtcUsdt, OrderSide.Sell, "2.000", "51000.00", commission: "10");
        f.Reduce(open, OrderSide.Buy, "1.000", "50000.00", commission: "5"); // (51,000 - 50,000) * 1 - 10 - 5 = 985

        Assert.Equal(Money.Parse("1475 USDT"), f.Portfolio.RealizedPnl(TestIds.BtcUsdt));
        Assert.Equal(Money.Parse("1475 USDT"), f.Portfolio.RealizedPnls(TestIds.Binance)[Currencies.USDT]);
        Assert.Equal(Money.Parse("0 USDT"), f.Portfolio.RealizedPnl(TestIds.EthUsdt));
        Assert.Null(f.Portfolio.RealizedPnl(TestIds.BtcUsdtBybit));
    }

    [Fact]
    public void Total_pnl_is_realized_plus_unrealized_and_unknown_when_either_is_unknown()
    {
        Fixture f = new();
        Position position = f.Open(TestIds.BtcUsdt, OrderSide.Buy, "2.000", "50000.00");
        f.Reduce(position, OrderSide.Sell, "1.000", "50400.00"); // realized 400, 1 BTC left
        Money? withoutPrice = f.Portfolio.TotalPnl(TestIds.BtcUsdt);

        f.Quote(TestIds.BtcUsdt, "50100.00", "50110.00"); // unrealized (50,100 - 50,000) * 1 = 100

        Assert.Null(withoutPrice);
        Assert.Equal(Money.Parse("500 USDT"), f.Portfolio.TotalPnl(TestIds.BtcUsdt));
    }

    [Fact]
    public void Net_position_adds_signed_quantities_of_all_open_positions_of_the_instrument()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        f.Open(TestIds.BtcUsdt, OrderSide.Sell, "0.400", "50000.00");
        f.Open(TestIds.EthUsdt, OrderSide.Sell, "3.000", "3000.00");

        Assert.Equal(0.6m, f.Portfolio.NetPosition(TestIds.BtcUsdt));
        Assert.True(f.Portfolio.IsNetLong(TestIds.BtcUsdt));
        Assert.Equal(-3m, f.Portfolio.NetPosition(TestIds.EthUsdt));
        Assert.True(f.Portfolio.IsNetShort(TestIds.EthUsdt));
        Assert.True(f.Portfolio.IsFlat(TestIds.EthBtc));
        Assert.False(f.Portfolio.IsCompletelyFlat());
    }

    [Fact]
    public void Closed_positions_do_not_count_towards_the_net_position()
    {
        Fixture f = new();
        Position position = f.Open(TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        f.Reduce(position, OrderSide.Sell, "1.000", "50000.00");

        Assert.Equal(0m, f.Portfolio.NetPosition(TestIds.BtcUsdt));
        Assert.True(f.Portfolio.IsFlat(TestIds.BtcUsdt));
        Assert.True(f.Portfolio.IsCompletelyFlat());
    }

    [Fact]
    public void Net_exposure_is_the_absolute_net_position_valued_at_the_mid()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        f.Open(TestIds.BtcUsdt, OrderSide.Sell, "0.400", "50000.00");
        f.Open(TestIds.EthUsdt, OrderSide.Sell, "3.000", "3000.00");
        f.Quote(TestIds.BtcUsdt, "49990.00", "50010.00"); // mid 50,000
        f.Quote(TestIds.EthUsdt, "2890.00", "2900.00"); // mid 2,895

        // BTC: 0.6 * 50,000 = 30,000. ETH: |-3| * 2,895 = 8,685.
        Assert.Equal(Money.Parse("30000 USDT"), f.Portfolio.NetExposure(TestIds.BtcUsdt));
        Assert.Equal(Money.Parse("8685 USDT"), f.Portfolio.NetExposure(TestIds.EthUsdt));
        Assert.Equal(Money.Parse("38685 USDT"), f.Portfolio.NetExposures(TestIds.Binance)[Currencies.USDT]);
    }

    [Fact]
    public void Net_exposure_is_zero_when_flat_unknown_without_a_price_and_null_for_unknown_instruments()
    {
        Fixture f = new();
        f.Open(TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        Assert.Equal(Money.Parse("0 USDT"), f.Portfolio.NetExposure(TestIds.EthUsdt));
        Assert.Equal(Money.Parse("0 BTC"), f.Portfolio.NetExposure(TestIds.EthBtc));
        Assert.Null(f.Portfolio.NetExposure(TestIds.BtcUsdt));
        Assert.Null(f.Portfolio.NetExposure(TestIds.BtcUsdtBybit));
        Assert.Empty(f.Portfolio.NetExposures(TestIds.Binance));
    }
}
