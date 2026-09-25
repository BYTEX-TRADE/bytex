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

## Starting from a saved state

A simulated venue lives in memory, so stopping the node loses what it held. To
start a node where the last one stopped, give the venue its state back:

```json
"startingBalances": ["9899 USDT"],
"restore": {
  "positions": [
    { "instrumentId": "BTCUSDT-PERP.BYBIT", "side": "long", "quantity": "0.1", "avgPx": "50000" }
  ],
  "orders": [
    { "clientOrderId": "O-STOP", "instrumentId": "BTCUSDT-PERP.BYBIT", "side": "sell", "type": "stopMarket",
      "quantity": "0.1", "triggerPrice": "49000", "reduceOnly": true, "linkedOrderIds": ["O-TARGET"] },
    { "clientOrderId": "O-TARGET", "instrumentId": "BTCUSDT-PERP.BYBIT", "side": "sell", "type": "limit",
      "quantity": "0.1", "price": "52000", "reduceOnly": true, "linkedOrderIds": ["O-STOP"] }
  ]
}
```

- Balances are not part of `restore`: put the balances the account had into
  `startingBalances`. A restored position moves no money until it is closed;
  the closing fill's profit or loss is measured from `avgPx`.
- `positions` is for a margin account. On a cash account a position is a
  balance of the base currency, so it goes into `startingBalances`, and a
  position in `restore` stops the start.
- `orders` are the orders that were resting: `limit`, `stopMarket`,
  `stopLimit`, `marketIfTouched`, `limitIfTouched`, with the quantity still to
  be filled. `timeInForce` (default `gtc`), `postOnly` and `reduceOnly` are
  kept. `linkedOrderIds` names the orders to cancel when this one fills, which
  is how a stop and its target stay a pair.
- The engine learns the state the way it does from a real venue, by
  reconciling at start (`reconcileOnStart`, on by default): the position comes
  in as the fill that opened it, at `avgPx` with no commission, under the
  client order id `RESTORED-<symbol>`, and the orders come in as open orders
  under their own ids. Reconciled orders belong to the strategy that claims the
  instrument in its `externalOrderClaims`; without a claim they belong to
  `EXTERNAL` and no strategy hears about them.
- A restored order starts matching once the engine has reconciled it, and
  from then on it can be modified and cancelled like any other.
- The instruments named in `restore` must be loaded when the client connects.
  If the venue cannot take the state as given (an instrument that is not
  loaded, a position on a cash account, an order type that does not rest) the
  node does not start, rather than start flat.
