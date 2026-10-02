# Concepts

| Page | What it covers |
|---|---|
| [Architecture](architecture.md) | tradingRuntime, message bus, engines, environment contexts, threading |
| [Strategies](strategies.md) | the RuntimeModule/Strategy programming model and its guarantees |
| [Orders](orders.md) | order types, time in force, instructions, contingencies, state machine |
| [Data](data.md) | market data types, subscriptions, bar aggregation, the catalog |
| [Backtesting](backtesting.md) | simulated venues, fill and latency models, results |
| [Live trading](live.md) | trading node, reconciliation, sandbox, persistence |
| [Risk](risk.md) | pre-trade checks and trading state |
| [Indicators](indicators.md) | built-in indicators and registration |
| [Object storage](object-storage.md) | a catalog on S3-compatible storage: naming it, what it costs, what is verified |
| [Redis](redis.md) | cache state and bus messages in Redis: keys, what is stored, what is not |
| [PostgreSQL](postgres.md) | the same state in PostgreSQL: four tables, the schema, what is verified |
| [Plugins](plugins.md) | extending the engine with strategies, adapters, and indicators |
| [Strategy documents](documents.md) | strategies as data: the format, the node catalog, validation, and the runtime that trades one |

The design notes under [docs/design](../design/) are the authoritative
reference for the public contracts; these pages explain how to use them.
