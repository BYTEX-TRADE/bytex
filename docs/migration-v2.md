# V2 Breaking Changes

This candidate changes source APIs, configuration contracts and storage formats.
The candidate package version is 0.11.0. Coordinated skills publication and
explicit owner acceptance remain required before release.
No compatibility or legal independence claim follows solely from renaming.

## Source And Configuration

MarketArchive replaces ParquetDataCatalog; MarketKey replaces InstrumentId;
CandleSeries/SamplingRule/SamplingMethod/CandleOrigin replace the old bar identity
types. Origins are Provider and Computed. EventTime and CreatedTime replace the
old timestamp property names. TradingRuntime, ModuleHost, RuntimeModule,
MarketDataService, OrderCoordinator, OrderPolicy, OrderSchedule and TwapSchedule
replace the former runtime/host/module/engine/schedule vocabulary.

JSON uses tradingRuntime/moduleHostId, marketKey/candleSeries, and document
schemaVersion 2.0 with candleSeriesDefinitions. Normal parsers reject old identity
strings; explicit conversion lives in the migration APIs and CLI. Bar bus topics
contain the new candle identity. Custom consumers must rebuild subscriptions.

Identities use `bx-market:v2/VENUE/<escaped-symbol>`,
`bx-candle:v2/VENUE/<escaped-symbol>/method/step/price/origin`, and
`bx-sampling:v2/method/step/price`. CLI CSV import uses `--candle-series`, not the
old option. Archive objects are opaque and manifest-backed; see
[Archive v2](concepts/archive-v2.md) for conversion, recovery and maintenance.

Rebuild all plugins against the candidate. Passing rebuilt example-plugin tests
does not establish compatibility of every third-party plugin or old binary. Old
plugins may load where their API usage is unaffected, or fail on renamed members;
there is no promise of universal binary compatibility or automatic conversion.

## Preserved Contracts

Financial semantics, fixed-point amounts, model event ordering and venue-native
symbols are preserved. Vendor field names such as Databento ts_event and
instrument_id remain vendor contracts. EXTERNAL remains a historical reconciliation
strategy identity, not a candle origin. Human audit records retain their persisted
actor field and historical values while local code uses AuditIdentity/AuditContext
and AuthoredBy. Framework names such as IsExternalInit are not BYTEX vocabulary.

Historical conversion fixtures and change records intentionally show old names.
They are evidence and migration input, not supported normal-reader aliases.

## Release Gate

Review API snapshots, full regressions, offline skills, read-only venue checks,
conversion and recovery evidence, source preservation, plugin checks and ordered
replay parity together. Skipped service tests and owner-only operational workflows
remain explicit limitations.
Publication, repository changes and final owner acceptance are separate actions.
