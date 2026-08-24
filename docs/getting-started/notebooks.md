# Notebooks

The engine packages work in .NET Interactive (Polyglot Notebooks in VS Code,
or JupyterLab with the .NET kernel), which is a convenient way to explore
data, iterate on a strategy, and inspect results interactively.

## Setup

```bash
dotnet tool install --global Microsoft.dotnet-interactive
dotnet interactive jupyter install        # for JupyterLab; VS Code needs only the Polyglot Notebooks extension
```

## Referencing the engine

From published packages:

```csharp
#r "nuget: Bytex.Backtest"
#r "nuget: Bytex.Indicators"
```

From a source checkout (after `dotnet build`), reference the built assemblies:

```csharp
#r "../../src/Bytex.Core/bin/Debug/net10.0/Bytex.Core.dll"
#r "../../src/Bytex.Backtest/bin/Debug/net10.0/Bytex.Backtest.dll"
```

## Example

`examples/notebooks/first-backtest.ipynb` builds synthetic bars, runs the
EMA-cross strategy, prints the summary, and shows positions and the equity
curve as notebook tables. Open it from the repository root so the relative
assembly paths resolve.

## Tips

- `BacktestResult` tables (`Orders`, `Fills`, `Positions`, `Accounts`,
  `EquityCurves`) are plain records and render as grids.
- `ReportWriter.ToJson(result)` gives the whole result as JSON for other
  tools; `ReportWriter.WriteAll(result, dir)` writes CSV files.
- Use `engine.Reset()` between runs to sweep parameters without rebuilding
  the engine.
