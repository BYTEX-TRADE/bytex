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
| CCI | `CommodityChannelIndex(period)` | `Value` |
| MFI | `MoneyFlowIndex(period)` | `Value` 0–100 |
| CMO | `ChandeMomentumOscillator(period)` | `Value` −100–100 |
| Linear regression | `LinearRegression(period)` | `Value`, `Slope`, `Intercept`, `RSquared` |
| Supertrend | `Supertrend(period, multiplier)` | `Value`, `Direction`, `Flipped`, `Upper`, `Lower` |
| Parabolic SAR | `ParabolicSar(step, maximum)` | `Value`, `Direction`, `Flipped` |
| Ichimoku | `Ichimoku(conversion, base, spanB, displacement)` | `ConversionLine`, `BaseLine`, `CloudTop`, `CloudBottom`, `SpanAAhead`, `SpanBAhead`, `ChikouReference`, `CloudIsBullish` |
| Book imbalance | `BookImbalance(levels)` | `Value` −1–1, `BidSize`, `AskSize` |

With `excludeCurrent` the Donchian bands cover the `period` bars **before**
the current one. With the current bar in the window its own high is the upper
band, so "the close crossed above the upper band" can never be true; a
breakout rule needs the option on. The default leaves the current bar in.

### Ichimoku: which cloud

Two of Ichimoku's lines are plotted 26 bars forward and one back, so "the cloud"
means two different things and every implementation of it picks one. Both are
here, named for which they are:

- `SpanAAhead` and `SpanBAhead` are what has just been computed. They belong
  `Displacement` bars **in the future** and say nothing about the bar in hand.
- `CloudTop` and `CloudBottom` are the cloud **over this bar** - the spans
  computed `Displacement` bars ago. A strategy asking "is price above the cloud"
  means these.
- `ChikouReference` is the close `Displacement` bars ago, which is the price the
  chikou span compares the current close with.

`IsInitialized` waits for the cloud over the current bar to exist, not only for
the longest line to fill: an Ichimoku without its cloud is not one, and a
strategy waiting on the flag would otherwise be answered from zeroes.

### Indicators that refuse what they cannot measure

Most indicators take a price from anything. Four of these do not, and say so
rather than measuring nothing:

- **Ichimoku, Supertrend and the parabolic SAR** need a bar's high and low, so a
  single value, a quote or a trade is refused.
- **The money flow index** needs volume, which a quote does not carry.
- **The book imbalance** needs resting size, so a bar and a trade are refused - a
  trade is size that has gone. A quote is accepted as the one-level book it is.

### Book imbalance

`BookImbalance` reads the order book rather than a price, so it implements
`IOrderBookIndicator` and is registered for one:

```csharp
RegisterIndicatorForOrderBook(Config.MarketKey, _imbalance);   // updated before OnOrderBookDeltas
SubscribeOrderBookDeltas(Config.MarketKey);
```

The registration asks for that type, so an indicator that cannot read a book
cannot be registered for one - which is the mistake it would otherwise invite,
because every other indicator takes a price and would accept the registration
and measure nothing.

It answers a **share** between −1 and 1 rather than a size, because the
difference between two sides is not comparable between one instrument and
another or between a quiet hour and a busy one, and a threshold needs a number
that means the same thing in both. An empty book is not balanced: there is
nothing to measure, so `IsInitialized` goes false rather than the value reading
zero.

**What it is not** is a prediction. Resting size is what somebody has offered to
do and may withdraw, and the side with more of it is not the side that wins.

**A strategy document cannot use it yet.** A document's indicator nodes are fed
bars, and a document has no book input; the other seven are all available as
nodes (`ind.cci`, `ind.mfi`, `ind.cmo`, `ind.linreg`, `ind.supertrend`,
`ind.psar`, `ind.ichimoku`).

## Using them in a strategy

```csharp
private readonly RelativeStrengthIndex _rsi = new(14);

protected override void OnStart()
{
    RegisterIndicatorForBars(Config.CandleSeries, _rsi);   // updated before OnBar
    SubscribeBars(Config.CandleSeries);
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
