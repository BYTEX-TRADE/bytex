# Changelog

Notable changes to BYTEX, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, a minor version may
change public APIs; a patch version only fixes.

## [Unreleased]

The 0.11.0 candidate below is awaiting review and owner acceptance; it has not
been published.

## [0.11.0] - Unreleased

### Changed

- Breaking source and configuration vocabulary: MarketKey, CandleSeries,
  SamplingRule, CandleOrigin (Provider/Computed), TradingRuntime, ModuleHost,
  RuntimeModule, MarketDataService, OrderCoordinator, OrderPolicy and
  OrderSchedule. Timestamp properties are EventTime and CreatedTime.
- Versioned market and candle identities, document schema 2.0, and the
  `--candle-series` CSV-import option. Normal readers reject legacy identities;
  rebuild plugins against this candidate.
- MarketArchive stores immutable opaque segments with checksum-bearing manifests
  instead of timestamp-derived filenames. Consolidation publishes replacement
  manifests while retaining superseded objects for recovery.
- Current documentation and dependency notices reflect the candidate contracts.

### Added

- Explicit legacy document and archive conversion into separate destinations,
  with source verification, resumable archive checkpoints and deterministic
  document IDs when a legacy document omits its ID.
- Read-only archive recovery inventory and validation of malformed manifests.
- Conversion, interrupted-write, concurrent-append, API-surface and plugin
  regression coverage, plus paired original/candidate replay verification.

See [V2 breaking changes](docs/migration-v2.md) and
[Archive v2](docs/concepts/archive-v2.md) for migration instructions and limits.
This section does not authorize publication or assert universal binary
compatibility with 0.10.0.

## [0.10.0] - 2026-09-30

A state store that is not Redis, and five things the engine accepted while quietly doing
something else.

A MINOR and not a patch, because it adds: `Bytex.Persistence.Postgres` is a new packable
library with two public types and a surface file of its own. Before 1.0 a patch only fixes,
which this does not, so the number says so.

Every fix here was silent: an order reported against a strategy that does not exist, a
window one bar short at its newest end, thirteen settings read and discarded, and a
document member misspelled and ignored. None of them failed anything - they returned
a number, and the number looked like a number.

### Fixed

- **A reconcile that fails is counted and says why, instead of looking like one that found nothing.** The
  reconciliation a node reports carried a check count, a difference count, a timestamp and an interval - and
  nothing at all about an attempt that never happened. So a node whose every reconcile threw looked identical to
  one agreeing with its venue perfectly: checks nothing, differences nothing, status running. That is how a node
  which could not place an order for an hour - every signed request refused over a drifted clock - showed as
  healthy while the reason sat in a log nobody reads. `ReconciliationFailures`, `LastReconciliationError` and
  `LastReconciliationFailure` are counted separately from the checks, because an attempt that produced no report
  is not a reconciliation that found nothing to correct, and both the periodic attempt and the one at start-up now
  record before the failure goes anywhere else.

- **A signed request carries the VENUE's time, not this machine's - so a node whose clock has drifted can still
  trade.** Every signing adapter stamped its requests from the host clock, which made correctness depend on the
  machine being right. A machine routinely is not: a second of drift is ordinary on a desktop, where the operating
  system resynchronises on a long interval. On a real node the effect was as bad as it gets and looked like
  nothing at all - public data connected and streaming, the node reporting itself running with a strategy loaded,
  and **every signed request refused**, so it could neither place, cancel nor reconcile an order. The host was
  1,067 ms ahead. **Raising the venue's tolerance window is not the fix**, which is the intuitive move and a wasted
  afternoon: the rule is asymmetric, so the window buys tolerance only for a clock that LAGS, while one running
  ahead is capped at a fixed second whatever the window says. `VenueClock` measures the offset against the venue's
  own public time endpoint - taking the midpoint of the round trip, so the network delay is not charged to the
  offset - adds it to every signed timestamp, re-measures when it grows stale, and re-measures at once when a venue
  refuses a timestamp outright. Wired through Binance, Bybit, Bitget, Gate and KuCoin, REST and the private
  websocket logins alike; every endpoint and response shape was measured against the live venues rather than read
  from documentation. **Kraken and Hyperliquid are deliberately untouched**: they sign with a monotonic nonce, not
  a timestamp the venue compares against its own clock, and both already take the maximum of their counter and now
  - so no clock correction can move a nonce backwards. A test holds the arithmetic and the staleness rules, and a
  source guard holds every adapter to it with no exemption list, which is why the one remaining host-clock call in
  Gate's public subscribe was found and wired too. Confirmed by signing with the host clock again and watching the
  guard name the file and line.

- **A history request for an instrument the client cannot resolve now refuses, instead of answering with an
  empty list.** Every data client did the same thing: look the instrument up, and on a miss `return []`. That is
  the same answer as "the venue holds no history for that window", so a caller could not tell apart the case
  where the venue was asked and had nothing from the case where the venue was never asked at all. The second is
  the one worth knowing about, and it was the quieter of the two: a chart that draws nothing and a warm-up that
  finds no bars look identical to a person, and neither says the instrument was simply not loaded. **The
  machinery to say so already existed and was unused** - every request path already turns a thrown exception
  into a logged error and an error response carrying the reason, so the refusal reaches the requester rather
  than the log alone. `DataClientBase.RequireLoaded` now names the instrument and says the venue was never
  asked, at **twenty-three sites across eleven data clients** - every venue, for bars, trades and funding
  alike. A test drives a real client for both the refusal and the case that must still return data, and a
  second reads the source of every `*DataClient.cs`, because a fix applied by hand at twenty-three sites is one
  that comes back the first time a twelfth adapter is written from an existing one as a template. Confirmed by
  restoring one site and watching both fail.

- **A backtest can be fed the book depth the catalog stores.** The catalog gained somewhere to put a book
  so that a run would read it once instead of fetching it from the vendor on every execution - and a run
  could not ask for it: `BacktestDataConfig.DataKind` accepted `quotes`, `trades`, `bars`, `book_deltas`
  and `funding`, then refused everything else as an unknown kind, on both the loaded and the streamed
  path. Only a hand-assembled `BacktestEngine` could use stored depth; nothing driven by configuration
  could, which is every run the CLI starts. The engine itself was never the problem - it has routed depth
  since 0.6. The doc comment on that member was wrong twice over, naming four kinds and omitting funding
  as well. **A data kind has to be registered in three separate places** - the catalog's table of kinds,
  the dispatch that consolidates one, and this configuration - and two of the three had already been
  missed. So the test does not list them: it asks the catalog what it holds and requires every answer to
  load and to stream. A seventh kind forgotten in any of the three now fails without anybody remembering
  to look. Confirmed by removing both cases again and watching three of the four new tests fail.

- **`bytex catalog consolidate` crashed once book depth was stored.** `book_depth` was added to the table of
  kinds the catalog holds but not to the dispatch that consolidates one, so it passed the name check - the
  catalog does hold it - and then threw **"'book_depth' is not a kind this catalog holds"**, a message
  contradicting itself. Consolidating with no `--kind` walks every data set, so a catalog with depth in it
  could not consolidate anything, and the unhandled exception aborted the loop before the kinds ordered after
  it. `--kind` did not list `book_depth` either. The dispatch is now driven in a test from the catalog's own
  listing of kinds, so the next kind added cannot repeat this. Reachable, not hypothetical: writing depth
  twice was enough, and bounded files make it a single write.

- **`catalog consolidate` no longer reports "-> 1" when it means more.** A data set past the file bound
  consolidates to several files, and the count printed is now computed from the records rewritten rather than
  assumed.

- **Two order books with the same levels are now the same book.** `OrderBookDepth` holds each side as an
  `IReadOnlyList` and `OrderBookDeltas` holds its deltas the same way, so the equality the compiler
  generates for a record compared the list **references**: two records with identical contents were never
  equal. Every other market-data record is made of value types, which is why only these two were wrong.
  It failed in the way that costs the most time - nothing threw and nothing reported which field
  differed, the answer was simply always "different", so anything deduplicating a repeated snapshot
  quietly did nothing and the obvious test failed with both sides printing identically. Both now compare
  their contents element for element. `GetHashCode` deliberately hashes the scalars and the two ladder
  **lengths** and not the levels themselves: the contract a hash owes is that equal values hash alike,
  and walking fifty levels on every dictionary lookup would cost more than the collisions it avoids.
  Confirmed by reverting to generated equality and watching four of the fourteen new tests fail,
  the `HashSet` case among them.

- **The book-snapshot loader read the wrong columns, and both ladders came out crossed.**
  `book_snapshot_25` is **interleaved**, four columns to a level - `asks[L].price, asks[L].amount,
  bids[L].price, bids[L].amount` - and it was read as twenty-five asks followed by twenty-five bids.
  Nothing threw, because a price and its amount stay adjacent under either reading. What came out was an
  ask ladder alternating with the bids beneath it and a bid ladder starting at the thirteenth bid and
  alternating with asks priced above it: two non-monotonic, internally crossed ladders of real prices,
  from the right instrument, at the right timestamp. Book-depth fills bind against exactly this, so a
  resting buy would have sized against the other side - worse than having no depth, because it is
  silently plausible. **The fixture had the same assumption in it**, so it round-tripped and proved only
  that the parser agreed with the test; only the vendor's own file disagreed. It is now a checked-in
  trimmed slice of that file, and the assertions are the ones a wrong layout cannot satisfy: each side
  moving only away from the touch, and a best bid strictly below the best ask. Confirmed by restoring the
  shipped arithmetic and watching it fail at level 1.

- **A key is no longer required to construct the Tardis client.** It called `Secrets.Require`, which
  throws, so the free datasets were unreachable through a client that needs nothing to read them. A key is
  still sent when there is one, and a paid dataset requested without one now fails at the request with the
  vendor's own answer rather than at construction with a message about configuration.

- **A body that is not the file is said so, and dataset downloads obey the rate limiter.** The download
  path checked the status code only, so a request this vendor will not serve - a ranged one among them -
  answered with an HTML error page under HTTP 200, which reached the decompressor and died as an
  `InvalidDataException` naming neither the request nor the reason. The body is now checked for the gzip
  marker in its first two bytes, with what arrived and the body itself in the message. **By its bytes and
  not its declared type, for a measured reason: the vendor serves these files as `text/csv`**, so a
  content-type check would have refused every real download. The same path used to build an HTTP client
  per call, bypassing the client's own rate limiter and retry policy on the one method that fetches
  ninety-megabyte files; `HttpClientWrapper.GetBytesAsync` now carries it through both.

- **`documents validate --json` answers in JSON when the document cannot be read at all.** The document was
  read before the flag was looked at, so a member this engine does not model, or JSON that does not parse,
  printed the exception's own words as prose - and anything parsing the output, a script, a CI step or a
  model deciding what to fix next, got a parse error where the finding was. The exit code was right, which
  is what let it survive: whatever checked only the code was satisfied and whatever read the output was not.
  Strict document reading did not create this; broken JSON always did it. What changed is how often, because
  a misspelled member now stops the read. Two more ways to fail had the same hole: an unknown
  `--environment` also printed prose, and a path that does not exist threw with nothing handling it, so it
  answered with a stack trace. All three now emit the same keys the success path emits - `name`, `valid`,
  `stoppedAt`, `findings` - through one serializer, so a caller that can parse the good case can parse the
  bad one. With `--json` the report goes to **stdout** whatever the outcome, because the exit code already
  carries the failure and `validate --json | jq` has to see something; prose keeps going to stderr.

- **A mixed batch no longer loses the kinds the catalog cannot store.** `WriteAsync` ran five type filters
  over the list and returned success, so anything else in it was discarded in silence - no exception, no
  count, nothing to notice. That was every kind of book depth, both price updates, instrument status,
  signals and custom data: a caller handing over a list got back a promise the catalog had not kept, and
  what went missing was missing rather than visibly wrong. Depth and batched deltas are now written -
  a batch of deltas is deltas, so it is unrolled into them rather than turned away - and anything the
  catalog genuinely has no home for is refused by name and count, after the rest of the batch has been
  written, saying what a catalog does hold.

- **An execution for an order a node never placed is ignored, on every venue.** A venue's private stream
  carries everything that happens to an *account* - a person trading the same key by hand, another node on the
  same key, an order left behind by a previous run - and ten of the twelve stream handlers reported those
  under a made-up `EXTERNAL` strategy. That looked like a decision and was not one: the engine looks a stream
  event up by client order id and then by venue order id, and finding neither it logs "for unknown order" and
  drops the event, so the attribution only ever produced a log line about somebody else's order. It did not
  reach a strategy that had *claimed* the instrument either, because `ExternalOrderClaims` are honoured in
  reconciliation and a stream handler bypasses reconciliation. The two Kraken handlers refused instead, so the
  engine held two opposing conventions and both were tested; refusing is now the rule everywhere, and
  `EXTERNAL` keeps its documented home in reconciliation, where a claim is read and an order is actually
  adopted. Adapters ask one shared question, `OrderPlacedHere(clientOrderId, venueOrderId)`, which tries both
  ids - a venue may echo an id it was not sent, and the engine indexes venue order ids for exactly that, so a
  handler refusing on the client id alone would throw away events for its own orders. Nine adapters gain that
  fallback with the refusal. Found by CI: the Kraken test that asserts the refusal held on a developer machine
  and failed on the runner, because it asserted that the order's own local event arrived before anything from
  the socket - a race. Every such test now proves the point by socket order, and two guards keep the ruling
  from drifting: one that lets only a reply to an engine command name `EXTERNAL`, and one that requires every
  execution client with a private stream to ask the question.

- **The broker id Kraken's futures platform carries is proved to depart.** The declaration says this
  platform puts an id in a field of the order and the client does put one there, and nothing asserted
  that it arrives on the wire - the rig even took a broker id that no test passed. Spot had both halves;
  its sibling had the parameter and no test behind it, which is the shape a declaration takes when it is
  true of the code and unproven. Both halves are now held: the field departs when an id is configured,
  and the untagged path is byte-for-byte what it was. Whether the venue ACCEPTS it still needs an
  approved account - the platform documents the parameter as pre-production only and validates neither
  parameters nor paths before the signature, so an unapproved id may simply be ignored.

- **A document loaded by path is given the settings its payload names.** A payload naming a file built its
  config from a hand-written list of three fields, where the record has eighteen, so thirteen settings a
  person wrote were read and discarded without a word: `useHyphensInClientOrderIds`, `warmupBars`,
  `orderIdTag`, `omsType`, `externalOrderClaims`, `manageContingentOrders`, `manageGtdExpiry`,
  `emitDecisionEvents`, `decisionHistory`, `annotationWindow`, `runtimeModuleId`, `logEvents`, `logCommands`. The
  inline shape deserialized the whole object and was right all along, which is why the two disagreed.
  What it cost was not evenly spread. OKX refuses a client order id containing a hyphen, so
  `"useHyphensInClientOrderIds": false` being dropped meant every order of every document loaded by path
  was refused by the venue - while the adapter's own message told the operator to set the flag that could
  not be set. `warmupBars` being dropped is a live node starting with empty indicators.
  The list is gone rather than longer: the path shape now rewrites its payload into the inline shape, so
  there is ONE deserialization for both and a setting added to the record is honoured by both without an
  edit here. It had already fallen behind twice - `environment` was added to that list when a paper run
  came up with backtest rules, and the rest were left. A payload naming a document twice, by path and
  inline, is now refused rather than having one of them silently win.

- **A node's own `config` block reaches the strategy whole.** The merge beside it was a hand-written list
  of six where `StrategyConfig` has seven, and the seventh was `UseHyphensInClientOrderIds` - so that
  setting could not be reached from a node configuration either, by any shape of payload. Every member of
  `StrategyConfig` and of `RuntimeModuleConfig` is carried now, and a test compares the two payload shapes member
  by member by reflection, so the next member added is compared without anybody remembering to.

- **Bitget gave back every window one bar short, and the missing one was the newest.** Its
  `history-candles` endpoint bounds a candle by its CLOSE and this helper read it as bounding the OPEN, so
  the bound it sent asked for the candle before the one asked for. Asked for two days of hourly bars it
  answered 49 where Binance, Bybit, KuCoin, OKX, Kraken, Gate and Hyperliquid all answered 50 - and the bar
  it dropped is the most recent one, the bar a run is about to act on. Spot and USDT futures both, since
  they share the helper. The first bound now reaches one interval past the last open wanted, which is past
  that candle's close; `BarWindow.Closed` already trims anything beyond the end and anything still forming,
  which is how the other helpers absorb their own endpoints' quirks. The paging step was right all along -
  the bound it sets is an open, and the candle closing at that open is the next one older - which is why
  only the newest bar was ever lost.

- **The recording had the same wrong assumption, so no offline test could see it.** The stub answered by
  open, the helper asked by open, and both halves of a wrong pair agreed forever: asked for the reported
  windows, the offline suite returned exactly the 50 and 25 it should while the venue returned 49 and 24.
  The recording now filters by close, as measured - `endTime=2026-09-18T04:00:00.001Z` answers with the
  candle opening at 03:00, not 04:00 - and with it corrected the defect reproduces offline, along with
  three other tests that had been quietly agreeing with it. This is the limit of R12.8 stated plainly: a
  recording is only as true as the day it was taken.

- **A member this engine does not model in a document is refused rather than skipped.** The rule 0.9.1 set
  for every configuration, applied to documents on the owner's ruling: strict everywhere. Both levels were
  silent. A strategy payload accepted a misspelled setting - and since the fix above made fifteen of them
  work, somebody who misspelled the sixteenth had every reason to believe it took effect; on a venue that
  refuses a hyphen in a client order id that meant every order refused with nothing pointing at the typo.
  Inside the document, a misspelled `evaluation` fell back to the bar close, so the run evaluated on closed
  bars while the document said ticks and every figure it produced looked exactly like a figure. The cost is
  accepted with open eyes and written into the file: a document carrying a field a NEWER schema understands
  is now refused by an older engine rather than read without it. Refusing to run is an answer somebody can
  act on; running something other than what was written is not.

- **A node parameter this catalog does not define stops the run.** It was a warning - "it is ignored" - on
  the reasoning that a document from a newer host may carry one and the run is still sound. The run is not
  sound: the node cannot honour the parameter under any version, so it runs on a default nobody chose, and a
  misspelled `riskPercent` is a position sized by something its author never wrote. Ruled by a host built
  on this engine,
  whose case was the part this engine cannot see: a builder writes these from the catalog, so an unknown
  name is unreachable through the interface and none of its thirty shipped templates produce one; documents
  there are often written by a model, which receives the findings and can correct a Block on its next turn
  while having no way to notice a warning; and its canvas pins only Blocks to a node, so as a warning this
  was a line in a list with nothing marking the node it belonged to. The message names the **catalog**, the
  node and the parameter, because somebody holding an older document needs to know which key to delete and
  that there is nothing to hunt for. Accepted cost: a saved document whose parameter was renamed in a later
  catalog goes from running with one setting ignored to being refused.

- **A risk test was asserting its rule against a stop that was never applied.** Found by the change above on
  its first run, in the engine's own suite. `risk.exit` declares `stop` as an anchor and an offset; the
  fixture wrote `stop: { unit, value }`, which are `target`'s field names - so the document set no stop, the
  node fell back to its default, and a test named for the relationship between the stop and the target was
  checking it against a stop nobody wrote. It passed for four releases.

### Added

- **A node says what its indicators were doing before it started, and what its numbers mean.** Three things a
  person needs to check an engine's arithmetic by eye, which is the only way to know a condition is waiting on
  what it should be. **The warm-up now hands over what it computed:** a node warms its indicators from history,
  those per-bar values existed inside the runtime and were thrown away, so a line could only begin where the node
  began - on a five-minute bar, an hour of nothing to look at. Recomputing them outside the engine would prove
  nothing, because a second implementation cannot verify the first: if the two agreed it would say nothing and if
  they disagreed nobody could say which was right. Bounded by the warm-up itself, so it is not a rolling history.
  **Every indicator output declares its unit** - `price` shares the candles' axis, `percent` carries its range
  where it has one, `ratio` and `unbounded` belong in their own pane - so nothing needs a list of node types of
  its own, the kind that goes stale the moment an indicator is added and is noticed only when a line is drawn on
  the wrong scale. A test walks the catalog and fails if an output does not say. ATR is declared unbounded rather
  than a price on purpose: it is a distance, not a level. **And the monitor frame carries every node's value**,
  not only the conditions', so a node watched over the control channel is told what a run in the same process
  already knew.

- **`data.position` says how long ago it got out, in seconds.** It already published the last exit's price and
  how many bars ago it happened, both readable while flat. Bars-ago answers "how long since I got out" only
  while every bar arrives: it counts evaluations, so a gap in the data or a frame started by a tick makes it
  mean something other than elapsed time. `lastExitSecondsAgo` counts time instead, measured from the venue's
  own closing timestamp when it gives one rather than from the moment the node heard about it, read against the
  clock and not against the last bar to close. It survives a restart with the rest of the node's state and is
  floored at zero, so a state restored from a run that ended later cannot report an exit in the future. A
  `Series`, deliberately: there is no time `ValueKind` and adding one would change the node catalog contract for
  a value every condition uses as a duration anyway. Pinned by asserting it equals bars-ago times sixty on
  gapless minute bars, which fails if the unit is wrong - confirmed by publishing nanoseconds and watching it go
  red.

- **A file is bounded, so a streaming read is bounded.** `StreamAsync` opens one file at a time, but every row
  in that file is materialised before the first is yielded - so while one write produced one file of any size,
  "stream it" was not an answer. It is measurable rather than theoretical: 25-level depth costs about 3,011
  bytes a snapshot, so an instrument-day is 1.53 million snapshots and **4.3 GiB in a single file that nothing
  could open**. A write now fills a file to `MarketArchive.MaxRowsPerFile` and starts another, each named
  for the range actually in it, so the window filter keeps skipping files a read does not need. Rows are also
  mapped a file at a time, so the row objects for a whole write are no longer all alive together. It bounds a
  file and not a write: handing the catalog a day in one call still builds that day first, which is the
  caller's to limit. Consolidation obeys the same bound - it reduces the file **count** to the fewest the bound
  allows rather than to one, which would have undone the whole thing on the next `catalog consolidate`.

- **Order-book depth without a paid key.** Tardis serves the first day of every month with no
  authentication, and `book_snapshot_25` is real 25-level depth - about 89 MB gzipped for an
  instrument-day, 1.5 million rows of 104 columns. It is the only real L2 reachable without a
  subscription: the free archives the venues publish themselves carry top-of-book or a band metric, so
  producing levels from either would mean inventing prices. `LoadBookSnapshotsAsync` reads it into one
  `OrderBookDepth` per row, flagged as a snapshot, **with a limit** - a day is 1.5 million rows, and the
  deltas loader beside it takes no limit, which is why `incremental_book_L2` at 700 MB cannot be used for
  this at any setting. A level the file left empty ends that side's ladder rather than becoming a zero:
  these rows are padded to full width, so a book eleven deep carries fourteen empty pairs, and reading
  them as zeros would put a bid at zero where a fill would find it. What this buys is the one thing the
  matcher's book walking could not be shown to do: a fill binding to the levels **below** the best bid
  and ask. Consumption needed nothing - `OrderBookDepth` already carried arbitrary-length ladders,
  `OrderBook.Apply` was already multi-level, and the backtest engine already routed depth.

- **The catalog stores order-book depth, so a run reads it once instead of fetching it every time.**
  `grep Depth` over `Bytex.Data` returned nothing: the matcher has walked a book since 0.6 and there was
  nowhere to keep one, so every execution went back to the vendor for the same instrument-day and re-paid
  a rate limit per sweep - which also made a backtest depend on the network. `WriteOrderBookDepthAsync`,
  `OrderBookDepthAsync` and `StreamOrderBookDepthAsync` sit beside the other kinds under a `book_depth`
  directory, with the same windowing, streaming, listing and consolidation.

  **One row per snapshot, each carrying its whole ladder.** A day of 25-level snapshots for one instrument
  is about 1.5 million of them; normalised to a row per level it is 76 million, the same explosion that
  makes a venue's incremental feed unusable for a run.

  **And the two sides are list columns, so nothing in the schema knows a level count.** Twenty-five is a
  property of one vendor's dataset, not of depth, and a fixed-width schema would have to put a value in
  every column a short ladder does not fill - where a zero is a price a fill can reach. A snapshot written
  at N levels reads back at N, which is what carries the loader's truncation rule through storage instead
  of leaving it true only in memory. Both halves are held by tests that were checked against the
  mutations: padding the read to twenty-five fails seven of them.

- **A node's state can live in PostgreSQL (R6.7).** `Bytex.Persistence.Postgres` holds what
  `Bytex.Persistence.Redis` holds, under exactly the same keys, so a deployment that does not want to run
  Redis does not have to. Four tables - a string table, a set table, a list table and a hash table - created
  on first use and never dropped, because a node starting against a database holding the state it is about
  to reload must not have it taken away. Four and not one: a single table of keys and blobs would mean
  writing the shape into the value and working it out again on every read, where a table per shape puts each
  promise in the schema. Every read of a list orders by a sequence that only increases, because an order is
  rebuilt by replaying its events in the order they were appended and PostgreSQL promises no ordering
  without `ORDER BY` - a query missing one rebuilds an order with its acceptance after its fill. Every key
  carries the moduleHost id, so two nodes share a database without either loading or flushing the other's rows.
  It holds runtimeModule and strategy state as Redis does, because the layer above is the same one.

- **The key layout is one class now, shared by every backing store.** What decides whether a resumed node is
  the node that stopped - the keys, the index sets that make a reload possible, the rebuilding of orders from
  their events and of order lists from ids - was Redis's own class. It is now shared as SOURCE, compiled into
  each persistence library: the types are internal, so a public one of the same name cannot collide in two
  assemblies, and a deployment wanting nothing to do with Redis does not acquire its client to get them.
  Copying the class instead would have been the cheaper hour and the wrong one - an order-list rebuild right
  in one copy and stale in the other is a bracket missing a leg on exactly one of the two stores, which no
  test would think to compare. `Bytex.Persistence.Redis` keeps every public member it had, including
  `EventSerializer`, which now forwards to the shared implementation.

- **Every backing store is held to the same promises.** `BackingStoreConformanceTests` asks each of them,
  through `ICacheDatabase`, for the things a store has to mean: an order rebuilt from its events in the order
  they happened, an index that does not hold one id twice, runtimeModule state where writing some fields leaves the
  rest, a flush that takes every kind of key, and two moduleHosts on one database reading and flushing none of
  each other's state - the last being the shape of a real defect in a comparable engine. Run against the
  in-memory double, against a real Redis, and against a real PostgreSQL; the server halves are skipped with
  a reason rather than passed where there is no server, and `BYTEX_TEST_POSTGRES` points them at one.

- **The last bar of a window is checked against the venues (R12.10).** The live suite asks each venue for a
  window anchored on whole hours and holds the newest bar to being the one that opens at the end of it -
  the check that would have caught the above without anybody counting by hand. It asserts the last bar
  rather than the count on purpose: a venue publishing no candle for a quiet interior minute is a market
  being quiet, not a bound being wrong. Three offline tests pin the same rule on Bitget, so nudging the
  bound breaks them.

### Changed

- **Four records in `Bytex.Documents` gained positional members, which changes their shape.** `PortSpec` carries a
  unit and an optional range; `FrameSnapshot` and `DocumentMonitorFrame` carry every node's value; and
  `DocumentMonitorView` carries the warm-up series. **This is a breaking change to those constructors and their
  deconstructors** - permitted before 1.0, where a minor may change a public API, and said plainly here rather
  than left for somebody to discover at a compiler error. Code that constructs a `PortSpec` positionally with the
  old five arguments still compiles, because the new members have defaults; code that deconstructs any of the four
  does not. The node catalog's own version is unchanged at 1.0: the export only gained fields, so anything reading
  the JSON is unaffected.

- **Two guards learned what a library is.** The surface guard and the packaging guard enumerated every
  directory under `src/` and demanded an api file and a package description of each. The shared key layout
  lives there as source and produces no assembly, so both now key on a directory holding a project. A new
  library with a csproj still fails them until it is written down - which is how
  `api/Bytex.Persistence.Postgres.txt` came to exist.

### Documentation

- **The two handlers that carry the same bars now say so.** `RuntimeModule.OnBar` takes a subscription's bars;
  `OnHistoricalData` takes a request's answer, item by item, before `OnDataResponse` takes the whole response.
  Both were bare `protected virtual` methods with no documentation at all, neither mentioning the other - so
  overriding one and not the other leaves a request's answer silently unread, which looks exactly like a venue
  that returned nothing. Found the hard way by somebody who overrode `OnBar`, watched two hundred requested
  candles arrive and vanish, and had to read the dispatch to see why. `OnDataResponse` now also states that a
  FAILED request arrives there and nowhere else, so an runtimeModule that ignores it cannot tell a refusal from an
  empty answer. Worth knowing alongside: registered indicators are updated from historical items by the
  dispatch itself, so an runtimeModule that only wants them warmed overrides neither - which is why the gap survived,
  since the commonest use of a warm-up works untouched.

- **Enums are open, and the policy now says so.** A public enum here is extensible - a minor may add a
  member, because new instrument classes and asset classes arrive as venues and asset classes do, and
  holding them for a major would mean a major for every venue that quotes something new. What that costs
  anybody building on the engine was undocumented: the freeze test sees an added member as an addition and
  passes, while a `switch` without a `default` arm breaks in their repository. `versioning.md` now states
  that only the existing members are frozen, not the set, and that an unknown value must be treated as
  unknown.

- **Where a spread's legs may appear is decided before the freeze, not after** ([design note 0013](docs/design/0013-multi-leg-instruments.md)).
  Option and futures spreads come after 1.0, and whether they are an addition or a breaking change depends
  on one thing: if legs have to be visible to `Order` or `Position`, the shapes that must move are the ones
  1.0 holds still. So a spread is an instrument in its own right with its own id, its legs described on the
  instrument for reference, margin and reporting, and never visible to an order or a position - which is
  also how a venue presents one. **Nothing is built for it now**, and nothing needs to be: if a venue does
  turn out to require visible legs - Interactive Brokers is the likely one, where a combo is defined by the
  legs in the request and executions come back per leg - they can still arrive additively, and the note
  pins the narrow route that keeps it so. `Legs` as a property in the record BODY of `OrderInitialized` and
  `OrderFilled`, never as a positional parameter: both are positional records, so the surface carries a
  `.ctor` and a `Deconstruct` for each and a new parameter changes both, while a body property changes
  neither. An order keeps naming one instrument and a position keeps one key; that invariant is the whole
  decision.

- **R12.10 is delivered, and it was never testnets.** The requirements catalog still carried it at
  Roadmap tier, and `docs/roadmap.md` and the README still listed "opt-in testnet tests" as work left
  for 1.0, a release after it shipped. What shipped in 0.9.1 checks the recordings against the venues'
  own live endpoints - public data only, no key and no orders, because an automated suite that sends
  orders to a venue is a different decision from one that reads it. All three documents now say that,
  and 1.0 is left with what it really has left: verification against live venues and the API freeze.

## [0.9.1] - 2026-09-28

Fixes. Every one of these was a request the engine accepted and quietly did not honour, or a figure
the documentation promised and no file carried - the class of defect that leaves a result looking
exactly like a result.

### Fixed

- **A setting this engine does not know is refused rather than skipped.** `"testnet": true` was read
  by nothing, so a node started, said nothing, and traded the real venue with real money. Every
  configuration a person writes - a node, a backtest, a batch, a search, and each client's own
  settings inside them - is now read strictly, and the refusal names the member, the type that does
  not have it and the file it came from. What a venue sends is still read leniently: an exchange
  adding a field to its own response must not stop a running node. There is no `testnet` setting
  anywhere except on Hyperliquid's client, where the address decides which network a signature is
  for; a test network is reached with `baseUrlHttp` and `baseUrlWs`.

- **An order sized in the quote currency is refused where the venue cannot express it, instead of being sent
  as a base size.** "Buy 100 USDT of BTC" left as "buy 100 BTC" - about twenty-five thousand times the
  intended order - on Binance anything but a spot market order, Bybit anything but a spot market order, and
  KuCoin anything but a market order. Each of those three has a field that means quote and takes it for one
  order shape only; the number was sent regardless of shape. A venue usually refuses the result for want of
  balance, which is luck rather than safety: on an instrument priced below one it FILLS at the wrong size -
  "100 USDT of DOGE" at 0.10 buys ten dollars' worth - and nothing afterwards says the size was not the one
  asked for. Nothing upstream catches it either, because the risk engine reads the quantity as quote and
  approves the size that was meant while the adapter sends another. Refused by name now, as Gate,
  Hyperliquid, Kraken, Bitget futures and KuCoin futures already did; the order shapes that fit are
  unchanged and still carry it.

- **A leverage the venue will not grant refuses the run on every venue that publishes a ceiling.**
  OKX, Bitget, Gate and Hyperliquid each read the ceiling their venue publishes and filed it in
  `Info` without putting it on the instrument, so the check that reads it skipped them and an
  over-limit configuration was accepted in silence - the venue then grants what it will and trades
  on, and the strategy runs at a size it was never tested at. Hyperliquid additionally applied the
  figure to the assets that granted it and left the rest on whatever they had, with a warning.

- **`add-strategy` works on a node `bytex run` started.** Adding, starting, stopping and removing a
  strategy while a node runs shipped in 0.9.0 and the first of them was refused on every node this
  tool started: the engine leaves the meaning of a path to the host and nothing here supplied one. A
  path is a file holding one strategy definition - the same object as an entry of `strategies`.

- **Custom statistics reach `result.json`.** They reached the printed summary and a batch's rows and
  not the file, which is the reading a program does, while the changelog said they were there. Nulls
  are kept: "asked and unanswerable" is not "never asked". The guard that should have caught it only
  checked a result's top-level fields, and now holds each nested projection to its own type.

- **`catalog info` says how many rows.** The documented list of what it reports always included it
  and the entry it printed from carried no count. Counting reads each file's parquet footer, so it
  is asked for: `Entries()` lists as before and `Entries(true)` counts.

- **Hyperliquid reads the network from the host rather than the whole URL.** A `baseUrlHttp`
  differing by a trailing slash, a port or a path signed for the test network while talking to
  mainnet, and a wrong network letter is refused by both - so every order came back rejected with
  nothing connecting it to a URL somebody had written the obvious way. An unrecognised host still
  signs for the test network, says so, and can be overruled with `testnet: false`.

- **A Binance status query for one order names it by the id the venue was given.** Five of the six
  sites that put a client order id on the wire went through the broker prefix; asking about a single
  order did not, so with a broker id configured the venue answered "no such order" for an order that
  was working.

- **A trailing offset Binance cannot express is refused rather than sent as the wrong unit.** This
  venue trails by a percentage; an offset given as a price was sent as one, clamped to the venue's
  ceiling and accepted, so the order trailed by something nobody asked for. Basis points convert
  exactly and still go through.

- **Hyperliquid says what its history boundary costs.** It serves about the last 5,000 bars of an
  interval counted from now, and an older window comes back as an empty array rather than an error.
  The method's own documentation said this was reported; nothing reported it.

- **Bitget and Gate set leverage on the instruments a run names, not on the venue's whole list.**
  Hundreds of signed requests at start-up, past their own rate budgets, for contracts a run will
  never touch - and the ones the venue refused left the leverage applied to some contracts and not
  others. A set larger than what a venue answers in one window is refused outright rather than
  half-sent.

### Added

- **A venue's listing, as names rather than instruments.** `IInstrumentProvider.ListAsync` answers what a
  venue calls its markets without building instruments, for a host resolving a name - "the perpetual on
  ETH against USDT, whatever this venue calls it". On the venues that enrich a listing afterwards that
  is the whole cost: OKX asks position tiers five families at a time, so a swap family of 477 contracts
  is ~96 requests after the one that lists them, and a name lookup discards every answer. Measured by
  such a host: 2.4 s to load one known instrument against 40.8 s to find the same one by its pair.
  An `InstrumentListing` is deliberately not an `Instrument` and a listing publishes nothing - an
  instrument missing its margin requirement can be traded, and a position sized against no venue
  requirement looks exactly like a position. OKX and Bitget answer it in one request; every other
  provider falls back to loading and describing what it loaded, which is correct and unchanged.

- **Everything a simulated venue models can be stated in a run's description.** Seven settings had no member
  there at all - fill sizing, the bar volume share, the book type, order amends, contingent orders, the fee
  model and venue behaviours - so anybody not writing C# could not configure them and was not told why.
  `feeModel` names a kind and its figures the way `marginModel` already did, and a venue **behaviour** is
  named the way a document names an indicator: `ISimulationModuleFactory` turns a name into a module, and the
  engine ships one for `rolloverInterest` (R8.19), which until now could only be constructed in C# - the one
  behaviour the engine has could not be switched on from a configuration. A name nothing can build is refused
  rather than skipped, and a behaviour missing a figure is refused naming it. A test now holds the two types
  to each other in both directions, which is what `liquidate` slipped through for a release.

- **A host can ask Tardis which dataset a market is published as.** `TardisDataClientConfig.DatasetFor`
  answers the two-step lookup - `VENUE:CLASS`, then `VENUE:CLASS:INVERSE` first for a coin-settled contract -
  that hosts were repeating against the map themselves. Two copies of one rule, and the copy is the one that
  goes stale the day a venue is added. Null means this vendor is not set up for that market, which the client
  still turns into its own refusal naming what is served.

- **The recordings are checked against the venues (R12.10).** Every adapter test runs against payloads
  recorded from the live venue, with no key and no network - which cannot notice a venue renaming a field,
  dropping one, or sending a number as a string: the offline suite goes on passing against the photograph
  while the adapter is broken. An opt-in set now runs the same providers against the real venues and holds
  them to the same assertions, read-only and without credentials, skipped with a reason unless
  `BYTEX_LIVE_VENUE_TESTS` is set. Run once per release. Its first run found a rate limit this engine was
  exceeding on OKX's position-tier endpoint, which no stub can show.

### Changed

- **OKX asks for position tiers several at a time.** Loading its swap family took about fifty seconds
  and almost none of it was the rate limit: the pages were sent one after another, each waiting for the
  last, where the venue's published budget carries ten at once. Bitget has fanned these out since it
  shipped, for a tier endpoint that is worse off. Same request count, same limit, bounded by the budget
  rather than by the round trip.

### Documentation

- `cli.md` covers `catalog info`, `catalog check`, `catalog consolidate`, `version`, the headerless
  CSV options (`--no-header`, `--columns`, `--separator`) and the control channel's strategy
  commands, and states how a configuration file is read.
- Tardis's `exchangeMap` example uses the per-family key shape (`VENUE:CLASS`, and
  `VENUE:CLASS:INVERSE`); the documented `-PERP` keys had stopped matching anything.
- Binance, Gate and Hyperliquid say there is no `testnet` setting, and Hyperliquid's own section
  describes how the network is decided and how to overrule it.
- Bitget, Gate and Hyperliquid state what a configured leverage is checked against and what it
  reaches.

## [0.9.0] - 2026-09-27

0.8 and 0.9 shipped together: everything in both milestones was finished before either was cut, so the
resumption and large-data work is in this release beside markets and state.

### Added

- **A node that is killed comes back the same as one that was never interrupted.** What a
  strategy knows - its phase, a ladder's progress, a trailing anchor - reached the disk only when
  the node was asked to stop, because `SaveState` had a single call site inside `StopAsync`. A
  container stopping, a service manager, a crash, a power cut, or a host killing the process after
  its own shutdown timeout all brought the strategy back empty while reconciliation handed it back
  the position it had been holding. `NodeStoreConfig.SaveInterval` now saves while the node runs,
  defaulting to thirty seconds, collected on the tradingRuntime loop and written off it so a slow disk
  cannot stall trading. SIGTERM is handled as the ordinary stop, so a container or service manager
  gets the save and the venue disconnect that Ctrl+C already got.

- **Repeating timers keep their phase across a restart.** A component re-registers its timers as
  it starts and an interval anchored to "now", so a four-hourly rule on a node restarted at 03:59
  next ran at 07:59 - and drifted again at every restart. The schedule now survives, and firings
  that fell while the node was down are **not** replayed: running a four-hour rule hours late runs
  it against a market that has moved, so the clock lands on the first boundary still ahead and
  reports how many it passed over. A component whose interval changed is asking for a different
  schedule and anchors to now, as before.

- **A backtest can run over more data than memory holds.** The catalog read a period by loading it,
  so a year of ticks had to fit in memory before the first of them was dispatched, and a machine
  that could not hold the period could not run the backtest at all. The catalog now also streams -
  `StreamQuoteTicksAsync`, `StreamTradeTicksAsync`, `StreamBarsAsync`, and the same for book deltas
  and funding rates - and `BacktestRunConfig.ChunkSize` runs a backtest in chunks, merging the
  streams of every data set by timestamp so what reaches the engine is in the same order it would
  have been in loaded whole. The engine releases each chunk once it has dispatched it, and refuses
  data older than what it has already dispatched rather than quietly running a backtest in the
  wrong order: a streaming run that bounded nothing would have been the same run with a longer
  name.

- **What the catalog holds, what is wrong with it, and how to put it right.** `bytex catalog info`
  reports what is stored per data set - the period covered, how many rows, how many files, how much
  disk - because the only way to learn any of it was to read the file names. `bytex catalog check`
  reports what would stop a streaming read: records out of order across files, a period held twice,
  a file that cannot be read. `bytex catalog consolidate` rewrites a data set as one ordered file
  and drops records held twice, which is what repeated overlapping downloads leave behind. It
  writes the new file before deleting anything, so an interrupted consolidation costs a temporary
  file rather than the data.

- **Databento historical data.** Trades, top-of-book quotes and bars from a vendor rather than a
  venue, so there is no execution client and nothing to trade on it. Read as CSV rather than as the
  vendor's binary format on purpose: a decoder inferred field by field that is subtly wrong reads a
  plausible price out of the wrong bytes instead of failing, and every column this reads is a name
  the vendor publishes. Prices arrive fixed-point with every unit a billionth and are scaled on the
  way in - read without the scale, a price is wrong by nine orders of magnitude in the direction
  that looks like a cheap instrument rather than like an error. Only the four aggregations the
  vendor publishes are served; a five-minute bar is refused by name rather than aggregated here and
  handed back under the vendor's own.

- **The whole of a node's state in Redis, and its bus published for other programs to read.** The
  cache stored orders, positions, accounts, instruments and runtimeModule state; it now also stores order
  lists, and the currencies that the instruments and accounts it stores cannot be read back
  without. A bracket used to come back from a restart as three unrelated orders, which is worse than
  not coming back at all, because nothing afterwards can tell that cancelling one was meant to
  cancel the others. `RedisBusStream` publishes chosen bus topics to Redis streams, one stream per
  topic, each entry carrying the payload's type beside the payload so a reader never has to guess
  the shape from the topic. Nothing is published unless a topic is asked for, and a full queue drops
  its oldest messages and counts them rather than applying back-pressure: a dashboard falling behind
  must not slow down the orders it is watching. Both halves are tested against a real Redis as well
  as against an in-memory double; the server tests are skipped, not silently passed, where there is
  no server. See [Redis](docs/concepts/redis.md).

- **The data catalog can live on object storage.** It called `File` and `Directory` at fifty sites, so
  it could only ever be a directory on the machine reading it - which is the wrong shape for a team
  whose data is larger than any one machine's disk. The catalog now reads and writes through a store,
  and `Bytex.Persistence.S3` is one: anything speaking the S3 API serves it, named the same way a
  directory is. `new MarketArchive("s3://bucket/prefix")`, `--path s3://bucket/prefix`, or the
  same string in a backtest's data configuration - the layout, the file names and every guard over
  them are the catalog's rather than the file system's, so both work alike. Signing is written here
  rather than taken from a vendor SDK, because the whole of what a catalog needs is five requests and
  a vendor's client brings a dependency tree larger than this repository to serve them. What it costs
  is stated: Parquet is read from a footer at the end of the file, so reading one object means
  fetching the whole object, and a remote catalog is paid for per query. See
  [object storage](docs/concepts/object-storage.md).

- **An object appears only once it is whole**, on either store. A write goes under a working name and
  is put in place when it finishes, so an interrupted one leaves nothing rather than a truncated
  Parquet file under a name claiming a range it does not hold.

- **Strategies can be added, started, stopped and taken off a node while it runs.** A node had to be
  restarted to change what it ran, which means stopping every other strategy on it to change one.
  `NodeController` does the four, and the control channel carries them as five commands (protocol
  version 6), each answered with every strategy on the node, its state, and what it is holding. What
  it refuses is the substance of it: a strategy is added stopped, because a node that began trading
  one the moment it arrived would be trading on a decision nobody took; stopping does nothing to the
  orders and positions it was holding unless it is asked, because either default is wrong for
  somebody; and a strategy still holding orders or positions is not removed at all, because removing
  it takes away the only thing on the node that knows what they were for. A path crosses the channel
  and never a document - the node reads its own files, as it does with its keys.

- **Margin can be worked out by a model, and a tiered one ships.** The engine charged one rate times a
  notional for any size, and no large venue does: every one of them charges by tiers, more margin for a
  bigger position. A run using the flat rate therefore understates what a big position costs, which is
  the dangerous direction - it sizes positions it could not have afforded and reports a return nobody
  could have had. `IMarginModel` is the rule, `RateMarginModel` is what every result so far used, and
  `TieredMarginModel` is a table by notional, validated when it is built rather than when it is read. On
  a backtest the model belongs to the venue and the venue teaches it to the engine, the way the leverage
  it grants already reaches the engine, so a run cannot size positions by one rule and liquidate them by
  another - the venue, the pre-trade check and the margin balance a report shows all use the one model.
  Every leverage row in a result now says which model worked it out, because with tiers a single
  leverage number is the best case rather than the whole truth.

- **A run can report figures of its own.** The built-in statistics are a judgement about what matters,
  and whose money it is decides that: a firm judging by Calmar, by turnover or by a measure of its own
  had to recompute it outside the engine from the tables, and a figure computed twice from two readings
  of one run is how two numbers in one report come to disagree. `IPerformanceStatistic` is asked once
  per settlement currency and handed what the engine already worked out, so it builds on those numbers
  rather than repeating them. What it answers reaches the result, the printed summary, `result.json` and
  a batch's rows, and a guided search can be pointed at one by name. A figure with nothing to say is
  reported as absent rather than as zero, because "not measurable" and "measured, and zero" are
  different facts - and a search reading "no answer" as zero would rank an unmeasurable run above every
  losing one. A statistic that throws costs its own figure and not the run.

- **What a position looked like at a moment is kept rather than recomputed.** A position's fills are
  kept, so everything realised can be worked out again; what cannot is what it was worth while it was
  open, because that needs the price at that moment. `Cache.SnapshotPosition` keeps one - the mark it
  was measured against, the unrealised profit at it, the realised figures and the commissions - and the
  engine takes one itself when a position closes, at the fill that closed it. A position with no price
  anywhere is kept without an unrealised figure rather than with a zero.

- **Eight more indicators**: Ichimoku, Supertrend, parabolic SAR, CCI, MFI, Chande momentum, linear
  regression and book imbalance. Seven are available to a strategy document as nodes as well as in code.
  Two of them carry a decision rather than only arithmetic. Ichimoku keeps what it has just computed
  apart from the cloud over the current bar - two of its lines are plotted 26 bars forward, so "the
  cloud" means two different things and a strategy asking whether price is above it means the second -
  and it is not ready until that cloud exists rather than only until its longest line fills. The book
  imbalance answers a share between -1 and 1 rather than a size, because the difference between two
  sides is not comparable between one instrument and another; an empty book reads as not ready rather
  than as balanced. Four of the eight refuse input they cannot measure - a money flow index needs the
  volume a quote does not carry, a book imbalance needs resting size rather than a trade that has gone -
  because an indicator that accepted it would read as ready and mean nothing. An indicator that reads
  the book is registered through its own contract, so one that cannot read a book cannot be registered
  for one.

- **Bars that close on how one-sided the trading was.** Three imbalance kinds were named in the model and
  refused by the engine, which is the worst of the three possible states: a caller could write the bar
  type down and asking for it threw. They are built now, with runs bars beside them - six kinds in all.
  An imbalance is signed, so a market worked evenly in both directions never closes one however much of
  it there is; a run counts each side alone, so the same market closes them steadily. An update is signed
  by the venue's own word where there is one, because a trade carries the side that took the liquidity,
  and by the tick rule where there is not. An update is never split across two bars: half a size is a
  size, and half a signed trade is not. The threshold is the one the bar type states rather than an
  estimate of it, which is a deliberate departure from the published form - an estimated threshold makes
  a bar depend on the estimator's parameters and on everything it has seen, so the same data would
  produce different bars in two runs, and nothing in a bar type could carry those parameters. The
  documentation says so, and says an adaptive threshold is a strategy's to compute.

- **A run can say which assumption it made about the inside of a bar.** A bar says what the high and the
  low were and not which came first, so an engine reading one is always assuming something the data
  cannot settle - and until now there was one assumption and no way to measure what it was worth.
  `HighFirst` and `LowFirst` state it outright: run a backtest both ways and the difference between the
  results is the honest size of the uncertainty, which is the only answer there is to "did my stop or my
  target come first". `TickSizePath` walks the range in the instrument's own increment instead of jumping
  between four prices, so an order resting inside a bar is reached at the price it would really have
  traded at. A bar whose walk would exceed the venue's bound is jumped and COUNTED, and the count is in
  the result: a run where some bars were walked and others were not is a run whose fills mean two
  different things. Every run now reports which of these it used, so a reader can tell a run that chose
  from one that took the default.

- **A DAY order expires at its venue's session end rather than at midnight.** A session is not a calendar
  day on most venues: an index future's session ends in the evening and the next begins minutes later. The
  engine treated the UTC date as the session, which expired an evening order hours early - a fill it
  allowed in those hours could not have happened - and kept a morning one hours late. A venue now states
  when its day ends, an instant at or after that time belongs to the next session, and a venue that never
  closes keeps the old behaviour, which is what a crypto venue does with a DAY order.

- **The public surface is written down, and a test holds it to that.** An API freeze that is a sentence in
  a readme is a promise nobody can keep. Every public and protected member of every packable library is now
  in `api/`, one file per library, read from the built assemblies on every test run. Before 1.0 a change to
  an API fails until the surface file is updated in the same commit, and the failure prints what went and
  what arrived - which is what a changelog entry has to describe. From 1.0 the same test is the freeze.
  `docs/versioning.md` says what a version number promises, what counts as public API, which four contracts
  carry their own version numbers, and what is deliberately not frozen.

  It also says how long a version is supported: while the line is 0.x, the latest minor and nothing
  back-ported; from 1.0, ordinary fixes for the current major and security fixes for the previous one until
  six months after its successor shipped. Six months is a duration rather than a number of releases because
  what somebody planning an upgrade needs is a date they can put in a calendar. A security fix that cannot
  be made without a breaking change says so in the advisory rather than shipping a patch that breaks the
  build of everybody who applies it, and a correctness fix that changes what a backtest produces is never
  back-ported - people on an older major are often there precisely because they want the numbers they
  already have. `SECURITY.md` points at that policy instead of carrying a second copy of it.

- **Published benchmarks.** `bench/Bytex.Benchmarks` measures what the engine costs per event - a message
  on the bus, a tick into a bar, a delta into a book, a bar through an indicator, a price rounded to a
  venue's increment - with the numbers and how to reproduce them in `docs/benchmarks.md`. Per event rather
  than per backtest on purpose: a number for a whole run says more about the data it was given than about
  the engine.

- **One page that lists the extension points.** Strategies, runtimeModules, adapters, execution algorithms,
  indicators, simulation modules, margin models and performance statistics were each documented where they
  live, and nowhere together: `docs/extending.md` is the table, with the three rules that apply to all of
  them.

- **A plugin can bring node types a document can use.** The runtime contract for a node type has been
  public since 0.2, and there was no way for a plugin to supply one: a host had to build a catalog
  itself, which the tool nobody replaces did not do, so the contract was real and unreachable. A plugin
  now implements `INodeTypeProvider` beside `IPlugin`, declaring one prefix and yielding a descriptor per
  type, and the host composes one catalog from every provider it loaded. `--plugins` does it for every command
  that reads a document, so `documents validate`, `documents catalog` and `documents schema` see the same types
  a run does: a `validate` that refused what `backtest` then ran would be worse than not supporting plugin
  nodes at all. Three rules keep a document meaning the same thing on every machine, all of them failing
  at composition rather than on the first bar: a type begins with its provider's prefix, a plugin cannot
  claim one of the engine's own, and two plugins cannot claim the same one. A plugin that could redefine
  `act.order` would change what every document that ever placed a market order means, on the machines
  that loaded it and only there. A document says what it needs under `requires`, so a host without the
  plugin refuses it with *needs Acme Nodes (acme) 1.2 or later, which is not loaded* rather than with the
  name of a type nobody recognises, and a builder that cannot draw the node keeps the block when it saves
  instead of dropping what the document needs. `examples/Bytex.Examples.Nodes` is the worked example - a
  stateless node, a stateful one with `SaveState`/`LoadState`, and the document that uses them, all three
  run by the test suite rather than only shipped, because a latch that forgets it has fired fires again on
  the first bar after a restart and the strategy acts twice.

- **A run as one page.** The report set was seven CSVs, a text summary and a JSON document - everything a program
  reads, and nothing for the person deciding whether a run is worth keeping, who was left opening spreadsheets and
  plotting the equity curve by hand. `tearsheet.html` is now written beside them: the headline figures per currency,
  the equity curve with how far below its running peak the account went, a bar per closed trade, the trade statistics
  and the tables in brief. One file with nothing fetched from anywhere - no script, no stylesheet, no font, no image,
  and no new dependency behind it; the charts are SVG written from the result's own points. A report that needs the
  network renders differently next year, renders as nothing on a machine with no route out, and tells whoever serves
  that route which strategies somebody is looking at. It carries what the numbers account for as well as what they
  came to - the capabilities modelled, the ones actually applied and the gap between them, where each margin
  requirement came from, the leverage asked for beside the leverage granted, what bounding fills came to - and a
  section with nothing in it says so, because "no funding was charged" and "funding was never modelled" are different
  facts and an absence cannot tell them apart. The same result renders the same page, byte for byte.

- **A document can be evaluated on ticks, and everything that counts bars still counts bars.** A strategy document
  was walked when a bar closed and at no other time, so every decision it made was made at a bar boundary: a level the
  market touched in the middle of a bar and came back from was never acted on. `evaluation` now takes `barClose` (the
  default, and byte-for-byte what every existing document does), `quote`, `trade` or `either`.

  A tick frame is not a second kind of graph - the same nodes are walked in the same order - and the three things that
  differ are the whole design. The bar on the frame is the last CLOSED one, so no node ever sees a partial bar;
  `data.price` is the new node that publishes the price the frame is about, which is the bar's close at a close and the
  tick's own price inside a bar. No indicator moves, because indicators are updated by bars: a tick frame compares a
  live price against figures as of the last close, which is the comparison worth making. And the bar index does not
  advance, which is what keeps a time stop, a delay, bars held and cancel-after-bars counting bars rather than ticks.

  `cond.cross` is the one built-in condition whose meaning depends on how often it is asked, and its `confirm`
  parameter stops being reserved: judged at the bar close it holds its answer while a bar is in progress, so a crossing
  that reverses before the close never fired; judged `instant` it fires the moment the lines meet.

  The cost is stated rather than discovered: the validator warns when a document asks for it - a liquid instrument is
  hundreds of frames a minute rather than one, and the run needs that tick data in it at all - and a run reports
  `BarFrames` beside `TickFrames`. One thing it deliberately does not buy: an intra-bar stop. `risk.exit` does not
  watch the price, it places a stop at the venue, and a simulated venue walks each bar's own path - so a strategy
  protected by a resting order already exits inside the bar, and a test pins that so nobody pays for tick frames to
  get what they already have.

- **A CSV an exchange actually hands out can be imported.** The loader honoured a quoted field and could read a
  file whose first line is already data, and `catalog import-csv` could ask for neither - so both existed for a host
  that references the library and for nobody using the tool they were built for. The command now takes `--no-header`
  (which requires `--columns`, because a file with no header has no names to match and guessing would only move the
  error to a column read from the wrong position, which parses into a plausible price and says nothing), `--columns`
  as `field=source` pairs by name or by position, and `--separator` as one character or `tab`. An import that read no
  rows says what usually causes that rather than reporting a cheerful "Imported 0", which is the same words as a file
  that really was empty.

### Fixed

- **A number the engine writes no longer depends on the machine that wrote it.** Every formatter and every parse
  in `src/` was already invariant except one, and nothing held it there: the repository builds with
  `InvariantGlobalization`, which switches off the whole `CA13xx` analyzer family, and makes the current culture in
  a test the invariant one - so no rule could warn anybody and no ordinary test could tell the difference. What was
  actually wrong was eleven strings that interpolated a bare `decimal` or `double`: an order's trailing offset, a
  position's average entry, why the risk engine denied an order, why a venue refused one, what a venue behaviour
  charged and why, a margin tier that cannot be right, the leverage a venue grants, and the size a contract venue
  cannot trade. `Price`, `Quantity`, `Money` and `UnixNanos` format themselves invariantly, which is why the wire,
  the CSVs, `result.json` and the catalog's file names were never at risk - but a venue behaviour's reason is a field
  of a report, and a denial reason is something a host stores and matches on. All of them now pass the provider the
  rest of the engine passes, and `int.TryParse` on a grid's level keys does too.

  The guard is a test rather than a rule, because a rule is not available: `CommaDecimalCulture` builds a culture that
  writes 1234.5 as "1.234,5" and negates with a "~", proves it took before handing back, and now guards the report
  writers, the tearsheet, the JSON a host parses, a document saved and validated, a whole run's decisions, an order on
  the wire, and the refusals above. The tool is a different case and says so for itself: `bytex` ships with
  `InvariantGlobalization`, so there are no cultures in it to get wrong, and a test reads that out of the runtime
  configuration beside the executable rather than trusting the property in the project file.

- **One unloaded plugin reported six blocking findings, of which two were true.** A node whose type this engine
  does not know was left out of the graph the validator then checked, so every edge that touched it was reported
  as *an edge that refers to a node that does not exist* - about a node the document plainly has - and the order
  node it fed was reported as having nothing wired to its trigger. Four findings sent somebody hunting for a typo
  in ids that were correct, and the two that named the missing plugin were buried among them. An edge to a node
  that is in the document is no longer dangling, and it still counts as a connection; an edge to an id that is
  genuinely absent is refused exactly as before.

- **The Databento adapter could not be reached from the command line at all.** Every other adapter's plugin
  is registered by the tool a user runs; this one shipped with a package, a guide, a key variable and a row
  in the integration index, and no registration - so `bytex` could not use the vendor by any means, while a
  host that referenced the package directly could. The CLI registers it now, and a list that every example
  configuration is checked against includes it, so the next adapter that ships unreachable fails a test
  rather than a user.

- **A bar was published for a window the engine had only partly watched.** A time aggregator
  floored the clock to the next boundary and published there, so one started at 10:03 published a
  bar stamped for 10:00-10:05 built from 10:03 onward - indistinguishable from a complete bar, and
  disagreeing with the same bar fetched from history or streamed by the venue. An indicator warms
  on it and a strategy's first decision is made on it. A bar is now published only if the
  aggregator watched the whole window it claims to cover; it costs one window at a start, a resume
  or a subscription, and the aggregator counts what it declined. Bars aggregated from ticks only:
  stored-bar backtests and venue-streamed bars are unaffected.

- **A venue publishing a negative precision took down its whole instrument list.** Kraken's
  futures market publishes `contractValueTradePrecision` signed, and contracts trading in
  multiples larger than one declare it negative. Cast to a byte, -3 became 253, `Quantity` refused
  it, and the exception escaped before the provider returned anything - so no Kraken perpetual or
  dated future could be listed, added or traded, while Kraken spot worked perfectly. Seven casts
  of the same shape across three adapters now read through `VenuePrecision.FromDeclaredPlaces`,
  which treats negative places as a larger step, holds anything past eighteen rather than
  throwing, and never throws: one strange contract must not cost a market its instruments.

- **A currency a venue defines came back from a restart with the wrong precision.** Cache
  persistence read currencies at startup and nothing ever wrote them. Most currencies here are
  built in, and an unknown code is not an error anywhere - `Currency.FromCode` invents one at the
  default precision of eight - so an instrument quoted in a six-place token was reloaded quoted in
  an eight-place currency of the same name, and every amount in it rounded two places too far out,
  silently, for as long as the node ran. Currencies are now written with the instruments and
  accounts that need them, before those records, and a corrected precision is written again rather
  than taken for one already stored.

- **Historical data was fetched from the wrong vendor dataset for two markets.** The Tardis
  exchange map was keyed by venue plus a boolean "is it a perpetual", and the vendor names a
  dataset per product family. A Bybit option was read from `bybit-spot` and a Binance
  coin-margined contract from the USD-margined `binance-futures`; both answer with nothing, which
  reads as the vendor holding no data rather than as the adapter asking wrongly. The map is now
  keyed by venue and family with every id taken from the vendor's own published list - including
  `okex` and `gate-io`, which no fallback could have guessed - and the fallback to a lower-cased
  venue name is gone: a market the map does not name is refused by name, listing what is served.

## [0.7.0] - 2026-09-26

### Added

- Two more Bybit families: INVERSE contracts and OPTIONS, alongside the spot
  and linear markets the adapter already carried. Selected the same way, by
  `productType: Inverse` or `productType: Option` on a client's configuration.

  Inverse contracts are the family every adapter in this repository had
  excluded, and the stated reason was that a USD-quoted, base-settled contract
  cannot be sized in base units without a price. It never needed to be: the
  venue sizes them in its own USD contracts, and the engine's money arithmetic
  inverts from one flag on the instrument - notional divides by the price and
  answers in the base currency, and margin, commission and funding all follow
  it. So they are published as the venue publishes them, 22 perpetuals and 4
  dated contracts, with the margin and the ceiling the venue states per symbol.

  Two facts about them cost more than the arithmetic. The dated contracts are
  spelled `BTCUSDZ26` - no dash, no date a reader could parse - so the linear
  family's rule for telling a perpetual from a future would have called all
  four of them perpetuals; the class comes from the venue's `contractType`
  field instead. And spot lists `BTCUSD` and `ETHUSD`, both trading, so an
  inverse perpetual named for its own symbol would share an instrument id with
  a spot pair and the family resolver would answer both with the pair. The
  suffix goes on the inverse symbols that end where their quote coin does,
  which is every perpetual and none of the dated contracts.

  Options are structurally unlike everything else here, and every difference
  was measured against the live venue rather than read from its documentation.
  They are charged no funding: the funding endpoint refuses the category. They
  have no bar history by any route: the kline endpoint refuses the category,
  and the kline socket topic is accepted, reported under `successTopics` and
  then silent. They publish no margin and no leverage ceiling: the risk-limit
  endpoint refuses the category and the contract data carries no leverage
  filter, so both are left unset and the ceiling reads as "the venue did not
  say" rather than as unlimited. Their strike is published nowhere but the
  symbol, so it is read from there and checked against the venue's own
  `baseCoin` and `optionsType` as it is read. Their book is published at depths
  25 and 100 rather than 50 and 200 - the other families' depths are accepted
  and never delivered. Their trades are published per underlying rather than
  per contract. Their quotes arrive on the tickers topic, under that market's
  own field names, because `orderbook.1` delivers nothing for them either.

  Four of those are declared as capabilities the family does NOT have, which is
  the point of declaring them: the venue would have accepted every one of those
  subscriptions, reported it as a success, and sent nothing.

  Two things about options could not be established without a key and are
  recorded rather than assumed. Whether the venue honours an amend on an option
  order: the amend path is signed, so no option order could be placed to amend.
  And what an option really costs - the venue charges a fraction of the
  underlying's index price capped at a share of the premium, while this engine
  prices a commission as a fraction of the traded notional, which for an option
  is the premium. The declared rate applied that way is a lower bound on the
  charge rather than the charge.

- Bybit asks about a whole market with the filter each of its families
  requires, rather than with the linear market's. A read of every order or
  position used to send `settleCoin=USDT` whatever was configured, which is
  right for one of the four: an inverse contract settles in its own base coin,
  so there is no single settle coin for that market, and an option market is
  asked for by underlying. The coins come from the contracts the client holds,
  one request each - which also means a client holding USDC linear contracts is
  now asked about them instead of only about its USDT ones.
- Binance's COIN-M (coin-margined) futures as a third family beside spot and
  USDⓈ-margined futures, selected with `accountType: coinMFutures`: data,
  execution, catalog, bar history and funding history on the venue's `dapi`
  host. Its contracts are quoted in USD, sized in a whole number of USD
  contracts, and margined and settled in the base coin - so they are inverse,
  and notional, margin, profit, commission and funding all come out in the coin
  and all go through one over the price. An instrument carries the venue's own
  `contractSize` as its multiplier, measured as 100 USD on every BTCUSD
  contract and 10 on the other 27, because a size left at one values a BTCUSD
  position at a hundredth of what it is and margin, commission and the
  liquidation price all follow it down.

  A third family and not a flag on the second: measured against the live venue,
  the two disagree about the host, the endpoint version of every account read
  (`/dapi/v1/balance` where the other is `/fapi/v2/balance`, and neither path
  exists under the other's version), the field a contract's tradability is
  published in (`contractStatus`, with no `status` at all - so a reader looking
  for the sibling's field took every pending and delivering contract for
  tradable), the fee schedule, and how many sockets its market data needs.
  Instrument ids keep the venue's own spelling - `BTCUSD_PERP` and
  `BTCUSD_261225` - because the venue has already marked which contract is
  which, and a second marking would have to be undone from an id that no longer
  says which family it came from. The instrument class comes from the venue's
  `contractType` field, never from the symbol.

  Its `exchangeInfo` ignores its own `symbol` filter, the same defect as the
  USD-margined family's and measured on this one rather than assumed from it:
  all 30 contracts come back for a symbol that exists, one that does not, and a
  pair. Its leverage brackets are behind the signed endpoint on its own host,
  so margin falls back to the venue-wide default and `maxLeverage` is null -
  "the venue did not say" rather than unlimited - and the leverage guard refuses
  a configured leverage above the ceiling wherever a key read one. Reachable
  from `catalog fetch-instruments --instrument-type CoinMFutures`, and
  `verify-keys` now asks whether a key with futures enabled really reaches that
  host, because one permission covers both futures markets on two addresses.

- A Hyperliquid adapter: perpetual futures, with data, execution, catalog and
  history. The fifth venue and the first whose credential is not a key pair the
  exchange issued - authentication is a wallet signature over EIP-712 typed
  data, so the credential is a secp256k1 private key that controls an address
  and the address is the account. The signing (Keccak-256, recoverable
  secp256k1 ECDSA, and the MessagePack encoding the venue re-derives the digest
  from) is written against the framework alone, with no new dependency, and was
  verified against the live venue without an account: signing with a key nobody
  has funded and reading back the address the venue recovered checks the whole
  pipeline, field order included, and it recovered the signing address for
  every action the adapter sends on both networks.

  Three of the venue's facts are unlike the others and are declared rather than
  discovered. Its margins come from the venue - `maxLeverage` per asset, with
  maintenance margin at half the initial margin, measured against three live
  positions to the last decimal place, rather than the flat 0.05/0.025 two
  other adapters carry. It has no market order at all, so one becomes an
  immediate-or-cancel limit through the book. And its candle read serves only
  about the last 5000 bars of an interval, counted from now and not from the
  window asked for, so there is a period of history it cannot be asked for at
  any page size - which the adapter says rather than returning quietly short.

- A node type declares whether it changes an order after placing it
  (`amendsOrders` on the node catalog), true for `act.modify`, `act.trail`,
  `act.moveStop` and `risk.exit`. A venue family already states whether it
  allows amendments, and that half alone is useless: validating a document
  against a venue needs to know which nodes in it would amend. The alternative
  was for every host to hard-code the engine's amending node types, in a list
  that goes stale the day another is added - silently, because a document full
  of amending nodes is valid until it meets the wrong venue, and what it costs
  there is a stop left at the size it was first placed at while the position
  grows past it. The flag is checked against the code rather than trusted: a
  node amends exactly when its implementation calls the amend API, and a test
  reads the catalog's source to say so.

- Venue behaviours a run can add, that the simulator does not know about
  (R8.19). The simulator models what venues do in general - it fills, charges
  commission, charges funding on a perpetual, liquidates an account out of
  margin. What it cannot hold is the long tail of things individual venues do
  that change a result, and building them in would mean carrying every venue's
  idiosyncrasies forever. A behaviour is now a module: it sees the venue's
  clock advance and what the account holds, and the only thing it can do is
  move money with a reason. It cannot fill, cancel or change a position, and a
  test fails if that surface is ever widened - a venue quirk that could rewrite
  fills would be the matching engine, in the one place the suite does not look
  for it.

- Interest on a position held across a venue's rollover, as the first such
  behaviour (R8.19). Separate from funding on purpose: funding is a perpetual's
  own mechanism, paid from one side to the other from rates the run was given,
  while this is the cost of the money behind a leveraged position - both sides
  normally pay it, it applies to instruments with no funding at all, and both
  can be charged on the same position on the same day. The two rates are given
  rather than one derived from the other, because a venue's convention is the
  caller's fact and a simulator that guessed it would produce a confident
  number nobody could check. A gap in the data is not a free ride: what is
  tracked is the last rollover charged, so a three-day jump between prints is
  charged three times rather than once.

- Every charge an added behaviour made is in the result, in `result.json` and
  in a `module_charges.csv`, with the behaviour's own account of what it was
  for. A reader who finds a result smaller than they expected can see that a
  behaviour took it rather than concluding the strategy lost it.

- A venue declares whether it pays a rebate on the trades routed to it, beside
  the mechanism that carries the id (`brokerProgramme` next to `brokerTag`).
  The two were one field, and reading "nothing carries an id here" as "this
  venue runs no programme" is a mistake with no symptom: orders are placed
  correctly, and the money simply is not collected. A venue may now say it runs
  none, that its programme is carried, that its mechanism is published and
  uncarried, or that its mechanism is published only to approved applicants -
  which are the same to an adapter and entirely different to whoever decides
  which programmes to join. The venue checklist enforces the new answer against
  the declaration, so neither can drift from the other.

### Changed

- A configured leverage is a decimal rather than a whole number. A strategy
  document carries leverage as a decimal and so does the simulated venue, so an
  integer on the live execution config forced whoever crossed into live to
  round - silently, in a direction they chose, making a live position a
  different size from the backtested one with nothing to say so. That was
  happening: 2.5 was being floored to 2. Venues that take the figure as written
  now receive it as written; Binance, whose own documentation gives the field as
  an integer, refuses a fraction when the client is built rather than rounding
  it, which is a sentence somebody can read instead of a position twenty percent
  smaller than the one they tested. A venue refusing a whole number it will not
  grant is still logged and left in the venue's own words, because only the
  venue knows that.

### Fixed

- The exported node catalog was missing `amendsOrders` for its first hour: the
  export is a hand-written projection, the same shape of defect as the report
  writer, so a field added to a node descriptor is invisible to every host until
  somebody remembers a line. Both now have a test that every field of the type
  reaches the file it is written to.

- Two members had been inserted between a doc comment and the member it
  documented, leaving one with two summary blocks and the other with none:
  `SimulatedVenueConfig.RefusesOrderAmends` against `DefaultLeverage`, and
  Binance's `Tradable()` against `ApplyLeverageAsync`.

- A run where a strategy faulted wrote a report that did not say so anywhere a
  program could read. `faultedStrategies` is on the result and was never listed
  in the JSON projection that writes the file, so the numbers in `result.json`
  were simply those of however much of the run happened before the strategy
  stopped trading, with nothing to indicate it. The projection is hand-listed,
  which is why this was possible at all, so there is now a test that every
  field of a result reaches the file - a new report field can no longer be
  silently dropped.

- KuCoin was declared as having no broker programme, in the adapter, in a test
  and in the release checklist. It runs two broker tiers with rebate
  management. Its mechanism is a signed partner credential on every REST
  request - an id, a broker name, and a signature over the timestamp, the id
  and the API key made with a second secret the programme issues - which a
  single configured id cannot express, so nothing carries it and looking at
  `brokerTag` was never going to show that. Now declared as a rebate going
  unclaimed, with the mechanism written down.

- A broker id and a leverage are now tested through the path a host configures
  them by. Both were only ever exercised with the configuration built in code,
  and a host does not build it in code - it hands a trading node a client entry
  whose config is JSON. A field that works in code but arrives under a name
  nothing writes is unreachable, and neither of these would have reported it:
  an untagged order is a valid order that pays nobody, and an unlevered
  position is a valid position of the wrong size.

- Loading a single instrument now loads that instrument, on every venue and
  every family. Binance's USD-margined futures ignores the symbol filter and
  answers with its entire contract list, so asking for one instrument loaded
  909 of them - for a known symbol, an unknown one, or anything else - and
  asking about a symbol that does not exist filled the provider with hundreds
  that do while still not finding the one requested. Nothing threw, so nothing
  noticed. A provider now adds only what was asked for, whatever the venue
  chooses to return.

- Asking a venue to load an instrument it does not list now leaves the
  provider empty everywhere, and throws nothing. It used to be four behaviours
  across six venue families, and both multi-family venues disagreed with
  themselves: Bybit spot answered success with an empty list while Bybit linear
  refused with code 10001; Binance spot refused with -1121 while its futures
  family ignored the question; KuCoin threw 900001 on spot and 404000 on
  futures. A caller could not write one piece of code against that, so typing
  a symbol a venue does not list produced a clean message in one family and a
  raw venue error string in another - and the only way to stop it was for the
  caller to hold the venue's own numbers. Those codes stay inside the adapters
  now. Refusals that are not about the symbol - a rate limit, a bad key, a
  blocked address - still reach the caller, with a test for each, and the
  parity table is keyed per family because a sibling family's answer turned out
  not to be evidence.

- A KuCoin key of the older version passed `verify-keys` and then failed when
  a node started. The check tried version 3 and then 2 and reported success;
  the clients took the stated version, or 3, and signed with it for good. So a
  key report said the key was fine and the node it was run for could not
  authenticate - worse than a rejection, because the report had already
  answered the question. The clients now settle the version the same way the
  check does, both read one named list of which refusals are not about the
  version, and a test fails if those two ever drift apart again.

### Added

- A family resolver: an instrument id in, the venue's product family and the
  instrument out, or a clear answer that no family of this venue has it. A
  venue's families are separate APIs, so loading an instrument means first
  choosing which client to build - and that was being decided by reading the
  symbol's spelling, which is a convention rather than a fact. Loading one
  instrument needs no key, so each family is asked for that single id and the
  family that answers owns it. A family that could not be asked at all is not
  counted as a no, because a venue briefly unreachable must not read as an
  instrument that does not exist.

- An adapter declares what it can DO with each market family, not only which
  markets a venue offers: fetching one instrument, listing them, bar history,
  funding history, market data, execution, and amending an order. Hosts were
  keeping this as hand-maintained tables per venue, which is exactly what goes
  stale. There are no derived flags - papering a node needs market data and
  instruments, trading it live needs execution - because a single "tradable"
  flag would have been wrong about KuCoin's perpetuals in both directions for
  a day. Every capability is checked against the thing it describes, so a
  declaration cannot become a second place for the truth to be wrong.

- Leverage reaches every venue. `leverage` on any execution client config, and
  each adapter honours it the way its venue works: KuCoin's perpetual futures
  demand one on every order, while Binance and Bybit hold it per symbol on the
  account and ignore anything sent with an order, so those set it at the venue
  before they trade. Left unset, nothing is touched - which is what every
  configuration written before the field existed means. A strategy written,
  backtested and papered at 3x went live at whatever the account was last left
  on, and nothing said so.

- Orders can carry a broker or partner id (R11.13). `brokerId` on an execution
  client config supplies the id; HOW it travels is each venue's own business
  and is declared per venue, so nothing above an adapter has to know which
  venues have a programme. Binance prefixes the client order id - and because
  that id is the key reconciliation matches an order on, the prefix is applied
  everywhere an order is named to the venue and stripped from everything the
  venue says back, so the engine keeps knowing the order by its own id. Bybit
  sends the id in the `X-Referer` header, outside the signed payload, so the
  order's identity is untouched. KuCoin has no programme an adapter can carry
  an id for and ignores the field rather than refusing it. Left unset, every
  venue receives exactly what it received before the field existed, which has a
  test per venue.

- KuCoin perpetual futures can be traded, not only downloaded and papered. An
  order is a whole number of contracts, so quantities convert at the boundary
  in both directions; the venue requires a leverage on every order and has no
  default, so the config supplies 1, meaning none; the account holds positions,
  which the venue signs in contracts, so the sign becomes the side and the size
  becomes base currency; and stops watch the mark price, because that is what
  the venue liquidates against. The venue cannot amend an order on this market
  at all, so a modification is refused with that reason rather than turned into
  a cancel-and-replace that would leave a perpetual unguarded.

- An adapter can declare its venue. `VenueDescriptor` states, per market
  family rather than per venue, which instrument classes it returns, whether
  it pays funding, where it answers, what a key for it is made of, which
  setting selects it, what it charges before an instrument is loaded and what
  it publishes for free. `bytex venues` prints it and `bytex venues --json` is
  how a host reads it without hosting the engine. Binance, Bybit and KuCoin
  declare themselves; every fact is checked against the adapter it describes.
- KuCoin perpetual futures: market data, the contract catalog, candle and
  funding history, and the declaration. Contracts are published with their
  size in base currency rather than in contracts, so a strategy sizing in base
  units is unchanged across venues. Inverse contracts are not offered, and the
  reason is in `docs/integrations/kucoin.md`. `productType` chooses the market
  and defaults to spot, so configurations written before futures existed mean
  what they meant. The futures execution client is not written yet.

### Removed

- **Venue testnets.** The flag, the hosts, the `*_TESTNET_*` variables and
  `verify-keys --testnet` are gone, along with the documentation for them. A
  venue testnet is a worse simulation than this engine already has: the sandbox
  venue matches orders against the book the real venue is streaming, so it
  prices against real liquidity, while a testnet offers fabricated depth in
  exchange for a second set of credentials to store, route and confuse with the
  real ones. It also carried a defect nobody had hit - Binance's spot and
  futures testnets are separate accounts with separate keys, and one variable
  pair served both, so running two testnet nodes at once authenticated one of
  them with the wrong key. Removing the feature removes the defect.
  A node that wants to rehearse against a venue runs a sandbox execution client
  on that venue's live data; that is what `sandbox-binance-ema-cross.json`
  shows.

### Added

- **Each venue's own margin requirement and maximum leverage, read from that
  venue per instrument** (R4.16). Two of the three venues shipped before this
  release supplied both from a constant in the adapter - 0.05 initial and 0.025
  maintenance for every contract - where Bybit's own risk limits give 0.0066 on
  BTCUSDT. The error was not cosmetic: `Instrument.InitialMarginRate` takes the
  larger of 1/leverage and the instrument's margin, so the figure is a FLOOR
  under every leverage a strategy asks for, and a wrong 0.05 silently charged
  the margin of 20x on a venue granting 150x. `Instrument.MaxLeverage` is new
  and null means the venue does not publish it, which is a different answer from
  unlimited to anything deciding whether a configured leverage is reachable.

- **Where a margin figure came from, recorded on the instrument and in a run's
  result.** Reading the venues fixed every run made from now on and could not
  touch a saved one, because a wrong figure and a right one are the same
  decimal. `Instrument.MarginSource` says which of six things produced it - the
  venue for this contract, the venue for all of them, the venue publishing none
  for a product it does margin, the engine substituting, nothing borrowed, or
  nobody recording it - and `BacktestResult.MarginSources` carries the set a
  run's positions used, in the result and in `result.json`. An instrument stored
  before this existed reports `unrecorded`, which is the honest answer and the
  one that explains why a saved result and a fresh one disagree.

- **A product family declares what collateralises it.** Three venues declare two
  families holding the same instrument class - Binance usdm and coinm, Bybit
  linear and inverse, Bitget usdt and usdc - and until now the only thing
  separating them was a name the venue invented for its own enum, so a host
  asking which family holds a perpetual got the first one declared and could not
  ask for the second. `VenueFamily.Collateral` is a kind and, for one kind only,
  the currencies: nothing borrowed, the instrument's quote currency, the
  instrument's base currency, or a set the venue fixes for the whole family. It
  reaches a host through `bytex venues --json` and is printed per family by
  `bytex venues`. It also states an omission nothing could express before: OKX
  and KuCoin both list coin-settled markets these adapters deliberately leave
  out, and a family declaring the quote currency says so.

- **A backtest reports the leverage it actually applied.** A live run that asks
  for more than the venue grants is refused by name; a backtest was clamped
  instead, by the margin floor above, and said nothing. On keyless Binance a
  strategy written for 50x was measured at 20x - smaller positions, a different
  drawdown, orders denied for margin that would have passed.
  `BacktestResult.Leverages` carries a row per instrument the run worked an
  order in: what was asked for, what the arithmetic used, whether that is a cap,
  and the margin figure and provenance that caused it. Reported rather than
  refused, because a backtest is cheap to run again and a stop at the moment
  somebody presses go, over a figure buried in a venue's margin schedule, buys
  less than telling them what the run did.

## [0.6.0] - 2026-09-23

### Fixed

- Margin is charged the way a venue charges it. Leverage and the instrument's
  margin rates were multiplied together, and both halves of that were wrong.
  Initial margin was `notional / leverage * MarginInit`, so on a 5 % instrument
  an account at 1x posted a twentieth of the notional - leverage of 1, which
  means no leverage at all, carried twenty times what the balance could pay
  for. It is now the notional times the larger of the leverage share and the
  instrument's floor: the whole notional at 1x, a tenth at 10x, a twentieth at
  20x, and no further, because leverage past the instrument's own rate is not
  something a venue grants.
  Maintenance margin was `notional / leverage * MarginMaint`, so an account at
  50x had to keep a fiftieth of the maintenance margin of the same position and
  **the more leverage was set the later the venue stepped in**. What the holder
  chose decides what has to be posted to open a position, not what has to be
  kept to hold one, so it is the instrument's rate on the notional and nothing
  else.
  Each rule now lives in one place - `Instrument.InitialMarginRate(leverage)`
  and `Instrument.MaintenanceMarginRate` - because the venue, the account, the
  risk engine and document sizing all have to agree about what a position
  costs, and four copies of the arithmetic is how they came to disagree.
  **This changes what every leveraged backtest does, which is the point:** a 1x
  account is no longer secretly a 20x one, and a liquidation now happens where
  a venue would have closed the position rather than a long way after it. A
  cash account is untouched.

### Added

- A strategy document says what leverage it is written for (R3.16):
  `account.leverage`, 1 by default, so every document written before the field
  existed does exactly what it did. It is a **requirement, not a request**: the
  venue is what grants leverage, and **a run whose venue grants less refuses to
  trade the strategy** rather than quietly sizing it at what the venue allows. A
  strategy written for ten times leverage and run at one is not the same
  strategy sized smaller - it is a different one, and a result from it would be
  read as evidence about a strategy nobody wrote. The refusal names both
  numbers and what to do about either. A venue granting more is no objection:
  the document is a floor, not a ceiling. Leverage below 1 is refused by the
  validator with `LEVERAGE_INVALID`, because that is not leverage but a
  fraction of the account.

- A venue that matches its own orders says what it is matching them against
  (control protocol version 5). Every `view` carries `venues`: for each paper or
  simulated venue, one row an instrument saying `book`, `quotes`, `bars` or
  `nothing` - **the best thing the venue has right now, not what it was
  configured to want**, because a venue told to match against a book it has not
  been sent is filling on quotes whatever its configuration says. `wantsBook`
  and `bookRequested` carry the configuration and whether the depth was asked
  for. A fill answers "would this have filled, and at what", and that answer is
  worth very different amounts measured against a book, a quote or a bar; paper
  began matching against the book in this release, so without this the change
  would have been silent. `venues` is left out entirely at a node where no venue
  matches anything itself, which is every live node.

- Leverage changes what a strategy document can take. Sizing was capped at what
  the free balance could buy outright, whatever leverage the account held, which
  made the setting mean nothing a user could see and put liquidation out of
  reach of anything a document does. The cap is now the balance times the
  leverage the account holds for the instrument. Leverage and nothing else: this
  venue would allow a good deal more - a 5 % initial margin rate at 1x is twenty
  times the balance - and taking the margin rate into the size as well would
  mean a document asking for no leverage still opened twenty times what it can
  pay for. A cash account and a 1x margin account size exactly as they did.

- Own-order book: a resting order waits its turn (R3.15). An order that comes
  to rest at a price now stands behind whatever is already quoted there, and a
  print at that price serves that queue before it reaches the order - so a
  limit order at the back of the book fills later than the same order at the
  front, which a fill on first touch could not tell apart. A trade past the
  price clears the queue, a level that has grown smaller moves the order
  forwards whether the size was traded or cancelled, and a level that has grown
  leaves it where it was. A repriced order joins the back of the new queue,
  which is what moving a limit order actually costs.
  Every order in a node's `view` carries `sizeAhead` and `queuePosition`
  (control protocol version 4), so "it has not filled yet" and "it will not
  fill until five more go through at that price" stop being the same row on a
  screen. Both are absent for an order standing in no queue; `sizeAhead` of
  zero means the front of the queue, which is a different thing.
  The node works this out from the book and the prints it is given, and says so
  the pessimistic way: a venue matches on a sequence it never publishes, so an
  order is never reported further forward than the data proves. A run with no
  book data has every resting order at the front of its queue, exactly as
  before.

- Order-book-level matching (R8.9). An order that takes now walks the book it is
  given, best price first, paying what each level costs and stopping at its own
  limit, rather than filling at the touch and waiting for the rest. A size
  larger than the touch is filled now, at a worse average - which is what a
  venue does and what 0.5's bound could only report. Fill or kill is decided
  over the whole book the order can reach rather than over one level of it, and
  a market order that eats the book gives up its remainder.
  `BacktestResult.Applied` carries `bookDepth` when a run actually walked a
  book; `Simulation` says only that the venue could have.
  A bar holds the walk off: a bar covers a length of time and a book is one
  moment inside it, so while the latest thing the venue heard is a bar the
  bar's `BarVolumeShare` bounds the fill and no book is walked - the next book,
  quote or print puts depth back on. `FillSizing.WholeFills` ignores the book as
  it ignores every other bound.
  Without this the number a person feels - what the position actually opened at
  - was the touch price however large the order was, and a strategy that trades
  size looked cheaper in a backtest than it could ever be live.

- `BinanceHistory.FetchFundingRatesAsync` and `BybitHistory.FetchFundingRatesAsync`:
  funding history without a node, the way `KucoinHistory` already reads candles.
  Public, static, no client and no message bus, so anything that stores history -
  a catalog download, a tool - calls the same code a running node calls, and
  what is stored can never disagree with what a node receives. The clients now
  call the helpers, so the paging, the page sizes and Bybit's newest-first
  reversal exist once rather than twice.
  Without this the rates fetched in 0.5 could only be had by standing up a live
  data client and driving the request bus, which is not what downloading history
  is, so the last step between funding being charged and funding being reachable
  was missing.

- A paper venue matches against the book its real venue is streaming (R10.11).
  The sandbox subscribed to quotes, trades and bars and never to depth, so a
  paper node connected to a venue that streams its book matched orders against
  bars while the book went past untouched. It now asks the data client for the
  book of every instrument it holds (`SubscribeOrderBook`, on by default with
  `MatchAgainstBook`) and matches against the book the cache maintains - the
  same route a backtest takes, so a paper fill and a backtest fill come out of
  the same matching against the same thing.
  This is where depth is worth having: paper is the step before somebody risks
  money, and the question it exists to answer - would this have filled, and at
  what - is the one a book answers and a bar does not. The data is live, free
  and already flowing; nothing is stored. `MatchAgainstBook = false` leaves the
  venue on quotes and bars.

- Guided parameter search (R8.23). A space of six parameters at ten values each
  is a million runs, so a space worth searching is one nobody can cross.
  `BacktestSearchConfig` describes one - a `Space` of parameters, each a list of
  choices or a numeric range walked in steps - and `RunSearchAsync` breeds
  generations of candidates over it, each generation a batch of chosen points
  judged by the caller. Which figure makes one candidate better than another
  stays the caller's: a function, or an `Objective` naming the figure and the
  currency for a configuration file that cannot carry one. It only ever
  proposes values the space allows, the same seed proposes the same candidates,
  a point already run is not run again, a candidate whose run produced no
  figure is never bred from, and the elites carry forward so a search cannot go
  backwards. `bytex backtest --config` runs one when the file names both a run
  and a space; `examples/configs/search-ema-cross.json` is an example.

- A batch can be given the parameter points to run rather than ranges to cross
  (R8.27). `BacktestBatchConfig.ParameterSets` is a list of points, each the
  value for every path it sets, and each one run once in the order given. It is
  what a search needs: a generation is the particular points the last one
  argued for, not the product of anything, and expressing it as a grid was not
  possible - so a generation had to be a batch per candidate, which is a report
  folder per candidate and no table covering the generation. Sweeps and sets
  are two ways of saying the same thing, so a batch uses one or the other and
  giving both is refused instead of guessed at; instruments and periods still
  multiply over the points either way. A point given as a set and the same
  point swept produce the same run, because both become the same list of points
  before anything expands.

### Fixed

- A margin account holds the margin its working orders need (R4.7). The
  venue reserved nothing for anything but a cash account, so an account could
  commit the same money as many times over as it could send orders: every order
  passed the margin check, because the check was made against a balance nothing
  had claimed. The initial margin an order would have to post is now held the
  moment the order is worked and given back when it fills, cancels or expires,
  and what the venue calls free is free of both those holds and the margin its
  open positions are already posting. An order that cannot be carried is refused
  with what it needed, at what leverage, against what was free. Closing asks for
  nothing, because closing gives margin back - except on a hedging venue, where
  the same order opens a position of its own and pays for it like any other.
  R4.7 was written as "an order is judged against the initial margin it needs at
  the account's leverage, against the balance free of margin already committed",
  and the catalog called it released; half of it was not true until now.

- The account is told the leverage its venue grants (R8.8). Nothing set it before:
  every account in the cache stood at 1x however the venue was configured, so
  the risk engine judged margin at 1x and anything sizing against the account
  could not know better. `AccountState` carries `Leverages` and
  `DefaultLeverage` now, optional and null-means-unchanged, so an adapter that
  learns its leverage from a venue can report it the same way it reports
  balances. Leverage per instrument in simulation is R8.8, and it reached the
  venue's own arithmetic but never the account every other engine reads.

- `MarketArchive.Entries()` left funding out of its listing. Funding rates
  could be written and read back, but a catalog holding them reported that it
  held nothing of the kind, so anything built on the listing - a data screen, a
  coverage check before a run - was blind to data that was there.

- And with the same limit. A limit is counted from whichever end the caller
  anchored: a start says where the window begins, so the limit caps how many
  bars follow it, and with no start the newest are the ones wanted. KuCoin took
  the newest even when a start was given, so the same request answered by two
  venues gave different bars - the last of a window from one and the first of it
  from the other. Counting forward from a start is also the only form that can
  stop as soon as it has what was asked for, rather than paging a whole window
  to throw most of it away. Nothing had ever asked that venue for a start and a
  limit together; two tests do now.
- Every venue answers the same question with the same period. What a window of
  bars contains is now one rule in one place - `BarWindow.Closed` - and every
  adapter returns what it says: each bar that closes at or after the start,
  opens at or before the end, and has closed by now. Each venue filtered
  differently before, by a candle's open or its close, in seconds or in
  milliseconds, including or excluding the candle still forming, so the same
  period asked of three venues came back as three periods and a catalog filled
  from them disagreed with itself at the edges. Binance and Bybit filter by a
  candle's open, so they are now asked for one interval more than the window and
  the rule decides what belongs; that is the venue's business and stays in its
  adapter. The visible change: a window now includes the bar that closes exactly
  at its start, because that bar covers the window's first moment, and never the
  bar still forming.
- The start/end form of `BinanceHistory.FetchBarsAsync` and
  `BybitHistory.FetchBarsAsync` gives the whole window. It documents itself as
  the bars between two times, passed no limit, and no limit meant one page - so
  a window of 1,080 hourly bars came back as 1,000: the first 1,000 on Binance,
  the last 1,000 on Bybit, because one venue answers oldest first and the other
  newest first. Anything filling a catalog that way stored a silently shorter
  history, and a backtest on it covers less time and looks perfectly healthy.
  No limit and a start now means the window, as `KucoinHistory` already had it.
  The tests that came with those helpers both passed an explicit limit, which
  is why neither could catch it; there is now one per venue on the no-limit
  path the convenience overload actually takes.
- Binance and Bybit candle history can be fetched without a node.
  `BinanceHistory.FetchBarsAsync` and `BybitHistory.FetchBarsAsync` are public
  and static, taking an http client and an instrument, as
  `KucoinHistory.FetchBarsAsync` already was. The paging, the direction each
  window is walked and the page sizes were private to each data client, so
  anything that stores history without running a node - a catalog download -
  had to write them again, and the second copy stops matching the first the day
  a venue changes a default. It was written twice. Each data client now calls
  the helper, so stored bars and a node's bars come from one piece of code, and
  a test asserts the two paths return the same bars across more than one page.
- A search leaves a record of what it searched. Every generation is a batch, and
  every batch wrote its table as `batch_{CCY}.csv` into the run's output
  directory, so generation two overwrote generation one and a three-generation
  search left the last generation's rows and nothing else. A batch now says what
  its own table is called (`BacktestBatchConfig.ReportName`, still `batch` by
  default), a search names each generation after itself, and the search writes
  `search_{CCY}.csv` - a row a candidate, in the order proposed, with its
  generation, whether it was run or taken from an earlier one, what it scored
  and the figures of the run it became - beside `search.txt`. Each candidate's
  own report is where it always was.
- Asking a venue what became of one order is answered by every venue that can
  report an order. `QueryOrderAsync` was implemented by Binance alone, so the
  same command against Bybit or KuCoin returned a completed task without
  sending a single request - which reads to the caller exactly like a query
  that worked, leaving it waiting for an answer that was never coming. Binance's
  implementation was three lines over a report the other two already had, so it
  is now what the base class does for everyone, and a venue that cannot answer
  says so in the log instead of returning as though it had. Nothing in the
  suite had ever called it on any venue; all three are now held to reaching
  their venue.
- And the answer now reaches the caller rather than only the log. A strategy
  that asked what became of an order got a log line: the report died in the
  client, so the order this node held stayed exactly as wrong as it was, which
  is the one thing asking was for. The report and that order's fills are now
  handed to the execution engine and applied through the same reconciliation a
  start-up mass status goes through, so an order whose events were lost catches
  up when it is asked after instead of at the next restart. The fills are
  fetched with it - one extra request - so a fill this node never saw is
  applied with the venue's own trade id, quantity and commission rather than
  worked out by subtraction: a reconciliation that invents a trade cannot be
  told from one that happened. Asking about an order the venue agrees about
  changes nothing, and a query is not counted as a reconciliation of the
  venue.

## [0.5.0] - 2026-09-23

### Added since the branch was first pushed

- Bar-driven fills are bounded too (R8.24). A bar offers `BarVolumeShare` of its
  volume - a tenth by default, `SimulatedVenueConfig.DefaultBarVolumeShare` -
  which is what one participant could plausibly have been over that length of
  time. One bar is one budget however many prices its path walks, and what an
  order cannot take goes on working and takes its share of the next bar: the
  bound is a rate over time, not a book that has run out, so a market order
  under it keeps working rather than giving up its remainder.
  Without this the feature was unreachable for the way the engine is actually
  used: almost every backtest is driven by bars, and a simulator that fills any
  size on any instrument biases a scan across a venue towards the instruments
  that could not have absorbed the trade. Measured on Bybit 15-minute bars
  against a 25,000 USDT position: BTCUSDT is untouched by the bound, ADAUSDT is
  bounded on 59% of its bars. Set the share to null for the old behaviour.
  The published reference backtests moved with it, and `breakout-retest` is the
  one to read: it ended the run with 4,119 USDT of a million, having paid 25,034
  in fees on risk-sized orders the market never had; bounded, it ends with
  1,061,685. The old number was not a strategy losing money, it was a simulator
  filling orders nobody could have filled.
- `BacktestResult.Applied` says what the simulator actually did in a run, beside
  `Simulation`, which says what its venues were configured to be able to do. A
  venue set up for partial fills and fed data that says nothing about size has
  bounded nothing, and a run with no funding rates in it has charged nothing: a
  product telling its users what their result accounts for has to read the first
  one. `BacktestResult.Participation` carries the share assumed, how many fills
  were bounded and how much went in under a bound.
- Funding history from the venue (R8.15): `RequestFundingRates`, answered by
  Binance (`/fapi/v1/fundingRate`, oldest first, a thousand to a page) and Bybit
  (`/v5/market/funding/history`, newest first, two hundred to a page), paged to
  the whole period and returned oldest first like every other kind of history.
  The catalog already stored funding; without a way to fetch it, nothing could
  put any there, so R8.15 would have shipped with no rates for anybody to be
  charged from. A spot client answers with nothing rather than asking a venue a
  question it has no answer to.

Realism. A backtest before this one traded against a market with infinite size
at the touch, held a perpetual for nothing, and rode a leveraged position however
far under water it went. All three are gone, so **results from before 0.5 are
not comparable with results after it** - which is the point of the release.

### Added

- Reference backtests (R12.9): `examples/reference/` holds one file per shipped
  example - the document, the seeded bars it runs on, the account it starts
  with, and the numbers the engine answered: fills, positions, closed positions,
  ending balance, realised profit, commissions and maximum drawdown, to eight
  decimal places. `ReferenceBacktestTests` runs all of them on every commit and
  names the one number that moved when one does. The data is generated from a
  seed rather than stored, so a reference needs no data files and cannot drift
  because a catalog changed, and each file records what the simulator that
  produced it models. Nothing about the engine as a whole was pinned before
  this: every other test says a rule holds, and none of them would have noticed
  the total return changing. What they pin is the document runtime on bar data
  and every statistic computed from it; the matcher's own rules stay pinned in
  the simulator's own tests, because these examples act on bar closes and never
  meet a quote's size or a print at a resting order.

- `PerContractFeeModel` (R8.17): a fee for every contract traded, with its own
  maker rate where a venue has one. Futures and options venues bill per
  contract, so a percentage of notional is the wrong shape for them in both
  directions - far too much on a large contract, far too little on a small one -
  and a strategy trading many cheap contracts pays most of its edge in fees a
  percentage model never shows. With fills now bounded by the size on offer, one
  order can pay in several bills, each for the contracts that fill took.

- Liquidation of margin accounts (R8.16). Every price the venue sees is a check:
  the account's balance plus what its open positions are up or down, against the
  maintenance margin those positions require at that same price. Below it, the
  venue takes the book away - the account's working orders are cancelled - and
  closes each position with a reduce-only market order tagged `LIQUIDATION`,
  under the strategy whose position it is, registered with the engine so the
  strategy's own books follow it rather than going on believing it still holds
  one. Each is reported (`liquidations.csv`, `liquidations` in the JSON,
  `BacktestResult.Liquidations`) with what was held, the price it was valued at,
  and the equity and maintenance margin that settled it.
  Until now a leveraged position rode however far under water it went, so a
  margin backtest could show a drawdown where a venue would have closed the
  account. `SimulatedVenueConfig.Liquidate` is on by default and turning it off
  reproduces the old behaviour. A cash account is never liquidated: it owns what
  it bought.
- `BacktestResult.Simulation` says what the simulator that produced a result
  models, by name (`partialFills`, `funding`, `liquidation`), taken from how the
  venues were configured. A product built on this engine can tell its users what
  it can and cannot do from the engine's own answer rather than from a release
  note, and a venue configured out of something does not claim it.

- Funding payments on perpetuals (R8.15). A published rate is applied to an open
  perpetual position at the price the venue last saw: the long pays a positive
  rate and takes a negative one, on the notional the position is worth now
  rather than what it cost, in the currency the instrument settles in - quote
  for a linear contract, base for an inverse one. Every payment raises an
  account state like any other movement and is listed in the report
  (`funding.csv`, `funding` in the JSON, `BacktestResult.Funding`), with the
  rate, the signed position and the price it was charged on.
  `FundingRateUpdate` has been in the engine since 0.2 and the simulator never
  read it, so holding a perpetual was free: a strategy that held one for a month
  paid nothing for it. The data reaches a run through the catalog, which now
  stores funding rates (`funding` in the catalog, `--data-kind funding` in a run
  configuration), and a rate arriving while the account is flat is nobody's to
  pay, as at a venue.

- Partial fills in the simulator (R8.24). A fill is bounded by the size the data
  says was on offer where it happens: the other side of the quote or the book
  for an order that takes, the print that reached a resting one. One touch
  cannot be taken twice - what the first order takes is not there for the second
  - and what is left of an order goes on working, or is given up where its kind
  says so: an immediate-or-cancel order keeps what it got, a fill-or-kill order
  that cannot be filled whole is not filled at all, a market order takes what
  the book had and gives up the rest, a market-to-limit order rests its
  remainder at the price it got, and an iceberg shows one slice at a time.
  Until now the simulator filled every order whole at one price whatever was on
  offer there, so a backtest traded against infinite liquidity at the touch:
  entries that could not have filled, at prices nobody would have got. **Results
  from before 0.5 are not comparable with results after it.** A venue set to
  `FillSizing.WholeFills` fills as it used to, for reproducing an old run.
  Data that says nothing about size bounds nothing: a bar carries the volume of
  a whole bar rather than a size at a price, so bar-driven runs are unchanged.

## [0.4.0] - 2026-09-22

### Added

- A document can work an order in pieces: the `work` parameter on `act.order`
  names an execution algorithm - `twap` today - with the horizon and the
  interval in minutes, and the node turns it into an order carrying
  `OrderScheduleId` and its own pace. Until now the algorithm shipped and no
  document could reach it.
  Nothing has to be registered by the host: a document that names `twap` cannot
  run without one, so the moduleHost starts a `TwapSchedule` with it, under the
  id `TWAP` (`INeedsOrderSchedules`, which any strategy may implement). A host
  that has its own algorithm under that id keeps it, whichever of the two was
  added first.
  The node reports the worked order as the one order the document asked for:
  working until the last piece is done, filled once, at what the pieces averaged
  - and, when a piece is refused and the pace runs out with part of it in, filled
  for that much rather than waiting on an instruction that is already over. The
  instruction never reaches a venue, so it never fills and never closes; a node
  that took it for its own state would have jammed on it for the rest of the run.
  Only a market or a limit order can be worked, and `cancelAfterBars` is not a
  bound on a worked order because the horizon is: the validator refuses either
  combination with `ORDER_CANNOT_BE_WORKED`.

- A ceiling on sizing: `maxNotional` (the most the position may be worth in the
  quote currency) and `maxPercentOfBalance` (the most it may be as a share of
  the free quote balance), on the `sizing` parameter that `act.order`,
  `act.bracket`, `act.ladder` and `risk.sizing` share. They are read after the
  mode has worked its number out, the smaller of the two holds, and when one
  holds an order back the decision log says what was asked for and what was
  allowed. Both are zero by default - no ceiling - so a document written before
  they existed sizes exactly as it did.
  A mode says *how* to size and cannot say how much of the account may be in one
  order, which is what `riskPercent` needed: the nearer the stop, the larger the
  position it buys, so "risk one percent" with a stop a tenth of a percent away
  is the whole account. Eight templates shipped with the engine did exactly that,
  each opening 99 to 105 percent of the account, and until now the only way to
  bound them was to build `min(risk, ceiling)` out of a comparison and two order
  nodes - eight nodes a strategy for something a mode should carry. The
  `riskPercent` choice now says a ceiling is what bounds it, instead of saying
  nothing bounds it.

- Many runs from one description, which is how a question about a strategy is
  usually asked: `BacktestBatchConfig` is a run and what to vary in it - an
  instrument list, a period list, and a grid of parameter values set in a
  strategy's own payload by dotted path - and `BacktestNode.RunBatchAsync` runs
  every combination. The runs expand in a settled order, each with a run id of
  its own, so two runs never write their reports over each other and run seven
  of yesterday's report is run seven of today's. `MaxParallel` runs several at
  once; they are independent engines, so what a run produces does not depend on
  it. A run that cannot be made or throws is a line in the table with its
  reason rather than a hole in it, because a scan that quietly dropped what it
  could not run would report the best of an unknown number of instruments.
  `BacktestBatch.Rows(currency)` is the comparable table, `Summary(currency)`
  is it as text, `Best(currency, by)` orders it by whatever is being looked
  for, and `batch_{CURRENCY}.csv` is written beside the runs' own reports. A
  path that cannot be followed or a sweep with no values is refused before a
  single run is made. `bytex backtest --config` reads a batch: a file whose
  object names a `run` is one, anything else is what it has always been.

- A live node can keep checking itself against its venues while it runs, not
  only at start-up: `reconciliationInterval` in its configuration, or
  `bytex run --reconcile-interval 00:05:00`. It is what notices a fill whose
  event never arrived, an order cancelled at the venue and a position somebody
  closed by hand, instead of trading on a picture that quietly went stale until
  the next restart. Zero, the default, leaves it to the check at start-up,
  because a mass status costs a request to every venue and some of them count
  those. Every check after the first asks only for what has happened since the
  one before it, with one interval of overlap; a check is skipped while an order
  is in flight, because the venue cannot report what it has not acknowledged
  yet, and while the check before it is still running.
  `OrderCoordinator.ReconciliationCount`, `ReconciledDifferences` and
  `LastReconciliation` count the checks, the times the venue and the node
  disagreed, and when the last one was, and every status, heartbeat and view
  carries all three.

- `TwapSchedule`: an order too large to go out at once is worked as many
  equal slices at an even pace, the first at once and one every interval until
  the horizon is up. The slices add up to the order exactly - whatever rounding
  is left over goes out on the last one - and a venue with a minimum order size
  gets fewer, larger slices rather than a stream of rejections. A slice is a
  market order when the order is one and a limit order at the order's own price
  when it is a limit order: the pace is the algorithm's business, the price is
  the order's. The horizon and interval come from the algorithm's configuration
  or from the order itself (`orderScheduleParams`: `horizon`, `interval`).
  Cancelling the order stops the slices, and an order it cannot work in slices
  is cancelled with the reason rather than left looking alive. The
  `OrderSchedule` base class it is built on was already there with nothing behind
  it; this is the first algorithm shipped with the engine, and the requirement
  for the base class is no longer a contract waiting for one.

- Orders can be triggered here instead of at the venue, which is what makes a
  venue with no stop orders tradeable by a strategy that uses them. An order
  names the price its trigger is judged against (`emulationTrigger`: the last
  trade, the bid/ask, the mark or the index), and the execution engine holds it:
  the venue is told nothing, the order is `Emulated`, it can be cancelled, and it
  expires here if its own GTD time passes. When the market reaches the trigger it
  is `Released` and the venue is told about a market order (stop-market,
  market-if-touched) or a limit order at the price it was holding (stop-limit,
  limit-if-touched). The released order keeps the id its owner submitted and
  carries the history to match, so a fill comes back on the order that was
  placed. A stop is reached at its trigger or through it, an if-touched order
  when the market comes back to it, a bid/ask trigger reads the side the order
  would have to cross, and a trigger the market has already passed is reached at
  once rather than on the next tick. Anything else that asks to be triggered here
  is denied (`EMULATION_UNSUPPORTED`), and an order list carrying one is refused
  whole (`EMULATION_IN_LIST`) rather than half sent.

- The loss limit is watched between fills. A strategy that buys, holds and
  sends nothing while the market falls submits nothing for the engine to judge,
  so a limit that only ran on submission could not see the loss that was
  happening. The engine now reviews the figure as the prices that move an open
  position arrive - quotes, trades, bars and mark prices - at most once per
  `OrderPolicyConfig.LossWatchInterval` (a second by default, of the engine's own
  clock, so a backtest watches the same moments every run). `WatchOpenLoss()`
  does it on demand and `LossWatchCount` says how often it ran. In a live node
  it happens on the tradingRuntime thread, like everything else a node does with data.

- Reaching a loss limit stops the engine trading by itself. `RiskLimits.OnLossLimit`
  is `StopTrading` by default: the engine puts itself into `Reducing` and stays
  there - nothing that would add gets through, everything that gets a position
  out still does, and a host resumes it with the release it already has. It does
  that once per period, so a host that resumes a node is not overruled a second
  later by the same loss; the rest of the period is then judged order by order,
  which is what the other setting, `DenyAdds`, does throughout.
  `LossLimitStoppedCount` counts it. A node carries it as
  `tradingRuntime.orderPolicy.limits.onLossLimit` in its configuration, as
  `bytex run --on-loss-limit stop-trading|deny-adds|flatten` on the command
  line, and reports it in the `limits` of every status, heartbeat and view.

- A loss limit can also close what is open: `LossLimitBreach.Flatten` does
  everything `StopTrading` does and then takes the book off the venue - every
  working order cancelled, a reduce-only market order for every open position,
  sent through the engine like any other order, so the state it has just put
  itself into lets them out. Stopping is not closing, and a stopped node holds
  what it held: the account goes on losing on the very position that reached the
  limit until somebody closes it, which is what this is for.
  `LossLimitFlattenedCount` counts the positions it closed. Never a default, and
  never will be - closing someone's position without being asked is the one
  thing a limit must not decide for itself - so a host says so, in its
  configuration or with `--on-loss-limit flatten`.

- `ind.math`, an arithmetic node: a value worked out from another with plus,
  minus, times or divide, against a second series or against a constant. A
  strategy can now compare against a price it computes itself - a level times
  1.03 - where before the only derived value in the catalog was the distance
  between two series. A connected second input that has no value yet publishes
  nothing rather than counting as zero, and dividing by zero publishes nothing
  and says so - on every bar it happens, because a node cannot tell a warm-up
  bar from a live one and a single report made during warm-up would be thrown
  away. The catalog is 80 types.

### Changed

- The loss limit counts what open positions are down, not only what has been
  realised. A position held through a fall costs the account exactly what one
  closed in the fall costs it, and the limit could not see the difference: a
  strategy 20% under water on an open position was, to the loss limit, a
  strategy that had lost nothing. The period's mark now carries what was open as
  well as what was realised, so a position still under water from yesterday does
  not spend today's limit, and the denial reason says "realised and open". A node
  configured with a loss limit before 0.4.0 will stop sooner than it used to -
  which is what the limit was set for.
- Every choice of an enum parameter in the node catalog now carries its own
  meaning: the name to show, one sentence saying what picking it does, and - for
  a choice that decides how the number beside it is read - the name of that
  number and the unit it is then in, with the range and step it should take
  there. `ParamSpec.Choices` is a list of `ChoiceSpec` instead of a list of
  strings, `ChoiceValues` and `Choice(value)` give the bare values back, and the
  exported catalog's `choices` are objects rather than strings. What a document
  stores does not change, and the exported JSON Schema still lists the bare
  values, so no document is affected. The case that prompted it: `act.order`
  sizing is a mode and a number labelled "Value", and under `riskPercent` the
  quantity is chosen so that a stop-out costs that percentage of the balance -
  the nearer the stop, the larger the position, up to everything the free
  balance can pay for. That now reads as "Percent of the balance to risk on the
  stop", in "% of the balance lost if the stop is hit", with the sentence that
  says a stop one percent away stakes the whole balance. Thirteen parameters
  across nine node types decide how the number beside them is read; every enum
  parameter in the catalog gained per-choice text - 55 parameters and 196
  choices - including `confirm` on `cond.cross`, which now says plainly that
  nothing evaluates ticks yet.

### Fixed

- The documentation said venue reconciliation "compares net positions with the
  venue and logs mismatches". It has taken the venue's word and booked the
  difference since 0.2.0; only the sentence was still describing what it used to
  do.
- A test of the live clock could fail for no reason: it set an alert 30
  milliseconds out and then read the clock's list of pending timers, so a stall
  between the two statements left the list empty and the run red. The clock was
  never at fault. The two things the test is about are now read where neither
  can race: that a pending alert is registered, from one due in a minute, and
  that a fired one is forgotten, after the event has arrived.

## [0.3.0] - 2026-09-22

### Fixed

- A backtest's equity curve is the account over time, not a step per fill. It
  was built by walking position events and adding each fill's realized profit,
  one point per fill, so nothing that happened to an open position existed in
  it: the curve could not fall while a position was held. Every figure taken
  from it agreed - the maximum drawdown of a strategy that bought and held
  through a 20% fall was zero, and the daily returns behind Sharpe and Sortino
  existed only for days that closed a trade. A six-month run whose position was
  13.6% under water at its worst reported a drawdown of 0.01%, which was the fee
  between a buy and its sell. **A strategy that never closes a loser looked
  riskless.** The run now samples equity once per timestamp of data - starting
  balance plus realized profit plus every open position marked at the price the
  venue had just seen - and the drawdown and the daily returns read that curve.
  It is the same quantity the per-currency statistics already reported as
  `TotalPnl`, sampled over time instead of over fills.

- `CurrencyStatistics.ProfitFactor` is `decimal?` and is null when a run had no
  losing trade. It was `decimal.MaxValue`, which a host holding the record read
  as a number and printed as a 29-digit integer; the JSON already wrote null,
  and now the property says the same thing.

### Added

- A margin account is asked whether it can hold an order. The risk engine
  checked a cash account's balance and had no margin logic at all, so on a
  margin account any order passed the balance gate however much margin it
  needed. `CheckMargin` - on by default, like the cash check - computes the
  initial margin from `MarginAccount.CalculateInitialMargin` and compares it
  with the balance free of the margin already committed; an order beyond it is
  denied with `INSUFFICIENT_MARGIN` naming the margin, the leverage and what
  was free, counted as `MarginDeniedCount`. Anything that reduces is let
  through, and a quote-quantity order is judged on the size it converts to.
  **A margin-account backtest that used to submit what it could not hold now
  has those orders denied**, which is the point of the check; set
  `CheckMargin = false` for a venue that judges margin itself (#98).

- A leverage a margin calculation cannot use is refused where it is written. An
  account's setters already demanded at least 1; a simulated venue took
  `DefaultLeverage = 0` and divided by it when the first position opened (#98).

- A node can be told to show a moving price. `DisplayPrices` (or
  `bytex run --display-prices`) subscribes quotes for the instruments the node
  holds - or the ones `DisplayPriceInstruments` names - purely so that whatever
  watches the node has a price between bars: they are cached and reported in
  `status`, `heartbeat` and `view`, and no strategy that did not subscribe to
  quotes itself receives them. The subscriptions are given back when the node
  stops, a node holding more than `TradingNode.MaxDisplayPriceInstruments`
  instruments asks to be told which to show instead of opening a stream for
  each, and a node with nothing to show says so. A sandbox node's venue matches
  on these quotes as well as on its bars, and its log says so (#101).

- One switch stops a node trading without stopping the node.
  `TradingNode.Halt(cancelOrders, closePositions)` denies every order a strategy
  submits from then on, while the strategies keep running, keep their state and
  keep seeing data; `Resume()` releases it, `IsHalted` and `TradingState` say
  where it stands, and `StartHalted` (or `bytex run --halted`) starts a node
  that places nothing until a host lets it go. A halt may take the book off the
  venue on its way down - the cancel and the flatten happen before the halt is
  in force, so the orders that close a position are not denied by it.

- A host sets a node's limits from outside it. The loss and exposure limits and
  the caps live together in `RiskLimits`, which reads and writes as the text a
  person would type ("1000 USDT", "50%"): in `tradingRuntime.orderPolicy.limits` in the
  node's JSON, over the control channel with the new `limits` command, or with
  `--max-loss`, `--loss-period`, `--max-exposure`, `--max-open-positions`,
  `--max-open-positions-per-instrument`, `--max-working-orders` and
  `--max-working-orders-per-instrument` on `bytex run`, each of which overrides
  the file one value at a time. `OrderPolicy.SetLimits` replaces them while the
  engine runs and does not forget what the period has already lost.

- The control protocol is at version 3: `halt`, `resume` and `limits`, and
  `tradingState`, `halted` and `limits` in every status, heartbeat and view, so
  a monitor can show what a node is enforcing and what it would send back to
  change it (#100).

- The risk engine can be given caps on how much may be going at once:
  `MaxOpenPositionsPerInstrument`, `MaxOpenPositions`,
  `MaxWorkingOrdersPerInstrument` and `MaxWorkingOrders` (in
  `OrderPolicyConfig.Limits`), counted from the cache
  and off unless set. An order beyond a cap is denied with `POSITION_CAP` or
  `ORDER_CAP` naming which one it hit and by how much, counted on the engine as
  `CapDeniedCount`. Adding to a position the account already holds opens nothing
  new, so it does not count against the account's position cap; the orders of
  one submitted list count towards the order caps as they are judged; and
  nothing that reduces is ever denied by a cap (#99).

- The risk engine can be given a loss limit and an exposure limit. Set
  `MaxLossPerPeriod` and a `LossPeriod`, or `MaxExposure`, in
  `OrderPolicyConfig.Limits`, as an amount in a
  currency or as a percentage of the equity the account held when the period
  opened, and orders that would add are denied with `LOSS_LIMIT` or
  `EXPOSURE_LIMIT` naming the figures; both counted separately on the engine and
  published as `OrderDenied`, which a host already reads. Orders that reduce are
  never denied by either, a period is counted off the epoch so a day is a UTC
  day, and an order list is judged order by order against what the ones before
  it would already have added. Neither limit exists until it is configured, so
  nothing changes for an engine that does not set one (#98).

- `act.grid` works a price range level by level. Given the two ends of the
  range, how many levels to cut it into, how much to trade at each and how much
  profit a grid is worth, it puts an order at every level and, once one has
  entered, the order that takes the profit on that level; a level that completes
  its round trip arms itself again at the same price. A level the instrument
  refuses - below the venue's minimum notional, or a size the venue does not
  trade - is named in the decision log rather than left out in silence, and a
  range it cannot work is refused once rather than retried bar after bar. What
  is resting comes back off the venue when the grid is switched off and when the
  strategy stops: nodes can now say so by implementing `IStoppableNode`, which
  the runtime calls after the last frame. To the validator a grid both places
  and closes orders, so a document whose only action is a grid is a whole
  strategy - and a grid that would sell a spot range short is refused the way a
  short entry is (#97).

- A node can work several price levels at once. `NodeLevels` holds a level per
  key - its price, its size, the round trips it has completed, and an order it
  is following on each side - enumerated in a pinned order so a rerun repeats
  itself, fanning order events out to every level, and saved and loaded as one
  value through `IStatefulNode`. `TrackedOrder`, which follows one order a node
  placed, moved into `Bytex.Documents.Runtime` beside it and is public, so a
  node written outside this repository can own its orders the way the built-in
  ones do. This is the machinery a self-refilling grid needs; no node type
  changed (#96).

- `act.dca` places the safety-order ladder an averaging strategy is built on.
  Anchored at the fill that opened the position: how many orders, how far the
  first sits from the entry (percent, ATR, ticks or price), how much further
  each next one goes, how much larger each next one is, and the most the whole
  position may reach. A rung that would take the position past that cap is not
  placed, and the decision log says which one and why; so is a rung whose size
  rounds to zero. What is left of the ladder is cancelled when the position
  closes, so the next position starts with a ladder of its own. Wire it to the
  entry's `filled` pulse and its `position`; with `risk.exit` anchored to the
  average entry, the stop and the target follow every add (#95).

- `risk.exit` can be anchored to the average entry. With `anchor: averageEntry`
  both the stop and the target are computed again from the position's average
  whenever it moves, and the resting orders are moved to the new levels rather
  than replaced, so the node keeps its own order ids and tags. Until now the
  exit only resized them to the larger quantity and left their prices where the
  first fill put them: after two or three safety fills the stop sat further away
  in R than the document asked for, and the target sat behind the average, where
  it could no longer be reached. The default, `anchor: entry`, is what the node
  did before (#94).

- `data.position` reads the strategy's own position into the graph: whether one
  is open, which way, its quantity and average entry, its unrealized profit in
  percent and - when something wires a stop into it - in R, how many fills grew
  it, and how many bars it has been held. It also keeps the price of the last
  exit and how many bars ago it happened, readable while flat and across a
  restart, which is what a rule that re-enters after an exit measures from. The
  position facts are read from the cache every bar; only what the cache forgets
  when a position closes is kept by the node (#93).

- `act.close` publishes the price it closed at. The node places and owns its
  reduce-only market order - tagged with its own id, tracked to a terminal
  state - and publishes `filled` when it fills and `fillPrice` with the price,
  so a document can act on where it actually got out: re-enter from it, report
  it, or wait for the exit before moving on. It refuses to place an order for a
  position the instrument rounds to nothing, and says so, instead of sending a
  zero quantity (#92).

### Fixed

- Everything a node reports over the control channel is built on the node's own
  thread. A `status`, and every heartbeat, read the cache from the channel's
  thread while the tradingRuntime was writing it: at best that reports a moment which
  never existed - a quote that had arrived but was not applied yet - and at
  worst it reads a collection while it is being changed. The view already did
  this correctly; now the status, the heartbeat and the replies to `halt`,
  `resume` and `limits` do too, and a node whose loop has stopped reports
  nothing rather than numbers read from under it (#101).

- A host can connect to a node's control channel while the node is still coming
  up. On Windows the named pipe waited for the node by itself, but a Unix
  socket that does not exist yet refuses at once, so `NodeControlClient` gave up
  on the first attempt and told the host the node had failed to start. The
  timeout is a window on every platform now, and a channel nobody serves ends
  in a `TimeoutException` that names it (#100).

- Closing one leg of a hedged book works in a backtest. The venue judged every
  reduce-only order against its own netted position, and a long leg and a short
  leg net to nothing, so closing either was rejected as an order that would
  increase a position. Under `OmsType.Hedging` the venue no longer judges or
  clamps a reduce-only order against that net; the leg is the execution
  engine's, which applies the fill to the position the order names.

- A backtest's initial margin is the account's initial margin. The venue floored
  it at a ratio of its own - `max(MarginInit, 1 / leverage)` - so above a
  leverage of `1 / MarginInit` it reported more margin than
  `MarginAccount.CalculateInitialMargin` computes for the same position, while
  the maintenance margin beside it followed the account. There is one formula
  now.

- A missing instrument spec is refused by name. Every instrument constructor
  classified the spec before the base could check it, so `new CurrencyPair(null)`
  threw `NullReferenceException` instead of `ArgumentNullException`.

- Nothing in the test suite is skipped any more. Five tests were marked with the
  defect they had found and left switched off; three of the five defects are
  fixed above, and the other two - a sub-tick timestamp rounding 100 ns late,
  and a hedged position id dropped before the fill - had been repaired in the
  engine while their tests stayed off. All five run.

- A node's fill price survives a restart. The order tracker behind `act.order`,
  `act.bracket` and now `act.close` wrote the price into node state and never
  read it back, so a restarted node published no fill price until its next fill
  and anything measuring from the last one had nothing to measure (#92).

## [0.2.0] - 2026-09-21

### Added

- A strategy document runs. `DocumentStrategy` compiles a document once at
  start - nodes in topological order with document order as the tie-break, each
  node's bar type propagated from the bars it reads, the warm-up it needs taken
  from its parameters - and evaluates the graph into a frame of one value per
  output port on every close of the primary bar type. A node in an inactive
  phase is skipped and publishes nothing. Action nodes place ordinary orders
  through the strategy's own order factory, so the risk engine, the cache and
  the reports see nothing unusual, and each node tracks the orders it placed
  from submission to a terminal state. Every fire, skip, order and transition is
  a strategy event, kept in `Decisions`; `LastValues` is the last frame and
  `LastFrame` its condition-level summary. `OnSave` and `OnLoad` carry the
  phase, the bar index and the stateful nodes across a restart, and on start a
  document replays recent closed bars without placing anything so a long
  lookback is ready on the first live bar. The provider `bytex.document` builds
  one from a strategy configuration, and `bytex documents` validates a document,
  prints the catalog and prints the JSON Schema. A descriptor now carries its
  runtime factory again, so the validator can say `NODE_NOT_RUNNABLE` for a type
  this engine cannot build. See
  [design note 0012](docs/design/0012-document-runtime.md).

- `DocumentValidator`: a strategy document is checked before anything tries to
  run it, and every problem comes back as a `Finding` with a level, a stable
  reason code, the node and port it concerns and often a fix. Three layers -
  the document as a document (schema, ids, parameters, node types, ports,
  edges, required inputs, cycles, caps, phases and transitions), the strategy
  as a strategy (never places an order, nothing protects a position, a target
  behind its stop, sizing by risk with no stop, contradicting comparisons, an
  unreachable phase, a node in a mode it may not run in, an event a model
  classified gating a live run), and the strategy against the world it will run
  in (instruments exist, prices on the tick, sizes above the minimum quantity
  and notional, no short entry on a spot instrument, data covering the
  warm-up). A block stops the walk and `StoppedAt` says where, so a host shows
  findings about one thing rather than the wreckage of the layers after it. The
  third layer needs an `IValidationContext`; without one it is skipped rather
  than guessed at, so a document can be checked with no catalog, no data and no
  engine. Caps are `ValidatorOptions`, not part of the format. See
  [design note 0011](docs/design/0011-document-validation.md).

- The node catalog: one description per node type a strategy document may
  name, holding its kind, display name, plain-language face, typed input and
  output ports, and its parameters with defaults, limits, units and choices.
  `NodeCatalog.Default` is the built-in catalog, version 1: 76 types in eight
  families - data (6), indicators (20), levels (9), conditions (14), actions
  (11), risk (8), flow (6) and events (2) - and a plugin can register
  `custom.*` types into a catalog of its own. Every action reads a condition
  telling it when to act, every condition publishes something another
  condition can read, an action that places an order says how much to trade,
  and an event filter leaves out what a model classified until it is asked
  for. `PortSpec.Compatible` is the one wiring rule: numeric
  kinds are interchangeable, a one-bar pulse feeds anything reading a boolean
  but not the reverse, and bars and positions connect only to themselves.
  `NodeCatalog.ExportJson()` writes the catalog for a palette or an assistant,
  grouped by kind and ordered by name; `DocumentSchemaExporter` writes a JSON
  Schema (draft 2020-12) for a whole document, allowing exactly the catalog's
  node types and carrying each type's parameter schema, so a document can be
  checked before the engine reads it. See
  [design note 0010](docs/design/0010-node-catalog.md).

- `DonchianChannel(period, excludeCurrent)`: with the option on, the bands
  cover the bars before the current one. With the current bar in the window its
  own high is the upper band, so the classic breakout rule "the close crossed
  above the upper band" could never be true and a strategy written that way
  made no trades. The indicator factory takes `excludeCurrent=true`, and the
  channel names itself `DC(20,excl)` so two of them on one chart are told
  apart. The default is unchanged.

- `Bytex.Documents`: the strategy document format, a strategy as data. A
  document is JSON holding named instruments and bar types, the parameters a
  sweep varies, a typed graph of nodes and edges (`node:port`), phases with
  transitions, and a fired-once switch. Any numeric node parameter may be
  `{"$param": "name"}` instead of a number and stays a reference through a
  round trip, so a document is sweepable. `DocumentJson` carries the
  conventions - camelCase, enums as strings, money and sizes as strings read as
  `decimal`, no nulls written, comments and trailing commas accepted - and
  refuses a document that reads as nothing. `Annotation` carries an event
  (calendar item, venue status, news) with scopes down to one instrument or one
  side of a currency pair, and records who classified it. This step is the
  format itself: the node catalog, the validator and the runtime that executes
  a document follow. See
  [design note 0008](docs/design/0008-strategy-documents.md).

- A node can serve a local control channel: `bytex run --control <name>` opens
  a named pipe on Windows or a Unix socket elsewhere and speaks newline
  delimited JSON. Out: `hello`, `heartbeat`, `event`, `status`, `view`, `bye`.
  In: `status`, `view`, `cancel-all`, `flatten`, `stop` with the same cancel
  and close flags as a graceful shutdown. `view` carries what the node holds
  (accounts, instruments with their last price, positions, orders) and, per
  strategy, whatever it says about itself through the new
  `IStrategyMonitorView`. `NodeControlClient` speaks the protocol so a host
  does not parse it by hand. There is no network listener and nothing on the
  channel carries credentials. See
  [design note 0009](docs/design/0009-node-control-protocol.md).

- KuCoin spot adapter (`Bytex.Adapters.Kucoin`, factory `KUCOIN`): instruments,
  quotes, trades, bars, a 50-level book, history, orders through the venue's
  high-frequency endpoints, its stop-order list, balances and reconciliation,
  plus `catalog fetch-instruments --venue KUCOIN` and
  `verify-keys --venue KUCOIN`. The venue hands out its stream address with a
  token before every connection, so a websocket client can now be given a
  provider for its address instead of a fixed one. An interval without a trade
  is published as a flat bar at the previous close and filled into history the
  same way, because that is what the venue's own history shows once it catches
  up. A stop order cannot be changed at the venue, so modifying one is a cancel
  and a new stop under the engine's own id. See
  [docs/integrations/kucoin.md](docs/integrations/kucoin.md).

- A simulated venue can be started from the state it held. The sandbox
  execution client takes a `restore` block with the open positions and the
  orders that were resting, so a paper node on a derivative can be stopped and
  started again instead of losing its position or refusing to resume. The
  engine adopts the state the way it does from a real venue, by reconciling at
  start, so the strategy that claims the instrument owns it. Balances stay in
  `startingBalances`. A restore the venue cannot honour stops the node from
  starting rather than starting flat. Order status reports carry the tags of
  the order they describe, so a restored exit order still says what placed it,
  and the sandbox client now reports its mass status under its own client and
  account id instead of the inner simulated venue's, so reconciliation matches
  its orders.
  See [docs/integrations/sandbox.md](docs/integrations/sandbox.md).

- A live node can leave evidence and be started again. With `store.directory`
  set it writes an append-only journal of everything it logs, every order,
  position and account event it sees and whatever a strategy publishes about
  itself (`journal/<date>.jsonl`), and saves its strategies and runtimeModules when it
  stops so a node started again over the same directory reads their state back
  (`state/<id>.json`). Journal writing never blocks the tradingRuntime thread: records
  are queued and written by one background writer, and a queue that overflows
  counts what it dropped rather than stalling the node. Files older than
  `store.journalDays` are removed at start. A node without the setting keeps
  nothing, as before.

- `bytex verify-keys` asks the venue what an API key may do before a live node
  starts: accepted or not, trading, withdrawal, IP restriction, markets and
  expiry. It only reads, never prints the key, and with `--json` reports facts
  and a failure code for other programs to build on (#19).

- `catalog fetch-instruments` takes `--base-url`, the option `verify-keys`
  already had, so the command can be pointed at a proxy or at a test venue
  instead of at the exchange.

### Changed

- The documentation says what the engine does, checked row by row against the
  requirements catalog. The catalog no longer claims an API reference that was
  never written, and describes the node catalog's export as what it is: JSON
  for a palette and a JSON Schema for a whole document. The README gains a
  strategy-documents row, a tooling row that names the document commands, the
  key check and the control channel, and the test count it actually runs. The
  release table and the roadmap mark Strategy Documents as delivered. The
  KuCoin page names the sandbox configuration that ships for it, which nothing
  pointed at before.

- What ships beside the library is tested like the library. The Redis state
  store is exercised through an in-memory Redis: the key every entity lands
  under, the index that finds it again, an update that appends only the events
  the server does not have, and an order, a position, an account, runtimeModule state
  and a general value rebuilt from what was stored - no server needed, since
  the server is the one part of that picture that is not this engine's code.
  The package settings, the container image, the example configurations and the
  notebook are read by tests and held to the repository they describe, so a
  renamed project or a moved file cannot leave them quietly broken. The control
  channel's `flatten` closes positions as well as cancelling orders, and says
  so in a test. A node type that is not built in - a plugin's, a venue
  integration's, a private library's - is validated, exported, schema-checked
  and evaluated on the same terms as a built-in one, and a described type with
  no runtime in this process is still named as such before a run starts.
  `catalog fetch-instruments` has the path that works under test, not only its
  refusals. A component's log line is attributed to that component and carries
  its id as a value a structured sink can index, not only as words in a
  sentence.

- Every remaining numeric literal that encodes a limit, a convention or a
  policy is now a named constant where the architecture puts that kind of fact,
  as the adapters' venue limits already were. `Scales` holds the percent, basis
  point and trading-day conversions; `UnixNanos` holds the nanoseconds in a tick
  and the days a bar type's month means; each indicator holds the period it is
  conventionally used with, and `IndicatorFactory` reads it instead of keeping a
  second copy that could disagree; the backtest engine names its book-matching
  priority and the fill model its default seed; the live network layer names its
  retry delays, its jitter, its timeouts, the status a venue sends instead of
  429, and how much of a message a log line carries; the control channel names
  its outbox size, the node store its queue size, and the CLI the venue codes it
  reads rather than only reports. Left as they are, deliberately: enum member
  values, ISO currency data, bar-interval and reason-code tables, node parameter
  specs, and defaults that already sit beside a named setting - in each of those
  the number is the data.

- Three example documents ship with the engine, and `bytex documents examples
  --out <dir>` writes them out: `ema-cross` (the document twin of the C#
  `EmaCross` example), `breakout-retest` and `support-bounce`.
  `examples/configs/backtest-document.json` backtests the first of them, so a
  reader goes from a clean checkout to a document trading over catalog data in
  two commands and no C#. Each example is held to the same bar as any other
  document: it validates with nothing to report, every node outside a phase is
  evaluated on the last bar, it round-trips through `DocumentJson` byte for
  byte, and two runs over the same bars end on the same fingerprint of orders,
  fills and equity - determinism is the engine's promise and a document is a
  strategy like any other. The runtime tests that go with them cover a
  fired-once document stopping after its first position closes, provider
  creation from an inline document and from a path, parameter overrides
  changing the outcome, and runtime state surviving a save and load.

### Fixed

- Bybit books only trades as fills. The private `execution` topic carries more
  than trades: a funding payment, a delivery, a settlement and a position
  transfer arrive there too, each with an `execQty` that is the position's size
  rather than a traded amount. Every row became an `OrderFilled`, so a held
  perpetual position was filled again at every funding interval - doubling the
  position the engine believed it had, at a price that was not a trade's, and
  drifting further with each payment. The `execution/list` history the
  reconciler reads had the same fault, which meant reconciliation confirmed the
  phantom rather than correcting it. Trade rows are `Trade`, `AdlTrade`,
  `BustTrade` and `BlockTrade` - a liquidation and an auto-deleverage are real
  trades and still book - plus a row with no `execType`, which is what spot
  sends. Anything else is logged and ignored; a type the venue adds later is
  ignored until it is listed, losing a fill that reconciliation then finds
  rather than inventing one that nothing corrects.

- A trading node whose start fails part-way disconnects the clients that had
  already connected. `StartAsync` threw and left the node not running, and both
  `StopAsync` and `DisposeAsync` returned immediately because a node that never
  finished starting was never running - so a node whose second venue timed out
  kept the first one's socket open for the life of the process, receiving data
  nothing read. The clients that connected are now disconnected in reverse
  order, the heartbeat is cancelled, the original failure is rethrown, and the
  node can be started again once the venue is reachable. Disposing a node that
  never ran now ends its tradingRuntime thread as well.

- `AverageDirectionalIndex` is seeded the way Wilder defined it: the mean of
  the first N real DX values, which is why the first ADX appears on bar 2N-1.
  It averaged in DX values taken from directional-movement sums that were still
  filling, so the number never converged on the textbook one and an ADX
  threshold meant something slightly different here than in every chart package.

- An indicator whose value is built from other averages reports itself
  initialized only once that value is real. `HullMovingAverage` said so after N
  inputs while its composite was still smoothing partial-window averages, and
  `Stochastics` said so after its %K window filled while %D was still averaging
  %K values measured over windows that had not. Both now feed the inner average
  only with complete inputs, so a value that reads as initialized equals the
  textbook value.

- `AverageDirectionalIndex`, `AroonOscillator` and `RateOfChange` refuse a
  period below one, where they used to accept zero and then divide by it, or
  quietly report a constant. `Stochastics` refuses either of its periods and
  names the one that was wrong. Every indicator that takes a period is now
  swept by a test, so a new one cannot be added without the check.

- Binance bar history covers the whole window it is asked for. The venue
  answers a request that carries a start time from that time forwards, but the
  fetch paged backwards from the end time, read the first page as the newest
  one, saw its first bar at or before the start and stopped - so a window
  longer than one page came back with its first 1,000 bars and nothing else,
  which a catalog download then stored as the whole period. A window is now
  walked forwards page by page until the venue runs out or the limit is
  reached, and a request with no start still pages backwards as before.

- Every limit the adapters work to is named where that venue's facts live, and
  no longer written as a number in the middle of a call. `1000` alone appeared
  eight times across the Binance and Bybit clients carrying three different
  meanings, which is how the paging defect above was able to hide. Now:
  `KlinePage` per account type (1,000 spot, 1,500 futures - the futures cap was
  understated by a third), `TradePage`, `MaxBookDepth`, `DefaultBookDepth`,
  `BookStreamInterval`, `RequestWeightPerMinute`, a `Weights` table holding the
  weight the venue charges for each endpoint this adapter calls,
  `ListenKeyKeepAlive`, the trailing callback-rate bounds and
  `DefaultRecvWindowMs` on `BinanceVenue`; `KlinePage`, `TradePage`,
  `RequestsPerWindow`, `RequestWindow`, the book depth tiers, `AuthExpiry`,
  `PingInterval` and `DefaultRecvWindowMs` on `BybitVenue`; `CandlePage`,
  `RequestsPerWindow`, `RequestWindow`, `DefaultPingInterval`, `NoSizeLimit`
  and `MaxClientOrderIdLength` on `KucoinVenue`; `RequestsPerWindow`,
  `RequestWindow` and `DownloadTimeout` on the Tardis client. `LogText` holds
  how much of a venue message a log line carries, `UnixNanos.NanosPerTick` is
  public for the adapters that convert ticks to nanoseconds, and `Scales` holds
  the percent and basis-point conversions.

- Bybit spot instruments carry their minimum order value. Spot reports it as
  `minOrderAmt` and the derivative categories as `minNotionalValue`, and only
  the latter was read, so every spot instrument came through with no
  `MinNotional` and an order below the venue's minimum was rejected by the
  venue instead of by the pre-trade check. Derivatives keep reading the field
  they report it under, which a test now holds them to.

- `UnixNanos.Parse` reads all nine fractional digits exactly. A stamp such as
  `2024-01-01T00:00:00.123456789Z` came back 100 ns in the future: the text was
  handed to the date parser whole, which rounded the seventh digit up, and the
  last two digits were then added on top of the rounded value. The fraction is
  now cut to seven digits before parsing and the remainder added, so a
  nanosecond stamp survives a round trip through parsing and formatting
  unchanged, and anything below a nanosecond is cut rather than rounded.

- An instant before the epoch reads as the time it is. Division towards zero
  named the tick after such an instant, so one nanosecond before 1970 formatted
  as `1970-01-01T00:00:00.0000000-01Z` - a negative remainder pasted onto a
  date - and `ToDateTimeOffset` put it in 1970. Both now floor to the tick the
  instant falls in: `1969-12-31T23:59:59.999999999Z`.

- `UnixNanos.TryParse` answers false for a date the 64-bit nanosecond range
  cannot hold (it ends in 2262) instead of letting the overflow escape, and
  `CandleSeries.TryParse` answers false for a step of more digits than an `int`
  holds. `MarketKey.TryParse` answers false when the symbol or the venue
  part is blank, where it used to throw. A `TryParse` that throws is worse than
  no `TryParse`: every caller has to wrap it anyway.

- Every order type that can rest refuses `Gtd` without an expire time. Only
  `LimitOrder` enforced it, so a stop, a stop-limit, a market-if-touched, a
  limit-if-touched, either trailing stop or a market-to-limit order could be
  created as "good until" no time at all, and then rested forever. The rule now
  lives in the order base class, where no type can forget it; a market order
  still refuses `Gtd` outright for its own reason.

- An L1 order book fed a multi-level depth keeps the best level of each side.
  Every level was added in turn and each add clears the side of an L1 book, so
  the book ended up quoting whichever level came last - the worst bid and the
  worst ask of the snapshot.

- `OrderBook.SimulateFills` for a quantity of zero returns no fills, instead of
  one fill of zero size at the best price.

- An order a strategy cancels while its submission is still on its way to a
  venue is no longer cancelled behind the venue's back. The engine cancelled it
  locally - the order had not reached the venue, so there was nothing there to
  cancel - and the venue then accepted and filled it, leaving the engine
  reporting a cancelled order and the account holding a position. The engine
  now keeps such an order until the venue answers for it and sends the cancel
  there; if the cancel overtakes the order and is refused because the venue
  holds nothing under that id yet, it is sent again the moment the order is
  accepted. A venue still does not un-fill an order because a cancel was sent
  too late.

- `Day` time in force in a backtest. A day order rested like a GTC one and
  could fill days or weeks later; it now expires when the UTC date rolls over.

- The activation price of a trailing stop in a backtest. It was never read, so
  the stop trailed and could fire before the market ever reached the price that
  was supposed to wake it. A trailing stop with an activation price is now
  dormant until the market reaches it, and then trails from there.

- A dormant OTO child is accepted once. It was announced as accepted when the
  list arrived and then worked as a fresh order when its parent filled, which
  gave it a second venue order id its owner never heard of - so a cancel or a
  status report by venue id was about an order neither side could name. It now
  starts working under the id it was given.

- A simulated venue puts an order in its book before announcing that it was
  accepted, so a command its owner sends on that news finds the order there.

- Backtesting on trade ticks: both sides of the synthetic book follow the last
  print. Each side was dragged outwards and never back, so after a few prints
  the bid was the lowest price of the whole run and the ask the highest, and a
  market order paid or received a price from minutes or days ago. Beside real
  quotes a print outside the spread still drags the side it went through, until
  the venue quotes again.

- Backtesting on trade ticks: a trade that prints through a resting limit order
  fills it, as maker at its own price. Somebody paying more than a resting
  offer, or selling for less than a resting bid, is a trade that would have
  taken it; with real quotes the print only moved the far side of the book,
  which a resting order never looks at, so such an order sat there while the
  market traded past it. A print exactly at the order's price is a touch and
  the fill model decides, as on a quote.

- Backtest statistics: a day that closed no position is a zero-return day in
  the daily return series instead of being left out of it. Both ratios are
  annualised with `sqrt(252)`, so dropping idle days made an idle stretch look
  like a run of trading days and flattered Sharpe and Sortino (1.3093 where
  1.2011 was right, on a series with one idle day).

- Backtest statistics: the max drawdown counts from the balance the run
  started with. Its first peak was the first point of the equity curve, which
  is already net of the first fill's fee, so a run that only lost money showed
  a drawdown short by that fee - and a run whose first fill was its worst
  moment showed none at all.

- Backtest statistics: a commission is reported under the currency it was paid
  in. A fee charged in a currency no position settled in was counted nowhere,
  and a currency that only ever paid fees was missing from the per-currency
  statistics entirely.

- A simulated cash account holds the funds its resting orders will need. It
  locked nothing, so one balance backed any number of orders: ten resting buys
  for the whole balance all filled and left it deeply negative. The venue now
  holds the quote currency a buy will spend and the base currency a sell will
  deliver from acceptance until the order fills or closes, follows an
  amendment, and publishes the account so `Locked` and `Free` are true while
  orders rest. An order the free balance cannot cover is rejected with
  `insufficient balance`, which also now applies to the orders of a list -
  submitting a list bypassed the check completely.

- The affordability check on a cash account counts the commission. A buy for
  the whole balance was accepted and then charged its fee, which took the
  balance below zero.

- A simulated venue with a cash account refuses a perpetual or a future
  instead of booking it as spot. A cash account settles a fill by exchanging
  two currencies, so a derivative on one traded with no margin, no leverage
  and a short that sold a base currency the account never held: the backtest
  reported trades no venue would have taken. The venue now says so when the
  instrument is added, whichever of the two was configured first, and names
  the way out (`accountType: margin`).

- Execution engine: an event the venue sends under a client order id of its
  own is applied to the order it belongs to, which the engine already found by
  venue order id; applying it as it arrived threw on the mismatch and took the
  engine's handler down with it. An event the order refuses is no longer
  published either, so a strategy is not told that its cancelled order was
  accepted.

- Control channel: a command that arrives while the node is writing a
  heartbeat or an event no longer takes the session down. The command handler
  wrote its reply to the same writer the streaming task was using, and two
  overlapping writes to a StreamWriter are an error; every message the node
  sends now goes through the one outbox.


- An order that states its size in the quote currency is honoured as one. The
  risk engine costs it as the amount of money it names instead of multiplying
  it by a price, and judges it against the instrument's notional limits rather
  than its size rules, so a 1,000 USDT buy no longer looks like a demand for
  fifty million. The simulated venue converts the amount to a base quantity at
  the price it fills at, rounded down to the size step so it never spends more
  than it named, and rejects it when the amount cannot buy one step.

- Data: records that share a timestamp keep the order they were written in,
  both from the catalog and from the CSV loaders, so a book snapshot replays as
  the snapshot it was instead of a shuffle of its deltas. A catalog directory
  name is now percent-encoded rather than flattened to underscores, so an
  instrument whose id contains a slash, a backslash or a colon is listed under
  its real id and no longer shares a directory with a different instrument. A
  bar's revision flag is persisted, so a revision does not come back as a final
  bar.

- A node's journal keeps its tail. The records a node wrote while stopping,
  the stop itself among them, could still be in the writer's queue when the
  process ended, because nothing flushed the store and the node never disposed
  it. A node now waits for the journal to reach the disk before it reports
  itself stopped, and disposing it stops the writer.

- Backtest: the same configuration runs the same way twice again. `Reset()`
  reseeds the venue's fill model and starts each strategy's client order id
  sequence over; a streaming `Run()` continues where the last one stopped
  instead of replaying the batches it already dispatched; the venue matches an
  order book update before the strategies are told about it, as it already did
  for quotes, trades and bars; and a command delayed by the latency model is
  carried out at its own due time rather than at the time of the next data
  event, so its acknowledgement and its fill carry the timestamps they would
  really have had.

- Strategy SDK: a GTD order that is partially filled still expires its
  remainder; a strategy with an `orderIdTag` receives the events of its own
  orders, because the tag now shapes the client order ids while the orders
  stay the strategy's own; and a base configuration no longer overwrites what
  a strategy payload set, so `manageGtdExpiry: true` stays true.

- Backtest: a reduce-only order can no longer open a position. The flag was
  checked when the order arrived and never again, so a stop or target resting
  while something else reduced the position still filled its whole size: it
  closed what was left and opened the opposite side with the rest. It now
  closes at most what the position holds and the remainder is cancelled, and
  an order that has nothing left to close is cancelled instead of filled.

- Risk engine: a price or quantity that is not a multiple of the instrument's
  tick or step is denied, which R4.1 already claimed; a currency the account
  has no balance entry for counts as zero instead of unlimited, so a sell of
  something the account never held is denied instead of being forwarded to the
  venue; and an order list is judged against the trading state like a single
  order, so a bracket can no longer open a position while the node is in
  reduce-only state.

- Backtest: what happens inside a bar is no longer optimistic. The OHLC path
  visits the nearer extreme first, as the documentation always said, instead
  of ordering the two by the candle's direction, which decided wrongly which
  of a stop and a target filled first. A stop or an if-touched order that the
  price crosses inside a bar fills at its trigger instead of at the extreme the
  bar reached, and an order at the opening or the closing auction keeps its
  limit: it fills only at or inside it, and expires otherwise, instead of
  filling at the auction price. Results of existing backtests change, which is
  the point: they were biased in the strategy's favour.

- Data engine: a historical request that fails in the client is answered with
  the reason instead of only being logged, so the runtimeModule that asked stops
  waiting for an answer that would never come.

- Live clock: an alert or an immediate timer whose time has already passed is
  no longer fired by the thread that sets it. A strategy that set one while
  starting dropped its own alert, because an runtimeModule ignores messages until it
  is running. A caller that relied on the firing happening before
  `SetTimeAlert` returned no longer can: the first firing always reaches the
  dispatcher afterwards.

- Binance and Bybit clients report themselves connected again after a socket
  reconnects by itself. One dropped socket marked a client disconnected and
  nothing ever cleared it, so a supervisor showed "market data disconnected"
  on a node whose prices were moving, and an execution client looked unable
  to reach the venue while it was placing orders. On Binance futures both
  stream routes have to be up before the client counts as connected.

- Execution engine: a position the venue reports and the engine does not hold
  is adopted instead of only logged as a mismatch. The difference is booked
  as a fill at the venue's average price, under an order of its own, owned by
  the strategy that claims the instrument. A node restarted after the fill
  that opened its position had left the reconciliation window used to come up
  believing it was flat. A position with no price anywhere, or on an unknown
  instrument, is still refused rather than invented.

- Execution engine: a fill the venue delivers twice is applied and published
  once, also when it flipped a netting position and was booked as two parts
  (the position used to be counted twice). Under hedging, an order submitted
  against a position id, as `ClosePosition` does, now acts on that position
  instead of opening another one (#16).
- Binance and Bybit: a dated futures contract (`BTCUSDT_250926`,
  `BTCUSDT-26SEP25`) was given a perpetual's id ending in `-PERP`, so its
  stream data, open orders and positions matched no instrument and were
  dropped. Dated contracts now keep their own name (#15).
- HTTP retries: a request with a body (POST, PUT, DELETE) was never actually
  retried on a 5xx or 429 answer, because the first attempt disposed the
  content. Every attempt now gets a content of its own, so order, cancel and
  amend calls are resent intact (#13).
- Binance USD-M futures: klines, mark prices and aggregated trades were never
  delivered. The venue serves them on a separate `/market` route; the data
  client now opens the `/public` and `/market` routes and sends each
  subscription to the one that serves it (#7).
- Binance spot: a cancel confirmation was applied to the id of the cancel
  request instead of the order it cancelled, so the order stayed open in the
  engine. Binance futures: a `TAKE_PROFIT` order, the limit take-profit, was
  reported back as a market one (#14).

### Changed

- The requirements catalog now marks as Roadmap what the engine does not do
  yet: increment checks in the risk engine (R4.13), partial fills (R8.24),
  extra bar execution modes (R8.25), DAY expiry in simulation (R8.26),
  streaming catalog reads (R9.7), the catalog `info` command (R9.8), and
  headerless or quoted CSV (R9.9).

## [0.1.0] - 2026-09-19

The first tagged release of the engine.

### Included

- Event-driven tradingRuntime, domain model, strategy SDK, risk and execution engines
  (`Bytex.Core`).
- Deterministic backtesting with a simulated venue (`Bytex.Backtest`).
- Live and sandbox trading nodes (`Bytex.Live`).
- Venue adapters for Binance and Bybit, and the Tardis data adapter.
- Parquet data catalog and CSV loaders (`Bytex.Data`), indicators
  (`Bytex.Indicators`), Redis persistence, and the `bytex` command line.

### Added

- `bytex run --env-file <path>` loads venue credentials inside the node
  process, so the process that launches a node never reads them (#5).
- Engine test suite: seven test projects run on Linux, Windows and macOS on
  every commit (#8). Tests marked `BUG` are skipped on purpose: each holds the
  correct expectation for a known defect and is enabled by the fix.

### Changed

- `Bytex.Live.ShutdownHelper` is public, so a host that embeds `TradingNode`
  can run the node's own cancel-and-flatten sequence (#3).
- Line endings are normalized through `.gitattributes` (#1).

[Unreleased]: https://github.com/BYTEX-TRADE/bytex/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.6.0
[0.5.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.5.0
[0.4.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.4.0
[0.3.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.3.0
[0.2.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.2.0
[0.1.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.1.0
