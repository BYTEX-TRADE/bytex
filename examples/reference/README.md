# Reference backtests

Published configurations with the numbers they produce. `ReferenceBacktestTests`
runs every file here on every commit and compares what came out with what is
written down, to eight decimal places.

Each file is one run: a shipped example document, the seeded random walk it runs
on, the account it starts with, and what the engine answered.

```json
{
  "document": "ema-cross",
  "bars": 3000,
  "seed": 7,
  "startPrice": "50000",
  "startingBalances": [ "1000000 USDT", "10 BTC" ],
  "currency": "USDT",
  "expect": { "fills": 103, "positions": 52, "...": "..." }
}
```

The data is generated rather than stored: the same seed gives the same three
thousand bars on every machine, so a reference needs no data files and cannot
drift because a catalog changed. `simulation` is what the simulator that
produced the numbers models, so a reference says on its face whether its numbers
came from a run that bounds fills by the size on offer.

## What they cover, and what they do not

They run the document runtime end to end on bar data and pin what came out of
it: the graph, the indicators, the sizing, the orders those produce, the fills
the venue gave them on bars, and every statistic computed from them. A change
to any of that moves a number here.

They do **not** cover the matcher's own rules. These examples act on bar closes
with market orders, so nothing in them ever meets a quote's size, a print
reaching a resting order, or the path walked through a bar - a tick-driven
reference was tried and produced numbers identical to the bar one, which is a
reference that covers nothing. Those rules are pinned where they can be seen:
`PartialFillTests`, `FundingTests`, `LiquidationTests`,
`LimitOrderMatchingTests` and the rest of the simulator suite.

## When one fails

It means the engine now answers something different. That is not a failure by
itself - it is a question with two answers:

- **the engine got more honest**, and the numbers are meant to move: partial
  fills, funding and liquidation each moved them, and each time these files
  were rewritten in the same commit as the change, with the reason in the commit
  message;
- **something broke**, and the reference is the only thing that noticed.

Decide which before touching the file. A reference updated without a reason in
the commit message is a regression gate that has been switched off.

## Updating deliberately

Run the failing test, read the two numbers, and write the new one in. Never
regenerate all of them "to get green": a file that changes without anybody
reading the difference is worse than no file at all.
