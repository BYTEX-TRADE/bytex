# Your first backtest

This walkthrough builds a strategy, feeds it bars, and reads the results. The
complete code is in `examples/Bytex.Examples`.

## 1. Define an instrument

Instruments carry the precision and limits the engine uses to round prices
and quantities and to validate orders.

```csharp
CurrencyPair instrument = new(new InstrumentSpec
{
    Id = InstrumentId.Parse("BTCUSDT.SIM"),
    AssetClass = AssetClass.Crypto,
    InstrumentClass = InstrumentClass.Spot,
    QuoteCurrency = Currencies.USDT,
    BaseCurrency = Currencies.BTC,
    PricePrecision = 2,
    SizePrecision = 6,
    PriceIncrement = new Price(0.01m, 2),
    SizeIncrement = new Quantity(0.000001m, 6),
    MakerFee = 0.001m,
    TakerFee = 0.001m,
});
```

## 2. Write a strategy

```csharp
public sealed record EmaCrossConfig : StrategyConfig
{
    public required InstrumentId InstrumentId { get; init; }
    public required BarType BarType { get; init; }
    public int FastPeriod { get; init; } = 10;
    public int SlowPeriod { get; init; } = 20;
    public decimal TradeSize { get; init; } = 1m;
}

public sealed class EmaCross : Strategy<EmaCrossConfig>
{
    private readonly ExponentialMovingAverage _fast;
    private readonly ExponentialMovingAverage _slow;

    public EmaCross(EmaCrossConfig config) : base(config)
    {
        _fast = new ExponentialMovingAverage(config.FastPeriod);
        _slow = new ExponentialMovingAverage(config.SlowPeriod);
    }

    protected override void OnStart()
    {
        RegisterIndicatorForBars(Config.BarType, _fast);
        RegisterIndicatorForBars(Config.BarType, _slow);
        SubscribeBars(Config.BarType);
    }

    protected override void OnBar(Bar bar)
    {
        if (!IndicatorsInitialized) return;
        Instrument instrument = Cache.Instrument(Config.InstrumentId)!;

        if (_fast.Value > _slow.Value && Portfolio.IsFlat(Config.InstrumentId))
            SubmitOrder(OrderFactory.Market(Config.InstrumentId, OrderSide.Buy, instrument.MakeQuantity(Config.TradeSize)));
        else if (_fast.Value < _slow.Value && Portfolio.IsNetLong(Config.InstrumentId))
            CloseAllPositions(Config.InstrumentId);
    }

    protected override void OnStop()
    {
        CancelAllOrders(Config.InstrumentId);
        CloseAllPositions(Config.InstrumentId);
    }
}
```

Handlers run on the engine thread one at a time; by the time `OnBar` runs the
bar is already in the cache and the indicators are updated.

## 3. Run it

```csharp
BarType barType = new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

using BacktestEngine engine = new(new BacktestEngineConfig(), loggerFactory);
engine.AddInstrument(instrument);
engine.AddVenue(new SimulatedVenueConfig
{
    Venue = new Venue("SIM"),
    AccountType = AccountType.Cash,
    StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
});
engine.AddData(bars);                       // IEnumerable<Bar>, sorted or not
engine.AddStrategy(new EmaCross(new EmaCrossConfig
{
    StrategyId = new StrategyId("EmaCross-001"),
    InstrumentId = instrument.Id,
    BarType = barType,
    FastPeriod = 10,
    SlowPeriod = 30,
    TradeSize = 0.5m,
}));

engine.Run();
BacktestResult result = engine.GetResult();
Console.WriteLine(result.Summary());
ReportWriter.WriteAll(result, "reports/ema-cross");
```

`Summary()` prints period, order and position counts, win rate, and per
currency P&amp;L, drawdown, Sharpe and Sortino ratios, profit factor, and
expectancy. `ReportWriter.WriteAll` writes orders, fills, positions, account
balances, and equity curves as CSV.

## 4. Where data comes from

- In memory: any `IEnumerable<IData>` (bars, quote ticks, trade ticks, book deltas).
- From CSV: `CsvLoader.LoadBars(path, instrument, barType)` and friends.
- From a catalog: `ParquetDataCatalog.BarsAsync(barType, start, end)`.
- From a venue: the Binance and Bybit adapters answer `RequestBars` and
  `RequestTradeTicks`; the Tardis adapter serves historical trades, quotes,
  and book deltas.

Next: [running from configuration with the CLI](cli.md).
