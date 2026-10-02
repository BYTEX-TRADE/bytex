# Versioning and compatibility

What a version number here promises, what it does not, and how a promise is kept
by something other than good intentions.

## The numbers

Versions follow [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

| Until 1.0 | From 1.0 |
|---|---|
| A **minor** may change or remove a public API. Every change is in the [changelog](../CHANGELOG.md). | A **minor** only adds. Nothing public changes shape or disappears. |
| A **patch** only fixes. | A **patch** only fixes. |
| — | A **major** is the only release that may remove or change a public API. |

Fixes do not wait for a milestone; they ship as patch releases.

## What "public API" means, exactly

Not "what the documentation mentions" and not "what somebody thought was
internal". It is the **exported surface of every packable library**, written down
under [`api/`](../api) — one file per library, every public and protected type and
member, as the assembly presents it.

```
class Bytex.Core.Model.Primitives.Price : System.IComparable<...>, System.IEquatable<...>
    .ctor(System.Decimal value, System.Byte precision)
    Add(Bytex.Core.Model.Primitives.Price other) -> Bytex.Core.Model.Primitives.Price
    ...
```

Those files are not documentation that can go stale. A test (`PublicSurfaceTests`)
reads the built assemblies on every run and fails when what they export is not
what the file says. So:

- **Before 1.0**, a change to an API fails that test until the surface file is
  updated in the same commit. The failure prints what went and what arrived,
  which is what a changelog entry has to describe.
- **From 1.0**, the same test is the freeze. A member that disappears or changes
  shape cannot reach a release without somebody deciding that release is a major.

A member being in a surface file is what makes it public API. A member that is
not there is not one, whatever its accessibility looked like in the source.

## Enums are open, and a `switch` over one needs a default

A public enum in this engine is **extensible**: a minor may add a member. New
instrument classes, asset classes and order kinds arrive as venues and asset
classes are added, and holding them all for a major would mean a major for every
venue that quotes something new.

So the promise is narrower than it looks, and this is the part that matters to
anybody building on the engine: **the set of members is not frozen, only the
existing ones are.** A value already defined keeps its name and its number. A
`switch` over one of these enums must have a `default` arm and treat a value it
does not know as unknown - not as an error, and not by falling through to
whichever case happens to be last.

The freeze test cannot catch this for you. A new member is an addition, so
`PublicSurfaceTests` passes, and the code that breaks is in somebody else's
repository. Which is exactly why it is written down here instead.

The engine holds itself to the same rule: where it maps a venue's own product
type onto one of these enums it goes through a `default` arm rather than
assuming the set is closed.

## Four contracts, four version numbers

The library surface is not the only thing this engine promises, and the others
carry their own numbers because they change for their own reasons and are read by
programs that were not compiled against the libraries at all.

| Contract | Its version | What it is |
|---|---|---|
| Libraries | the package version | the surface under `api/` |
| Node control protocol | `hello.version` (6) | the messages a host and a node exchange; see [design note 0009](design/0009-node-control-protocol.md) |
| Node catalog | `NodeCatalog.CatalogVersion` (1.0) | the node types a strategy document may use; see [design note 0010](design/0010-node-catalog.md) |
| Strategy document | the document's `schema` | the shape of a saved strategy; see [design note 0008](design/0008-strategy-documents.md) |

A host reads the protocol version from `hello` before it relies on anything in the
protocol, and additions to it are new message types or new payload fields - never
a changed meaning for an existing one.

## Things that are deliberately not frozen

Said here rather than left to be discovered:

- **What a Redis stream carries.** The record a node publishes its bus messages as
  (`BusRecord`) is documented as unstable until 1.0 in the type itself. Anything
  reading those streams pins that shape, and freezing it before the engine's own
  types are frozen would be promising on their behalf.
- **Internal types**, including everything visible only to this repository's own
  tests. `InternalsVisibleTo` is not a promise to anybody outside it.
- **The numbers a backtest produces.** A fix to the simulator changes results, and
  a result is a measurement rather than an API. Every such change is in the
  changelog with what it means for a saved result, because a comparison against
  one made before the fix is comparing two different simulators.
- **Log message text**, which is for people rather than for parsers.

## How long a version is supported

**While the line is 0.x**, there is one supported version: the latest minor.
Nothing is back-ported, because a minor may change an API and a fix on top of an
older one would be a fix for a different engine. A release you are on is supported
for as long as it is the latest; after that, the way to a fix is the next minor,
and the [changelog](../CHANGELOG.md) says what changed on the way there.

**From 1.0:**

| | Ordinary fixes | Security fixes |
|---|---|---|
| The current major | yes, as patch releases | yes |
| The previous major | no | yes, until six months after the next major shipped |
| Anything older | no | no |

Six months is deliberately a duration rather than a number of releases: what a
person planning an upgrade needs is a date they can put in a calendar, and "two
minors" is not one.

What a security fix on a previous major is: the smallest change that closes the
hole, as a patch release, with no other change carried along. A fix that cannot be
made without a breaking change is said so plainly in the advisory, and the upgrade
to the current major is the answer - a repository that pretends otherwise ships a
patch that breaks the build of everybody who applied it.

**What being supported does not mean.** A feature added to the current major is not
back-ported. Neither is a correctness fix that changes what a backtest produces:
those change measurements, and applying one to a previous major would silently
change results for people who are on it precisely because they want the numbers
they already have. Both are in the changelog of the release that has them.

Every one of these dates is knowable from the releases: there is no separate list
to keep, and no version is "supported" because somebody remembered to say so.

## How something leaves

1. It is marked `[Obsolete]` with what to use instead, in a minor release. The
   surface file records it as obsolete, so the deprecation is itself part of the
   written surface.
2. It keeps working for the rest of that major's life.
3. It is removed in the next major, and the removal is in the changelog.

Before 1.0 step 2 is shortened: a minor may remove what a previous minor
deprecated, because a minor may break. The changelog says so each time.
