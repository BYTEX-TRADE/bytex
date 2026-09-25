## What and why

<!-- What changed and why. Link the issue: "Closes #123". -->

## Tests

<!-- Which tests cover this change. For a bug fix: the test that fails without the fix. -->

- [ ] New behaviour is covered by tests, or a bug fix comes with a test that fails without it
- [ ] `dotnet build` is clean and `dotnet test` passes locally
- [ ] Not applicable (documentation, comments, build scripts only), because:

## Checklist

- [ ] Deterministic event ordering is preserved (kernel, engines, clock, matching)
- [ ] No `double`/`float` arithmetic on prices, quantities or balances
- [ ] Public contract or architectural change has a note under `docs/design/`
- [ ] New dependencies are in `Directory.Packages.props` and `THIRD-PARTY-NOTICES.md`
