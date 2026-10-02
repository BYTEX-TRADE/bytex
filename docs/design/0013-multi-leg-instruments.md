# 0013 - Multi-leg instruments

Status: accepted

## Problem

Option spreads and futures spreads are on the roadmap after 1.0, and 1.0 freezes
the public surface: from then on a minor may only add, and changing the shape of
something that already exists takes a major.

A spread has legs. Where those legs are allowed to appear decides whether adding
spreads later is an addition or a breaking change, and the two answers are not
equally reversible. If legs have to be visible to `Order` or `Position` - an
order carrying several instruments, a position keyed by more than one - then the
shapes that would have to change are the ones the freeze holds still, and the
work becomes a major version years after the decision was implicitly taken by
not taking it.

So this note is written before 1.0 on purpose. It decides nothing about how
spreads are priced, matched or margined, and it does not ask for any of it to be
built now. It decides only where a leg may appear, and by what route it may
appear later, because that is the part 1.0 makes expensive to get wrong.

## Scope

Any instrument whose economics are defined by two or more other instruments:
option spreads, futures spreads, and calendar and ratio combinations of either.

Out of scope: synthetic instruments defined by a formula over components rather
than by legs a venue quotes. That is a different thing with a different answer
and it gets its own note when it is built.

## Decision

**A spread is an instrument in its own right, with its own `MarketKey`.**

- It is a `sealed` subclass of `Instrument`, like every other instrument type.
  `Instrument` has a `protected` constructor and no abstract members, so a new
  subclass is purely additive.
- Its legs are described **on the instrument**, for reference, reporting and
  margin. They are data about what the instrument is.
- **Legs are never visible to `Order` or `Position`.** An order names one
  `MarketKey` and that stays true. A position is held against one
  `MarketKey` and that stays true. A filled spread is a position in the
  spread, not positions in its legs.

This is also how venues themselves present spreads: the exchange lists the
combination as a tradable symbol with its own book, accepts an order against
that symbol, and reports a fill against it. Modelling legs inside an order would
mean modelling something no venue asks for.

## Consequences

**Spreads become an additive change.** A new instrument subclass, a leg
descriptor type, and adapter support per venue. No frozen shape moves, so this
can ship in a minor after 1.0.

**A spread's P&L is the spread's.** Anything wanting per-leg attribution derives
it outside the position model, from the leg descriptors and the leg instruments'
own prices. The engine does not maintain leg positions, and a report that wants
them computes them.

**Leg-level risk checks are not free.** Pre-trade validation sees one instrument,
so a check that needs to reason about a leg reads the leg descriptors
deliberately rather than getting it for nothing. That is the price of keeping
orders single-instrument, and it is worth it.

## If legs do have to become visible, here is how, without a major

A venue may yet require it. The likely one is Interactive Brokers, where a combo
is a contract *defined by the legs in the order request* rather than a symbol the
exchange listed beforehand, and executions come back **per leg**. That venue is
not written yet, so this is a thing to confirm when it is scoped rather than a
thing to build for now.

It does not force a major, and this is the part worth writing down, because the
additive route is narrow:

- **`Legs` arrives as a property in the record BODY**, on `OrderInitialized` and
  `OrderFilled` - never as a positional parameter. Both are positional records,
  so the surface file carries a `.ctor(...)` and a `Deconstruct(...)` for each; a
  new positional parameter changes both and is a shape change. A body property
  leaves both untouched and is an addition.
- **`Order` and `Position` gain a get-only `Legs`**, defaulting to nothing. Both
  are classes with no abstract members to widen, so that is an addition too.
- **`Order.MarketKey` stays a single id, and `Position` stays keyed by a single
  id.** This is the invariant that has to hold. An order naming several
  instruments, or a position keyed by more than one, is the breaking version -
  and no venue needs it, IB included, because a combo still has one contract id
  of its own.

So the decision here is not a deadline. It is the rule that keeps the later work
additive whenever it happens.

**What would overturn it.** A venue that accepts an order against several
instruments with no combination identifier at all, so that there is nothing for
`MarketKey` to hold. Then the answer is a superseding note and a major - not a
quiet widening of `Order`.

## Alternatives rejected

**Legs on the order.** An order carrying a list of instruments and quantities.
Rejected: it changes the shape of the most-used type in the engine, it would
have to be a major, and every consumer of `Order` would have to learn that an
order may now name more than one instrument - including code that has no
interest in spreads.

**Legs as separate positions.** A spread fill opening a position per leg.
Rejected: it makes a spread indistinguishable from having traded the legs
separately, which loses the thing a spread is - the combination was quoted,
filled and margined as one unit, often at a price no individual leg traded at.
Reconciliation against a venue that reports one position would then disagree
with the engine on principle rather than by accident.

**Deciding later.** Rejected because "later" is after the freeze, and the cost of
the decision is the point of writing this now.
