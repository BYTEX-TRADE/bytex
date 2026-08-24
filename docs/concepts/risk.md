# Risk

Every trading command passes through the `RiskEngine` before the execution
engine. Denied orders receive an `OrderDenied` event with the reason; denied
modifications receive `OrderModifyRejected`.

## Checks on submission

| Check | Reason code |
|---|---|
| Trading state is `Halted` | `TRADING_HALTED` |
| Trading state is `Reducing` and the order would not reduce a position | `TRADING_REDUCING` |
| Instrument not in the cache | instrument not found |
| Quantity precision above the instrument's, zero, above max, below min | `QUANTITY_*` |
| Price or trigger precision above the instrument's, non-positive, outside min/max | `PRICE_*` |
| Notional above the instrument's max / below min / above `MaxNotionalPerOrder` | `NOTIONAL_*` |
| Cash account free balance cannot cover a non-reduce-only order | `INSUFFICIENT_BALANCE` |
| More than `MaxOrderSubmitRate` submissions per `OrderRateInterval` | rate exceeded |

Modifications are checked for precision and limits on the new values and for
`MaxOrderModifyRate`.

## Configuration

```csharp
new RiskEngineConfig
{
    Bypass = false,
    MaxOrderSubmitRate = 100,
    MaxOrderModifyRate = 100,
    OrderRateInterval = TimeSpan.FromSeconds(1),
    MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [id] = 50_000m },
    CheckCashBalance = true,
}
```

`RiskEngine.SetTradingState(TradingState.Halted | Reducing | Active)` changes
behaviour at runtime, for example from an operator command or a monitoring
actor.
