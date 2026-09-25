using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Position P&L is the number a trader ultimately reads. Every scenario here (open, add, reduce, close, flip,
// long and short, linear, multiplied and inverse contracts, commissions) is checked against figures worked
// out by hand in the comments, never against the implementation's own output.
public class PositionTests
{
    [Fact]
    public void The_opening_buy_creates_a_long_position()
    {
        CryptoPerpetual perp = EthPerp();

        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00"), t: 5, clientOrderId: "O-OPEN"));

        Assert.Equal(new PositionId("P-1"), position.Id);
        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Equal(PositionSide.Long, position.EntrySide);
        Assert.Equal(Quantity.Parse("10.000"), position.Quantity);
        Assert.Equal(10m, position.SignedQuantity);
        Assert.Equal(Quantity.Parse("10.000"), position.PeakQuantity);
        Assert.Equal(Quantity.Parse("10.000"), position.BuyQuantity);
        Assert.Equal(Quantity.Parse("0.000"), position.SellQuantity);
        Assert.Equal(2000.00m, position.AvgPxOpen);
        Assert.Null(position.AvgPxClose);
        Assert.Equal(Usdt("-4.00"), position.RealizedPnl); // only the entry commission so far
        Assert.True(position.IsOpen);
        Assert.True(position.IsLong);
        Assert.False(position.IsShort);
        Assert.False(position.IsClosed);
        Assert.Equal(new ClientOrderId("O-OPEN"), position.OpeningOrderId);
        Assert.Null(position.ClosingOrderId);
        Assert.Equal(At(5), position.TsOpened);
        Assert.Null(position.TsClosed);
        Assert.Null(position.Duration);
        Assert.Equal(Account, position.AccountId);
        Assert.Equal(Strategy, position.StrategyId);
        Assert.Equal(perp.Id, position.InstrumentId);
        Assert.Equal(Currencies.USDT, position.SettlementCurrency);
    }

    [Fact]
    public void The_opening_sell_creates_a_short_position()
    {
        CryptoPerpetual perp = EthPerp();

        Position position = new(perp, Fill(perp, OrderSide.Sell, "2.500", "2000.00", "T-1"));

        Assert.Equal(PositionSide.Short, position.Side);
        Assert.Equal(PositionSide.Short, position.EntrySide);
        Assert.Equal(-2.5m, position.SignedQuantity);
        Assert.Equal(Quantity.Parse("2.500"), position.Quantity);
        Assert.Equal(Quantity.Parse("2.500"), position.SellQuantity);
        Assert.True(position.IsShort);
    }

    [Fact]
    public void Adding_to_a_long_moves_the_average_entry_price_by_size()
    {
        CurrencyPair spot = BtcUsdt();
        Position position = new(spot, Fill(spot, OrderSide.Buy, "1.000000", "50000.00", "T-1"));

        position.Apply(Fill(spot, OrderSide.Buy, "3.000000", "52000.00", "T-2"));

        // (1 * 50 000 + 3 * 52 000) / 4 = 206 000 / 4 = 51 500
        Assert.Equal(51_500m, position.AvgPxOpen);
        Assert.Equal(Quantity.Parse("4.000000"), position.Quantity);
        Assert.Equal(Quantity.Parse("4.000000"), position.PeakQuantity);
        Assert.Equal(Money.Zero(Currencies.USDT), position.RealizedPnl);
    }

    [Fact]
    public void Adding_to_a_short_moves_the_average_entry_price_by_size()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Sell, "2.000", "2000.00", "T-1"));

        position.Apply(Fill(perp, OrderSide.Sell, "6.000", "2100.00", "T-2"));

        // (2 * 2000 + 6 * 2100) / 8 = 16 600 / 8 = 2075
        Assert.Equal(2075m, position.AvgPxOpen);
        Assert.Equal(-8m, position.SignedQuantity);
    }

    [Fact]
    public void Reducing_a_long_realizes_profit_on_the_closed_part_only()
    {
        CurrencyPair spot = BtcUsdt();
        Position position = new(spot, Fill(spot, OrderSide.Buy, "1.000000", "50000.00", "T-1", Usdt("2.00")));
        position.Apply(Fill(spot, OrderSide.Buy, "3.000000", "52000.00", "T-2", Usdt("2.00")));

        position.Apply(Fill(spot, OrderSide.Sell, "1.000000", "53000.00", "T-3", Usdt("2.00")));

        // Gross (53 000 - 51 500) * 1 = 1500; commissions 3 * 2 = 6; net 1494.
        Assert.Equal(Usdt("1494.00"), position.RealizedPnl);
        Assert.Equal(51_500m, position.AvgPxOpen); // unchanged by a reduction
        Assert.Equal(53_000m, position.AvgPxClose);
        Assert.Equal(Quantity.Parse("3.000000"), position.Quantity);
        Assert.Equal(Quantity.Parse("4.000000"), position.PeakQuantity);
        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Null(position.ClosingOrderId);
    }

    [Fact]
    public void Closing_a_long_at_a_profit_realizes_price_difference_minus_commissions()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00"), t: 0, clientOrderId: "O-OPEN"));

        position.Apply(Fill(perp, OrderSide.Sell, "10.000", "2100.00", "T-2", Usdt("4.20"), t: 90, clientOrderId: "O-CLOSE"));

        // Gross (2100 - 2000) * 10 = 1000; commissions 4.00 + 4.20 = 8.20; net 991.80.
        Assert.Equal(Usdt("991.80"), position.RealizedPnl);
        Assert.Equal(PositionSide.Flat, position.Side);
        Assert.True(position.IsClosed);
        Assert.False(position.IsOpen);
        Assert.Equal(0m, position.SignedQuantity);
        Assert.True(position.Quantity.IsZero);
        Assert.Equal(Quantity.Parse("10.000"), position.PeakQuantity);
        Assert.Equal(2100m, position.AvgPxClose);
        Assert.Equal(new ClientOrderId("O-CLOSE"), position.ClosingOrderId);
        Assert.Equal(At(90), position.TsClosed);
        Assert.Equal(TimeSpan.FromSeconds(90), position.Duration);
        Assert.Equal(PositionSide.Long, position.EntrySide);
        // Return on the 20 000 entry notional: 991.80 / 20 000 = 0.04959.
        Assert.Equal(0.04959m, position.RealizedReturn);
    }

    [Fact]
    public void Closing_a_long_at_a_loss_realizes_a_negative_amount()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00")));

        position.Apply(Fill(perp, OrderSide.Sell, "10.000", "1950.50", "T-2", Usdt("3.90")));

        // Gross (1950.50 - 2000) * 10 = -495; commissions 7.90; net -502.90.
        Assert.Equal(Usdt("-502.90"), position.RealizedPnl);
    }

    [Fact]
    public void A_short_profits_when_it_is_bought_back_lower_and_loses_when_higher()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Sell, "10.000", "2000.00", "T-1"));

        position.Apply(Fill(perp, OrderSide.Buy, "4.000", "1900.00", "T-2"));
        Assert.Equal(Usdt("400.00"), position.RealizedPnl); // (2000 - 1900) * 4
        Assert.Equal(PositionSide.Short, position.Side);
        Assert.Equal(-6m, position.SignedQuantity);

        position.Apply(Fill(perp, OrderSide.Buy, "6.000", "2050.00", "T-3"));
        Assert.Equal(Usdt("100.00"), position.RealizedPnl); // 400 + (2000 - 2050) * 6 = 400 - 300
        Assert.Equal(PositionSide.Flat, position.Side);
        // (4 * 1900 + 6 * 2050) / 10 = (7600 + 12 300) / 10 = 1990
        Assert.Equal(1990m, position.AvgPxClose);
        // 100 / (2000 * 10) = 0.005
        Assert.Equal(0.005m, position.RealizedReturn);
    }

    [Fact]
    public void A_sell_larger_than_the_long_flips_the_position_short_at_the_fill_price()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1"));

        position.Apply(Fill(perp, OrderSide.Sell, "15.000", "2100.00", "T-2"));

        // Only the 10 that closed the long are realized: (2100 - 2000) * 10 = 1000.
        Assert.Equal(Usdt("1000.00"), position.RealizedPnl);
        Assert.Equal(PositionSide.Short, position.Side);
        Assert.Equal(-5m, position.SignedQuantity);
        Assert.Equal(Quantity.Parse("5.000"), position.Quantity);
        Assert.Equal(2100m, position.AvgPxOpen); // the new short was opened by this fill
        Assert.Equal(Quantity.Parse("10.000"), position.PeakQuantity);
        Assert.Null(position.TsClosed);

        position.Apply(Fill(perp, OrderSide.Buy, "5.000", "2000.00", "T-3"));

        // The short leg adds (2100 - 2000) * 5 = 500.
        Assert.Equal(Usdt("1500.00"), position.RealizedPnl);
        Assert.Equal(PositionSide.Flat, position.Side);
    }

    [Fact]
    public void A_buy_larger_than_the_short_flips_the_position_long()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Sell, "4.000", "2000.00", "T-1"));

        position.Apply(Fill(perp, OrderSide.Buy, "6.000", "1990.00", "T-2"));

        Assert.Equal(Usdt("40.00"), position.RealizedPnl); // (2000 - 1990) * 4
        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Equal(2m, position.SignedQuantity);
        Assert.Equal(1990m, position.AvgPxOpen);
    }

    [Fact]
    public void A_flat_position_reopens_with_a_fresh_entry_price_and_keeps_its_realized_total()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1"));
        position.Apply(Fill(perp, OrderSide.Sell, "10.000", "2100.00", "T-2", t: 10));

        position.Apply(Fill(perp, OrderSide.Buy, "5.000", "2200.00", "T-3", t: 20));

        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Equal(2200m, position.AvgPxOpen);
        Assert.Equal(Quantity.Parse("5.000"), position.Quantity);
        Assert.Equal(Usdt("1000.00"), position.RealizedPnl);
        Assert.Null(position.TsClosed);
        Assert.Null(position.ClosingOrderId);
        Assert.Null(position.Duration);
    }

    [Theory]
    [InlineData(OrderSide.Buy, "2050.50", "505.00")] // (2050.50 - 2000) * 10
    [InlineData(OrderSide.Buy, "1990.00", "-100.00")]
    [InlineData(OrderSide.Sell, "2050.50", "-505.00")]
    [InlineData(OrderSide.Sell, "1990.00", "100.00")]
    [InlineData(OrderSide.Buy, "2000.00", "0.00")]
    public void Unrealized_pnl_marks_the_open_quantity_to_the_given_price(OrderSide entry, string lastPrice, string expected)
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, entry, "10.000", "2000.00", "T-1", Usdt("4.00")));

        Assert.Equal(Usdt(expected), position.UnrealizedPnl(Price.Parse(lastPrice)));
    }

    [Fact]
    public void Total_pnl_is_realized_plus_unrealized()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00")));
        position.Apply(Fill(perp, OrderSide.Sell, "4.000", "2100.00", "T-2", Usdt("1.68")));

        // Realized: -4.00 + (100 * 4 - 1.68) = 394.32. Unrealized on the remaining 6 at 2050: 50 * 6 = 300.
        Assert.Equal(Usdt("394.32"), position.RealizedPnl);
        Assert.Equal(Usdt("300.00"), position.UnrealizedPnl(Price.Parse("2050.00")));
        Assert.Equal(Usdt("694.32"), position.TotalPnl(Price.Parse("2050.00")));
    }

    [Fact]
    public void A_flat_position_has_no_unrealized_pnl()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1"));
        position.Apply(Fill(perp, OrderSide.Sell, "1.000", "2000.00", "T-2"));

        Assert.Equal(Money.Zero(Currencies.USDT), position.UnrealizedPnl(Price.Parse("9999.00")));
    }

    [Fact]
    public void The_contract_multiplier_scales_pnl()
    {
        FuturesContract es = EsFuture();
        Position position = new(es, Fill(es, OrderSide.Buy, "2", "4500.00", "T-1", new Money(4.50m, Currencies.USD)));

        // Unrealized at 4505.00: 5 points * 2 contracts * 50 USD = 500.
        Assert.Equal(new Money(500m, Currencies.USD), position.UnrealizedPnl(Price.Parse("4505.00")));

        position.Apply(Fill(es, OrderSide.Sell, "2", "4510.25", "T-2", new Money(4.50m, Currencies.USD)));

        // Gross 10.25 * 2 * 50 = 1025; commissions 9; net 1016.
        Assert.Equal(new Money(1016m, Currencies.USD), position.RealizedPnl);
        // 1016 / (4500 * 2 * 50 = 450 000) = 0.0022577...; compare on the exact fraction.
        Assert.Equal(1016m / 450_000m, position.RealizedReturn);
    }

    [Theory]
    // Inverse contracts pay (1/entry - 1/exit) * contracts in BTC for a long, the negative for a short.
    [InlineData(OrderSide.Buy, "10000.0", "12500.0", "2")] // (0.0001 - 0.00008) * 100 000
    [InlineData(OrderSide.Buy, "10000.0", "8000.0", "-2.5")] // (0.0001 - 0.000125) * 100 000
    [InlineData(OrderSide.Sell, "10000.0", "8000.0", "2.5")]
    [InlineData(OrderSide.Sell, "10000.0", "12500.0", "-2")]
    public void Inverse_contracts_realize_pnl_in_base_currency(OrderSide entry, string entryPrice, string exitPrice, string expectedBtc)
    {
        CryptoPerpetual inverse = XbtUsdInverse();
        Position position = new(inverse, Fill(inverse, entry, "100000", entryPrice, "T-1"));

        Assert.Equal(new Money(D(expectedBtc), Currencies.BTC), position.UnrealizedPnl(Price.Parse(exitPrice)));

        position.Apply(Fill(inverse, entry.Opposite(), "100000", exitPrice, "T-2"));

        Assert.Equal(new Money(D(expectedBtc), Currencies.BTC), position.RealizedPnl);
        Assert.True(position.IsInverse);
        Assert.Equal(Currencies.BTC, position.SettlementCurrency);
    }

    [Fact]
    public void Inverse_commissions_in_btc_reduce_realized_pnl()
    {
        CryptoPerpetual inverse = XbtUsdInverse();
        Position position = new(inverse, Fill(inverse, OrderSide.Buy, "100000", "10000.0", "T-1", new Money(0.0075m, Currencies.BTC)));

        position.Apply(Fill(inverse, OrderSide.Sell, "100000", "12500.0", "T-2", new Money(0.006m, Currencies.BTC)));

        // 2 BTC gross - 0.0075 - 0.006 = 1.9865 BTC.
        Assert.Equal(new Money(1.9865m, Currencies.BTC), position.RealizedPnl);
    }

    [Fact]
    public void A_commission_in_another_currency_is_tracked_but_not_netted_into_pnl()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", new Money(0.01m, Currencies.BNB)));

        position.Apply(Fill(perp, OrderSide.Sell, "10.000", "2100.00", "T-2", Usdt("4.20")));
        position.Apply(Fill(perp, OrderSide.Sell, "1.000", "2100.00", "T-3", new Money(0.002m, Currencies.BNB)));

        // The USDT P&L only sees the USDT commission: 1000 - 4.20.
        Assert.Equal(Usdt("995.80"), position.RealizedPnl);
        Assert.Equal(new Money(0.012m, Currencies.BNB), position.Commissions[Currencies.BNB]);
        Assert.Equal(Usdt("4.20"), position.Commissions[Currencies.USDT]);
    }

    [Fact]
    public void Many_small_fills_accumulate_without_drift()
    {
        CurrencyPair spot = BtcUsdt();
        Position position = new(spot, Fill(spot, OrderSide.Buy, "0.100000", "50000.10", "T-0"));
        for (int i = 1; i < 10; i++)
        {
            position.Apply(Fill(spot, OrderSide.Buy, "0.100000", "50000.10", "T-" + i));
        }

        Assert.Equal(Quantity.Parse("1.000000"), position.Quantity);
        Assert.Equal(1m, position.SignedQuantity);
        Assert.Equal(50000.10m, position.AvgPxOpen);

        for (int i = 10; i < 20; i++)
        {
            position.Apply(Fill(spot, OrderSide.Sell, "0.100000", "50000.20", "T-" + i));
        }

        // Ten closes of 0.1 BTC, each earning 0.10 * 0.1 = 0.01 USDT: exactly 0.10.
        Assert.Equal(PositionSide.Flat, position.Side);
        Assert.Equal(0m, position.SignedQuantity);
        Assert.Equal(Usdt("0.10"), position.RealizedPnl);
    }

    [Fact]
    public void A_repeated_trade_id_is_ignored()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00")));

        position.Apply(Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00")));

        Assert.Equal(Quantity.Parse("10.000"), position.Quantity);
        Assert.Equal(Usdt("-4.00"), position.RealizedPnl);
        Assert.Equal(1, position.EventCount);
    }

    [Fact]
    public void Fills_trades_and_orders_are_recorded_once_each_in_order()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1", clientOrderId: "O-1"));
        position.Apply(Fill(perp, OrderSide.Buy, "1.000", "2001.00", "T-2", clientOrderId: "O-1"));
        OrderFilled last = Fill(perp, OrderSide.Sell, "0.500", "2002.00", "T-3", clientOrderId: "O-2", t: 3);
        position.Apply(last);

        Assert.Equal(3, position.EventCount);
        Assert.Equal([new TradeId("T-1"), new TradeId("T-2"), new TradeId("T-3")], position.TradeIds);
        Assert.Equal([new ClientOrderId("O-1"), new ClientOrderId("O-2")], position.ClientOrderIds);
        Assert.Same(last, position.LastEvent);
        Assert.Equal(Quantity.Parse("0.500"), position.LastQty);
        Assert.Equal(Price.Parse("2002.00"), position.LastPx);
        Assert.Equal(At(3), position.TsLast);
        Assert.Equal(Quantity.Parse("2.000"), position.BuyQuantity);
        Assert.Equal(Quantity.Parse("0.500"), position.SellQuantity);
    }

    [Fact]
    public void Notional_value_uses_the_open_quantity()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Sell, "2.500", "2000.00", "T-1"));

        Assert.Equal(Usdt("5250.00"), position.NotionalValue(Price.Parse("2100.00"))); // 2.5 * 2100
    }

    [Fact]
    public void IsOppositeSide_tells_whether_an_order_would_reduce_the_position()
    {
        CryptoPerpetual perp = EthPerp();
        Position longPosition = new(perp, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1"));
        Position shortPosition = new(perp, Fill(perp, OrderSide.Sell, "1.000", "2000.00", "T-1"));

        Assert.True(longPosition.IsOppositeSide(OrderSide.Sell));
        Assert.False(longPosition.IsOppositeSide(OrderSide.Buy));
        Assert.True(shortPosition.IsOppositeSide(OrderSide.Buy));
        Assert.False(shortPosition.IsOppositeSide(OrderSide.Sell));
    }

    [Fact]
    public void A_fill_for_another_instrument_is_rejected()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1"));

        Assert.Throws<ArgumentException>(() => position.Apply(Fill(BtcUsdt(), OrderSide.Sell, "1.000000", "50000.00", "T-2")));
        Assert.Equal(1, position.EventCount);
    }

    [Fact]
    public void The_opening_fill_must_identify_the_position_and_the_account()
    {
        CryptoPerpetual perp = EthPerp();

        Assert.Throws<ArgumentException>(() => new Position(perp, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1", positionId: null)));
        Assert.Throws<ArgumentException>(() => new Position(perp, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1", withAccount: false)));
        Assert.Throws<ArgumentNullException>(() => new Position(null!, Fill(perp, OrderSide.Buy, "1.000", "2000.00", "T-1")));
        Assert.Throws<ArgumentNullException>(() => new Position(perp, null!));
    }

    [Fact]
    public void Text_form_shows_side_quantity_entry_and_realized_pnl()
    {
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00")));

        string text = position.ToString();

        Assert.StartsWith("Position(LONG 10.000 ETHUSDT-PERP.BINANCE, id=P-1, avg_px_open=2000", text, StringComparison.Ordinal);
        Assert.EndsWith("realized_pnl=-4.00000000 USDT)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_position_event_reports_a_flat_side_and_zero_quantities()
    {
        PositionClosed closed = new(
            Trader, Strategy, InstrumentId.Parse("ETHUSDT-PERP.BINANCE"), new PositionId("P-1"), Account,
            new ClientOrderId("O-1"), new ClientOrderId("O-2"), PositionSide.Long, Quantity.Parse("10.000"),
            Quantity.Parse("10.000"), Price.Parse("2100.00"), Currencies.USDT, 2000m, 2100m, Usdt("991.80"),
            At(0), At(90), TimeSpan.FromSeconds(90), Id(1), At(90), At(90));

        Assert.Equal(PositionSide.Flat, closed.Side);
        Assert.True(closed.Quantity.IsZero);
        Assert.True(closed.SignedQuantity.IsZero);
        Assert.Equal(Money.Zero(Currencies.USDT), closed.UnrealizedPnl);
        Assert.Equal(PositionSide.Long, closed.EntrySide);
        Assert.Equal(new ClientOrderId("O-2"), closed.ClosingOrderId);
    }
}
