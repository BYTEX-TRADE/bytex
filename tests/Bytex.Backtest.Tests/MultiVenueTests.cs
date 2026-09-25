using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: one engine can host several simulated venues. Their data must interleave on a single clock, while orders,
// prices and balances stay strictly separate per venue.
public sealed class MultiVenueTests
{
    private static (BacktestEngine Engine, ScriptedStrategy Strategy, CurrencyPair Btc, CurrencyPair Eth) Build()
    {
        CurrencyPair btc = TestInstruments.Spot(TestInstruments.Sim);
        CurrencyPair eth = TestInstruments.EthSpot(TestInstruments.Alt);
        BacktestEngine engine = new(new BacktestEngineConfig { RunId = "multi-venue" });
        engine.AddInstrument(btc);
        engine.AddInstrument(eth);
        engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim, StartingBalances = [new Money(10_000m, Currencies.USDT)] });
        engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Alt, StartingBalances = [new Money(5_000m, Currencies.USDT)] });
        ScriptedStrategy strategy = new(new ScriptedStrategyConfig
        {
            StrategyId = SimHarness.StrategyId,
            QuoteSubscriptions = [btc.Id, eth.Id],
        });
        engine.AddStrategy(strategy);
        return (engine, strategy, btc, eth);
    }

    [Fact]
    public void Data_of_two_venues_is_interleaved_by_timestamp_on_one_clock()
    {
        (BacktestEngine engine, ScriptedStrategy strategy, CurrencyPair btc, CurrencyPair eth) = Build();
        using BacktestEngine owned = engine;
        engine.AddData([Scripted.Quote(btc, 1000, 100.00m, 100.10m), Scripted.Quote(btc, 3000, 101.00m, 101.10m)]);
        engine.AddData([Scripted.Quote(eth, 2000, 10.00m, 10.10m), Scripted.Quote(eth, 4000, 11.00m, 11.10m)]);

        engine.Run();

        Assert.Equal(
            ["1000|quote BTCUSDT 100.00/100.10", "2000|quote ETHUSDT 10.00/10.10", "3000|quote BTCUSDT 101.00/101.10", "4000|quote ETHUSDT 11.00/11.10"],
            strategy.Journal);
    }

    [Fact]
    public void Each_order_is_matched_on_its_own_venue_against_that_venues_prices_and_balances()
    {
        (BacktestEngine engine, ScriptedStrategy strategy, CurrencyPair btc, CurrencyPair eth) = Build();
        using BacktestEngine owned = engine;
        engine.AddData([Scripted.Quote(btc, 1000, 100.00m, 100.10m), Scripted.Quote(eth, 1000, 10.00m, 10.10m), Scripted.Quote(btc, 3000, 100.00m, 100.10m)]);
        MarketOrder? btcOrder = null;
        MarketOrder? ethOrder = null;
        strategy.At(Scripted.Ms(2000), s =>
        {
            btcOrder = s.Submit(s.Orders.Market(btc.Id, OrderSide.Buy, btc.MakeQuantity(1m)));
            ethOrder = s.Submit(s.Orders.Market(eth.Id, OrderSide.Buy, eth.MakeQuantity(10m)));
        });

        engine.Run();

        Assert.Equal(100.10m, btcOrder!.AvgPx);
        Assert.Equal(10.10m, ethOrder!.AvgPx);
        Assert.Equal("SIM-001", btcOrder.AccountId!.Value.Value);
        Assert.Equal("ALT-001", ethOrder.AccountId!.Value.Value);

        // SIM: 10 000 - 100.10 - 0.2002 fee. ALT: 5 000 - 101.00 - 0.202 fee.
        Assert.Equal(9_899.6998m, engine.Exchanges[TestInstruments.Sim].Balances[Currencies.USDT]);
        Assert.Equal(1m, engine.Exchanges[TestInstruments.Sim].Balances[Currencies.BTC]);
        Assert.Equal(4_898.798m, engine.Exchanges[TestInstruments.Alt].Balances[Currencies.USDT]);
        Assert.Equal(10m, engine.Exchanges[TestInstruments.Alt].Balances[Currencies.ETH]);
        Assert.False(engine.Exchanges[TestInstruments.Alt].Balances.ContainsKey(Currencies.BTC));
    }

    [Fact]
    public void Result_adds_up_starting_and_ending_balances_of_all_venues_per_currency()
    {
        (BacktestEngine engine, ScriptedStrategy strategy, CurrencyPair btc, CurrencyPair eth) = Build();
        using BacktestEngine owned = engine;
        engine.AddData([Scripted.Quote(btc, 1000, 100.00m, 100.00m), Scripted.Quote(eth, 1000, 10.00m, 10.00m), Scripted.Quote(btc, 3000, 100.00m, 100.00m)]);
        strategy.At(Scripted.Ms(2000), s => s.Submit(s.Orders.Market(eth.Id, OrderSide.Buy, eth.MakeQuantity(10m))));

        engine.Run();

        CurrencyStatistics usdt = engine.GetResult().Currencies.Single(c => c.Currency.Equals(Currencies.USDT));
        Assert.Equal(15_000m, usdt.StartingBalance);
        Assert.Equal(15_000m - 100m - 0.2m, usdt.EndingBalance); // 10 ETH at 10.00 plus 0.2 % taker fee
    }

    [Fact]
    public void Order_for_an_instrument_that_was_never_added_is_denied_before_it_reaches_any_venue()
    {
        (BacktestEngine engine, ScriptedStrategy strategy, CurrencyPair btc, CurrencyPair _) = Build();
        using BacktestEngine owned = engine;
        CurrencyPair unknown = TestInstruments.EthSpot(TestInstruments.Sim);
        engine.AddData([Scripted.Quote(btc, 1000, 100.00m, 100.10m), Scripted.Quote(btc, 3000, 100.00m, 100.10m)]);
        MarketOrder? order = null;
        strategy.At(Scripted.Ms(2000), s => order = s.Submit(s.Orders.Market(unknown.Id, OrderSide.Buy, unknown.MakeQuantity(1m))));

        engine.Run();

        Assert.Equal(OrderStatus.Denied, order!.Status);
        Assert.Equal(0, engine.Exchanges[TestInstruments.Sim].OpenOrderCount);
    }
}
