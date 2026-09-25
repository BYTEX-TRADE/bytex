# Indicators

`Bytex.Indicators` provides decimal-based indicators that update from bars,
quotes, trades, or raw values. Each exposes `IsInitialized` (enough input
received), `HasInputs`, `Reset()`, and its values. A period below one is
refused by the constructor, and `IsInitialized` means the value is the one the
textbook defines - an indicator built from other averages waits for those to
fill rather than reporting a value that is still settling. ADX is seeded the
way Wilder defined it, so its first value is on bar 2N-1.

| Name | Class | Values |
|---|---|---|
| SMA | `SimpleMovingAverage(period)` | `Value` |
| EMA | `ExponentialMovingAverage(period)` | `Value` |
| WMA | `WeightedMovingAverage(period)` | `Value` |
| DEMA | `DoubleExponentialMovingAverage(period)` | `Value` |
| HMA | `HullMovingAverage(period)` | `Value` |
| VWAP | `VolumeWeightedAveragePrice()` | `Value` (resets daily) |
| RSI | `RelativeStrengthIndex(period)` | `Value` 0–100 |
| MACD | `MovingAverageConvergenceDivergence(fast, slow, signal)` | `Value`, `Signal`, `Histogram` |
| ROC | `RateOfChange(period)` | `Value` (%) |
| Stochastics | `Stochastics(k, d)` | `ValueK`, `ValueD` |
| OBV | `OnBalanceVolume()` | `Value` |
| ATR | `AverageTrueRange(period)` | `Value` |
| Bollinger | `BollingerBands(period, k)` | `Upper`, `Middle`, `Lower`, `Width` |
| Keltner | `KeltnerChannel(period, k, atrPeriod)` | `Upper`, `Middle`, `Lower` |
| Donchian | `DonchianChannel(period, excludeCurrent)` | `Upper`, `Middle`, `Lower` |
| ADX | `AverageDirectionalIndex(period)` | `Value`, `PlusDi`, `MinusDi` |
| Aroon | `AroonOscillator(period)` | `AroonUp`, `AroonDown`, `Value` |

With `excludeCurrent` the Donchian bands cover the `period` bars **before**
the current one. With the current bar in the window its own high is the upper
band, so "the close crossed above the upper band" can never be true; a
breakout rule needs the option on. The default leaves the current bar in.

## Using them in a strategy

```csharp
private readonly RelativeStrengthIndex _rsi = new(14);

protected override void OnStart()
{
    RegisterIndicatorForBars(Config.BarType, _rsi);   // updated before OnBar
    SubscribeBars(Config.BarType);
}

protected override void OnBar(Bar bar)
{
    if (!_rsi.IsInitialized) return;
    if (_rsi.Value < 30m) { /* ... */ }
}
```

Indicators can also be driven manually with `Update(bar)`, `Update(tick)`, or
`UpdateRaw(value)`.

## Writing your own

Derive from `Indicator`, implement `UpdateRaw(decimal)`, and override
`Update(Bar)` if you need the full OHLCV. `RollingWindow` provides a
fixed-size window with sum, min, max, average, and standard deviation.

## Configuration-driven creation

`BuiltinIndicatorFactory` creates indicators by name with string parameters
(`"RSI"`, `{ "period": "14" }`), and plugins may register their own
`IIndicatorFactory`.
