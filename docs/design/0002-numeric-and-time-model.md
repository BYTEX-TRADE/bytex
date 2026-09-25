# 0002 — Numeric and time model

Status: accepted

## Problem

Prices, quantities, and money must be exact, comparable, and carry the
precision their venue defines. Time must be precise enough to order events
from venues that timestamp in nanoseconds and must be identical in meaning
across backtest and live contexts.

## Decision

### Numbers

All monetary and size values are `System.Decimal` wrapped in value types that
carry an explicit precision:

```csharp
public readonly record struct Price(decimal Value, byte Precision) : IComparable<Price>;
public readonly record struct Quantity(decimal Value, byte Precision) : IComparable<Quantity>;
public readonly record struct Money(decimal Amount, Currency Currency) : IComparable<Money>;
public sealed record Currency(string Code, byte Precision, CurrencyType Type);
```

- `Precision` is the number of decimal places the venue allows. Values are
  rounded to that precision on construction using `MidpointRounding.ToEven`
  unless an instrument method (`MakePrice`, `MakeQuantity`) applies the
  venue's tick rounding rule explicitly.
- `Quantity` is never negative. Direction is always carried by `OrderSide` or
  `PositionSide`, never by sign.
- `Money` arithmetic between different currencies throws.
- `decimal` gives 28–29 significant digits, which covers every listed venue's
  precision requirements (crypto venues use up to 8–10 decimal places; the
  widest observed is 18 for some token quantities, still within range).
- `double` is permitted only in indicators and statistics, where values are
  derived and not used for order placement without passing through an
  instrument's `MakePrice`/`MakeQuantity`.

### Time

```csharp
public readonly record struct UnixNanos(long Value) : IComparable<UnixNanos>
{
    public DateTimeOffset ToDateTimeOffset();
    public static UnixNanos FromDateTimeOffset(DateTimeOffset value);
    public static UnixNanos Parse(string iso8601);
    public UnixNanos Add(TimeSpan span);
    public UnixNanos AddNanos(long nanos);
}
```

- Every data element and event carries two timestamps: `TsEvent` (when the
  thing happened at the source) and `TsInit` (when the object was created in
  this process). In backtests `TsInit` comes from the `TestClock`, so it is
  reproducible.
- The engine orders data by `TsInit` and then by insertion sequence. Ties are
  therefore stable.
- `UnixNanos` is a 64-bit signed nanosecond count since the Unix epoch (range
  1678–2262). `.NET` `DateTimeOffset` has 100 ns resolution; conversions
  truncate and are documented as lossy.
- Durations use `TimeSpan` (100 ns ticks). Timer intervals below 100 ns are
  not supported.

### Identifiers

Identifiers are `readonly record struct` wrappers over `string` with
validation in the constructor. An `InstrumentId` is `Symbol` + `Venue`
rendered as `SYMBOL.VENUE` (for example `BTCUSDT.BINANCE`). Parsing is the
inverse; the last dot separates venue from symbol so symbols may contain dots.

## Alternatives considered

- **Fixed-point integers (scaled `long`/`Int128`).** Faster but forces every
  arithmetic path to track scale manually and invites precision bugs when
  mixing instruments. `decimal` is native, exact, and fast enough for the
  targeted throughput.
- **`DateTime`/`DateTimeOffset` for timestamps.** 100 ns resolution loses
  ordering information from nanosecond-stamped feeds and makes tie-breaking
  venue-dependent.

## Consequences

- Value types are small structs; collections of ticks are cache-friendly.
- Serialization (JSON, Parquet) stores `decimal` as strings or fixed-point
  integers with scale; never as floating point.
- Any `double` that reaches an order path is a bug and is treated as such in
  review.
