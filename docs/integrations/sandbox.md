# Sandbox execution

Package: `Bytex.Live`. Factory name: `SANDBOX`.

The sandbox client pairs any live data client with simulated execution: it
subscribes to the venue's quotes, trades, and bars on the message bus and
matches orders with the same venue simulation used in backtests.

## Configuration

```json
{ "factory": "SANDBOX", "clientId": "BINANCE-SANDBOX", "config": {
    "venue": "BINANCE",
    "omsType": "netting",
    "accountType": "cash",
    "baseCurrency": null,
    "startingBalances": ["100000 USDT", "1 BTC"],
    "defaultLeverage": 1,
    "barExecution": "ohlcPath",
    "probFillOnLimit": 1.0,
    "probSlippage": 0.0,
    "latency": "00:00:00"
} }
```

`venue` must match the data client's venue so the instruments and prices line
up. The account id is `{VENUE}-SANDBOX`; balances start at `startingBalances`
and move with fills exactly as in a backtest.

## Behaviour

- Instruments are taken from the cache when the client connects and as they
  arrive on the bus.
- Fills, account states, and rejections are produced by the simulated venue
  and relayed under the sandbox account.
- Reconciliation returns the venue's open orders and positions, so restarting
  a node with persistence behaves like a live venue would.
