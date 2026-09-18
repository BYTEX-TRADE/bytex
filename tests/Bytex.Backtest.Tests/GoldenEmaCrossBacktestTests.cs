using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: an end-to-end run whose every trade can be derived on paper. EMA(1) is the close itself and EMA(3) has
// alpha = 2 / (3 + 1) = 0.5, seeded with the first close, ready after three bars. Trade size 2 BTC, spot, 0.2 % taker.
//
//  bar  close   EMA(3)          signal
//   1   100.0   100             -
//   2    98.0    99             -
//   3    96.0    97.5           ready; close < EMA, flat: nothing
//   4    99.0    98.25          close > EMA, flat       -> BUY  2 @  99.00   fee 0.396
//   5   103.0   100.625         long: hold
//   6   106.0   103.3125        long: hold
//   7   102.0   102.65625       close < EMA, long       -> SELL 2 @ 102.00   fee 0.408   trade P&L +6 - 0.804 = +5.196
//   8   101.0   101.828125      close < EMA, flat: nothing
//   9   104.0   102.9140625     close > EMA, flat       -> BUY  2 @ 104.00   fee 0.416
//  10   100.0   101.45703125    close < EMA, long       -> SELL 2 @ 100.00   fee 0.400   trade P&L -8 - 0.816 = -8.816
//  11   100.5   100.978515625   close < EMA, flat: nothing
//  12   102.0   101.4892578125  close > EMA, flat       -> BUY  2 @ 102.00   fee 0.408
//  end of run: the strategy flattens on stop         -> SELL 2 @ 102.00   fee 0.408   trade P&L  0 - 0.816 = -0.816
//
// Final USDT = 10 000 + 6 - 8 + 0 - (0.804 + 0.816 + 0.816) = 9 995.564, BTC = 0.
public sealed class GoldenEmaCrossBacktestTests
{
    public static readonly decimal[] Closes = [100.0m, 98.0m, 96.0m, 99.0m, 103.0m, 106.0m, 102.0m, 101.0m, 104.0m, 100.0m, 100.5m, 102.0m];

    /// <summary>Minute bars that open at the previous close and overshoot the body by 0.50 on both sides.</summary>
    public static List<Bar> Bars(Instrument instrument)
    {
        List<Bar> bars = new();
        decimal previous = Closes[0];
        for (int i = 0; i < Closes.Length; i++)
        {
            decimal close = Closes[i];
            bars.Add(Scripted.Bar(instrument, (i + 1) * 60_000L, previous, Math.Max(previous, close) + 0.5m, Math.Min(previous, close) - 0.5m, close));
            previous = close;
        }

        return bars;
    }

    private static BacktestEngine RunGolden(decimal tradeSize = 2m)
    {
        CurrencyPair spot = TestInstruments.Spot();
        BacktestEngine engine = new(new BacktestEngineConfig { RunId = "golden" });
        engine.AddInstrument(spot);
        engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim, StartingBalances = [new Money(10_000m, Currencies.USDT)] });
        engine.AddData(Bars(spot).Cast<IData>());
        engine.AddStrategy(new EmaCrossStrategy(new EmaCrossStrategyConfig
        {
            StrategyId = new Core.Model.Identifiers.StrategyId("Ema-001"),
            InstrumentId = spot.Id,
            BarType = Scripted.MinuteBars(spot),
            TradeSize = tradeSize,
        }));
        engine.Run();
        return engine;
    }

    [Fact]
    public void Strategy_trades_exactly_on_the_hand_derived_bars_at_their_closes()
    {
        using BacktestEngine engine = RunGolden();

        IReadOnlyList<FillReportRow> fills = engine.GetResult().Fills;

        Assert.Equal(
            [
                (OrderSide.Buy, 99.00m, Scripted.Ms(4 * 60_000)),
                (OrderSide.Sell, 102.00m, Scripted.Ms(7 * 60_000)),
                (OrderSide.Buy, 104.00m, Scripted.Ms(9 * 60_000)),
                (OrderSide.Sell, 100.00m, Scripted.Ms(10 * 60_000)),
                (OrderSide.Buy, 102.00m, Scripted.Ms(12 * 60_000)),
                (OrderSide.Sell, 102.00m, Scripted.Ms(12 * 60_000)),
            ],
            fills.Select(f => (f.Side, f.LastPx.Value, f.TsEvent)));
        Assert.All(fills, f => Assert.Equal(new Quantity(2m, 3), f.LastQty));
        Assert.All(fills, f => Assert.Equal(LiquiditySide.Taker, f.LiquiditySide));
    }

    [Fact]
    public void Every_fill_is_charged_the_taker_fee_on_its_own_notional()
    {
        using BacktestEngine engine = RunGolden();

        Assert.Equal(
            [0.396m, 0.408m, 0.416m, 0.400m, 0.408m, 0.408m],
            engine.GetResult().Fills.Select(f => f.Commission.Amount));
    }

    [Fact]
    public void Three_round_trips_produce_the_hand_derived_profit_and_loss_per_position()
    {
        using BacktestEngine engine = RunGolden();

        BacktestResult result = engine.GetResult();

        Assert.Equal([5.196m, -8.816m, -0.816m], result.Positions.OrderBy(p => p.TsOpened.Value).Select(p => p.RealizedPnl.Amount));
        Assert.Equal(3, result.Trades.ClosedPositions);
        Assert.Equal(0, result.Trades.OpenPositions);
        Assert.Equal(1, result.Trades.Winners);
        Assert.Equal(2, result.Trades.Losers);
        Assert.Equal(TimeSpan.FromSeconds(80), result.Trades.AverageDuration); // (180 + 60 + 0) / 3
    }

    [Fact]
    public void Final_balances_match_the_paper_calculation_to_the_last_decimal()
    {
        using BacktestEngine engine = RunGolden();

        SimulatedExchange exchange = engine.Exchanges[TestInstruments.Sim];
        Assert.Equal(9_995.564m, exchange.Balances[Currencies.USDT]);
        Assert.Equal(0m, exchange.Balances[Currencies.BTC]);

        CurrencyStatistics usdt = engine.GetResult().Currencies.Single(c => c.Currency.Equals(Currencies.USDT));
        Assert.Equal(10_000m, usdt.StartingBalance);
        Assert.Equal(9_995.564m, usdt.EndingBalance);
        Assert.Equal(-4.436m, usdt.RealizedPnl);
        Assert.Equal(2.436m, usdt.TotalCommissions);
        Assert.Equal(-0.04436m, usdt.ReturnPercent);
        Assert.Equal(5.196m / (8.816m + 0.816m), usdt.ProfitFactor);
    }

    [Fact]
    public void Doubling_the_trade_size_doubles_every_cash_flow()
    {
        using BacktestEngine engine = RunGolden(tradeSize: 4m);

        Assert.Equal(10_000m - (2m * 4.436m), engine.Exchanges[TestInstruments.Sim].Balances[Currencies.USDT]);
    }
}
