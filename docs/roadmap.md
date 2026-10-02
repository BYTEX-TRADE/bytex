# Roadmap

The releases ahead, in order. No dates: a release ships when it is ready, and
the order may change. Fixes do not wait for a milestone; they ship
continuously as patch releases (see the [changelog](../CHANGELOG.md)).

## 0.1 · Foundation ✅

Released. Deterministic tradingRuntime · backtesting · live and sandbox trading ·
Binance · Bybit · test suite on three operating systems

## 0.2 · Strategy Documents ✅

Released. Strategies as data · node catalog · three-layer validator with reason
codes · deterministic runtime · example documents and `bytex documents` · node
control channel

## 0.3 · Risk ✅

Released. Loss and exposure limits and caps on positions and working orders ·
margin checks for margin accounts · one switch that halts a node's trading and
leaves it running · limits set from node configuration, the control channel or
the command line · a moving price for whatever watches a node · the node
catalog with grid and safety-order actions and per-level state

## 0.4 · Execution ✅

Released. Order emulation: stop and if-touched orders triggered in the engine
for venues that have none · execution algorithms with `TwapSchedule` ·
continuous reconciliation while a node runs · batch runs and parameter sweeps
from one description, compared in one table · a loss limit that counts what open
positions are down, watches it between fills and stops the node when it is
reached · every choice of a node parameter saying what it means, and an
arithmetic node

## 0.5 · Realism ✅

Released. Fills bounded by the size on offer, with what is left going on
working or given up as the order says · funding payments on perpetuals ·
liquidation of margin accounts by the venue · a per-contract fee model ·
published reference backtests run on every commit

## 0.6 · Depth ✅

Released. An order that takes walks the book and pays what each level costs ·
an order that rests waits its turn in the queue at its price, and says where it
stands · a paper venue matched against the book its real venue is streaming,
and saying whether a fill came from a book, a quote or a bar · a margin account
holding the margin its working orders need · leverage that a strategy states
for itself and that changes the size it can take

## 0.7 · Venues ✅

Released. OKX · Kraken · Bitget · Gate · Hyperliquid · KuCoin Futures · Bybit inverse
contracts and options and Binance coin-margined futures, so that the venues
already here arrive with their full capabilities too · a venue declaring its own
facts per product family, readable without a client or a key · each venue's own
margin requirement and maximum leverage read from that venue instead of assumed,
and a result that says which of those it used · a broker id on every order each
adapter sends, delivered with the adapters rather than retrofitted into them

## 0.8 · Resumption and large data ✅

Released inside 0.9.0: the two milestones shipped together, because everything in both was
finished before either was cut. A node that is killed comes back the same as one that was never interrupted - what survives a
crash is what survives a polite stop, with strategies' own memory saved while a node runs rather
than only when it is asked to stop, schedules that keep their phase across a restart, and a bar
published only when the whole of its window was watched · reading the catalog without holding a
period in memory, and backtests that run in chunks over data larger than it · what the catalog
holds, said plainly: ranges, row counts and size · consolidation, de-duplication and integrity

## 0.9 · Markets and state ✅

Released. Databento · full Redis cache state and bus messages published to Redis streams ·
cloud object storage for the catalog · adding, starting and stopping strategies while a node
runs · margin models that can be chosen rather than assumed · position snapshots and pluggable
statistics · a wider indicator set · information-driven bars · the bar path walked in the
instrument's own increment, and a DAY order expiring at its venue's session end · node types a
plugin brings · a run as one self-contained page · a document evaluated on ticks as well as at
the bar close · and the 1.0 paperwork that was ready early: the public surface written down and
held there by a test, published benchmark numbers, and a support policy

0.9.1 added the check that keeps the rest honest: every adapter test runs against a recording,
which cannot notice a venue renaming a field, so the recordings are held against the venues' own
live endpoints once per release - opt-in, public data only, no key and no orders.

## 0.10 · State, strictness and book depth ✅

Released. A node's state in PostgreSQL, for a deployment that will not run Redis - one key
layout shared by every backing store, and a conformance suite each of them answers the same ·
the last bar of a window checked against the venues themselves

**And a backtest can be fed a real order book.** The matcher has walked one since 0.6 and
nothing could reach it, because no source a person could use published depth - the free archives
the venues publish carry top of book or a band metric, so producing levels from either would mean
inventing prices. What unblocked it was a vendor giving the first day of every month away with no
key at all. A loader reads those snapshots, the catalog has somewhere to keep them, and a run can
name that data set, so depth is read once instead of fetched again on every execution.

Storing it is most of the work. An instrument-day of 25-level snapshots is 1.5 million of them at
about 3,011 bytes each: 4.3 GiB, which nothing could open as one file. So a write fills a file to
a bounded number of records and starts another, each named for the range it holds - sixteen files
for that day - and consolidating obeys the same bound rather than merging them back. Two order
books with the same levels are also the same book now, which the record equality the compiler
generates had got wrong for the only two market-data types not made of value types.

Also here: the seconds since a position last closed, so a re-entry rule can wait a real interval
rather than a number of candles · the versioning policy saying plainly that public enums are open,
a minor may add a member, and a `switch` over one needs a default arm · and a decision recorded
before the freeze about where a spread's legs may appear ([design note 0013](design/0013-multi-leg-instruments.md)).

And five things the engine accepted while quietly doing something else, none of which failed
anything: an execution reported against a strategy that does not exist, a window one bar short
at its newest end, a document member misspelled and ignored, a node parameter the catalog does
not define passed off as a warning, and thirteen settings read from a payload and discarded.
They returned a number that looked like a number, which is the only kind of defect that
survives a green test suite.

## 1.0 · Stable

Verification against live venues - the last frontier, and deliberately the only thing left ·
the API freeze taking effect, the benchmarks and the support policy having shipped in 0.9.0 ·
the recordings checked against the venues having shipped in 0.9.1 · documentation and paperwork

## Out of scope

- A graphical user interface. BYTEX is an engine and SDK; interfaces are built
  on top of it.
- Market data distribution. Users connect their own data providers and venue
  accounts.
- Distributed execution across processes.
