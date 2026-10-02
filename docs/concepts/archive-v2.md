# Archive v2

This is a breaking-format migration candidate, not an already published release.
Do not replace an existing archive in place. Freeze and preserve its source first.

## Layout

Instrument definitions are `instruments/{encoded-market-key}.json`. Six data kinds
are supported: `bars`, `quotes`, `trades`, `book_deltas`, `book_depth`, and `funding`.
Each stream has `{kind}/{encoded-key}/segments/{32-hex-id}.parquet` objects and
immutable `commits/{32-hex-id}.json` manifests. Encoding uses ObjectKeyCodec for
storage keys and MarketKey/CandleSeries for identities; never assemble filenames
from a symbol or a timestamp. Empty-history spans remain explicit metadata.

A format-2 manifest carries its sequence, ordered segments, replaced object keys,
and an optional migration source hash. Each segment carries its key, inclusive
CreatedTime range, row count, byte count, SHA-256 digest, and ordinal. A segment
holds at most 100,000 rows. Reads verify committed object size and digest before
deserialization. Range pruning consults manifest metadata, not filenames.

## Publication And Recovery

All chunks of one stream batch are staged under unique names, verified, and then
published by one complete manifest. A crash before manifest publication leaves
uncommitted objects invisible to normal readers. Atomicity is per stream batch,
not across every stream in a mixed IData call. Retrying an ordinary append after
an ambiguous successful commit may duplicate rows; only conversion has stable,
source-derived checkpoint identities and idempotent resume.

Consolidation stably orders and deduplicates rows, stages bounded replacements,
and publishes one manifest replacing the previous active segment keys. Previous
segments and manifests remain on disk. There is no automatic purge or vacuum.
`bytex catalog recovery --path <archive>` inventories retained objects without
deleting them. `bytex catalog check --path <archive>` reports invalid manifests,
missing/corrupt segments, row-count mismatch and overlapping ranges.

Concurrent appenders use unique segment and commit keys. Conversion and
consolidation require an exclusive maintenance window: the store contract has no
compare-and-swap transaction. Conflicting replacement manifests are refused for
explicit investigation, not silently resolved. Backends must publish complete
objects atomically and provide consistent listings; arbitrary eventually
consistent stores are not validated. In-memory object-store tests do not replace
testing a configured real S3-compatible endpoint.

## Conversion

```bash
bytex migrate archive --source ./legacy-catalog --destination ./v2-catalog
bytex migrate document --source ./legacy-strategy.json --destination ./v2-strategy.json
bytex catalog check --path ./v2-catalog
```

Use separate, non-nested source/destination stores. A new destination must be
empty. The converter pins a SHA-256 inventory of every source object, rewrites
typed identities and timestamp fields, verifies output rows before committing,
and records completion only after all objects are handled. Unrecognized objects
and unknown row columns require review instead of silent omission. Legacy empty
Parquet segments require explicit review. One legacy file is materialized in
memory; output segments are bounded, but total conversion memory is not.

Document conversion preserves an explicit legacy id. Where id was omitted, it
assigns a deterministic id derived from the exact input text; identical-input
retries therefore produce identical output. Existing different output is refused,
not overwritten. Whitespace edits to an id-less source change its derived id.

Repeat the identical command to resume an interrupted conversion. Changed source
inventory or corrupt completed checkpoints are refused. Failed staging may leave
recovery objects; successful resume does not duplicate committed source files.
Keep the source quiescent for the entire conversion, including the interval
between final validation and acceptance. Do not delete either source or recovery
objects until backups and owner acceptance are independently confirmed.

After conversion, validate strategy documents against the new archive, rebuild
plugins, compare replay results, and obtain owner acceptance before changing any
consumer location. Rollback means selecting the preserved original installation
and archive, not rewriting v2 data back into the originals.
