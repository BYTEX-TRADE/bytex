# Design notes

Each note records one decision: the problem, the choice made, the
alternatives considered, and the consequences. Notes are numbered in the order
they were written and are never edited after acceptance — a later note
supersedes an earlier one.

| # | Title | Status |
|---|---|---|
| [0001](0001-architecture.md) | System architecture and environment contexts | accepted |
| [0002](0002-numeric-and-time-model.md) | Numeric and time model | accepted |
| [0003](0003-domain-model.md) | Domain model | accepted |
| [0004](0004-strategy-sdk.md) | Strategy SDK contract | accepted |
| [0005](0005-adapter-sdk.md) | Adapter SDK contract | accepted |
| [0006](0006-plugin-contract.md) | Plugin contract | accepted |
| [0007](0007-messaging-and-kernel.md) | Message bus and kernel execution model | accepted |

Contracts described in 0004–0006 are frozen for the 0.x line: additions are
allowed, breaking changes require a superseding note and a minor version bump.
