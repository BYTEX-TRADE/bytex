using System.Runtime.InteropServices;
using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Databento;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tardis;
using Bytex.Backtest;
using Bytex.Core.Adapters;
using Bytex.Core.Engines;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Plugins;
using Bytex.Core.Serialization;
using Bytex.Data;
using Bytex.Documents;
using Bytex.Indicators;
using Bytex.Live;
using Bytex.Live.Control;
using Bytex.Live.Sandbox;
using Microsoft.Extensions.Logging;

namespace Bytex.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Every option that names a catalog accepts s3://bucket/prefix from here on, because this says so once.
        Persistence.S3.S3Location.Register();

        RootCommand root = new("BYTEX trading engine command-line interface");

        Option<string> logLevel = new("--log-level") { Description = "Minimum log level (Trace, Debug, Information, Warning, Error)", DefaultValueFactory = _ => "Information" };
        Option<string?> plugins = new("--plugins") { Description = "Directory containing plugin assemblies" };
        root.Options.Add(logLevel);
        root.Options.Add(plugins);

        root.Subcommands.Add(BuildBacktestCommand(logLevel, plugins));
        root.Subcommands.Add(BuildRunCommand(logLevel, plugins));
        root.Subcommands.Add(BuildCatalogCommand(logLevel));
        root.Subcommands.Add(DocumentCommands.Build(plugins));
        root.Subcommands.Add(KeyCommands.Build(logLevel));
        root.Subcommands.Add(BuildVenuesCommand(plugins));
        root.Subcommands.Add(BuildVersionCommand());
        root.Subcommands.Add(MigrationCommands.Build());

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    // ----- backtest -----

    private static Command BuildBacktestCommand(Option<string> logLevel, Option<string?> plugins)
    {
        Option<string> config = new("--config", "-c") { Description = "Path to a backtest run configuration (JSON)", Required = true };
        Option<string?> output = new("--output", "-o") { Description = "Directory for reports (overrides the configuration)" };
        Command command = new("backtest", "Run one or more backtests, or a batch of them, from a configuration file");
        command.Options.Add(config);
        command.Options.Add(output);
        command.SetAction(async (parseResult, ct) =>
        {
            using ILoggerFactory loggerFactory = CreateLogging(parseResult.GetValue(logLevel)!);
            PluginRegistry registry = CreateRegistry(parseResult.GetValue(plugins));
            string path = parseResult.GetValue(config)!;
            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            string? outputDir = parseResult.GetValue(output);
            BacktestNode node = new(registry, loggerFactory);

            // A configuration that names a run and a space to search in is a search: generations of candidates, each
            // one judged by the figure the configuration names. It is looked for first because it names a run as a
            // batch does.
            if (ParseSearch(json, path) is { } search)
            {
                if (outputDir is not null)
                {
                    search = search with { Run = search.Run with { OutputDirectory = outputDir } };
                }

                BacktestSearch searched = await node.RunSearchAsync(search, ct).ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine(searched.Summary());
                Console.WriteLine();

                if (searched.Best is not { } best)
                {
                    Console.Error.WriteLine($"None of the {searched.Candidates.Count} candidate(s) of the search produced a result.");
                    return 1;
                }

                Console.WriteLine($"Best: {best.PointText} ({search.Objective!.Figure} {best.Fitness}) in {best.Run.RunId}");
                return 0;
            }

            // A configuration that names a run and what to vary in it is a batch: many runs from the one description,
            // reported as one table. Anything else is the run, or runs, it has always been.
            if (ParseBatch(json, path) is { } batch)
            {
                if (outputDir is not null)
                {
                    batch = batch with { Run = batch.Run with { OutputDirectory = outputDir } };
                }

                BacktestBatch done = await node.RunBatchAsync(batch, ct).ConfigureAwait(false);
                Console.WriteLine();
                foreach (Currency currency in done.Runs.Where(r => r.Result is not null).SelectMany(r => r.Result!.Currencies.Select(c => c.Currency)).Distinct().OrderBy(c => c.Code, StringComparer.Ordinal))
                {
                    Console.WriteLine(done.Summary(currency));
                    Console.WriteLine();
                }

                if (done.Completed == 0)
                {
                    Console.Error.WriteLine($"None of the {done.Runs.Count} run(s) of the batch produced a result.");
                    return 1;
                }

                return 0;
            }

            List<BacktestRunConfig> runs = ParseRuns(json, path);
            if (outputDir is not null)
            {
                runs = runs.Select(r => r with { OutputDirectory = outputDir }).ToList();
            }

            IReadOnlyList<BacktestResult> results = await node.RunAsync(runs, ct).ConfigureAwait(false);
            foreach (BacktestResult result in results)
            {
                Console.WriteLine();
                Console.WriteLine(result.Summary());
            }

            // A strategy that refused to run is a failed run, however tidy its numbers look: exiting 0 with no orders
            // reads exactly like a strategy whose conditions never came true.
            List<string> faulted = results.SelectMany(r => r.FaultedStrategies.Select(s => $"{r.RunId}/{s}")).ToList();
            if (faulted.Count > 0)
            {
                Console.Error.WriteLine($"Strategies that faulted and did not trade: {string.Join(", ", faulted)}");
                return 1;
            }

            return 0;
        });
        return command;
    }

    /// <summary>
    /// The configuration as a search, or null when it is not one. A search is an object that names both the run to
    /// vary and the space to vary it in; without a space it is a batch or a run, as it always was.
    /// </summary>
    private static BacktestSearchConfig? ParseSearch(string json, string path)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("run", out _)
            || !doc.RootElement.TryGetProperty("space", out _))
        {
            return null;
        }

        BacktestSearchConfig search = ReadConfig<BacktestSearchConfig>(json, path)
            ?? throw new InvalidOperationException("The search configuration could not be read.");

        // A file cannot carry a function, so a search described in one has to say which figure it is looking for.
        // Saying nothing would leave the engine to decide what better means, which is not its judgement to make.
        return search.Objective is null
            ? throw new InvalidOperationException(
                "A search needs an objective: the figure to judge a candidate by, the currency to read it in, and whether less of it is better.")
            : search;
    }

    /// <summary>
    /// The configuration as a batch, or null when it is not one. A batch is an object that names the run to vary -
    /// anything else is read as a run, or an array of runs, exactly as before.
    /// </summary>
    private static BacktestBatchConfig? ParseBatch(string json, string path)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("run", out _))
        {
            return null;
        }

        return ReadConfig<BacktestBatchConfig>(json, path)
            ?? throw new InvalidOperationException("The batch configuration could not be read.");
    }

    /// <summary>
    /// A configuration file, read strictly: a member this engine does not know is refused by name rather than skipped.
    ///
    /// <para>
    /// Every caller here is reading something a person wrote and expects to take effect. The case this exists for is
    /// <c>"testnet": true</c> - nothing in this engine reads it, so the run started against the real venue and said
    /// nothing. The refusal names the file, because a batch reads several and "unknown member" alone does not say
    /// which one to open.
    /// </para>
    /// </summary>
    private static T? ReadConfig<T>(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, BytexJson.Strict);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"{path} has a setting this engine does not have, so nothing would have read it: {e.Message} "
                + "Remove it, or correct its spelling. A setting this engine does not know is not a setting.", e);
        }
    }

    private static List<BacktestRunConfig> ParseRuns(string json, string path)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.EnumerateArray().Select(e => ReadConfig<BacktestRunConfig>(e.GetRawText(), path)!).ToList();
        }

        return [ReadConfig<BacktestRunConfig>(json, path)!];
    }

    // ----- run -----

    private static Command BuildRunCommand(Option<string> logLevel, Option<string?> plugins)
    {
        Option<string> config = new("--config", "-c") { Description = "Path to a trading node configuration (JSON)", Required = true };
        Option<TimeSpan?> duration = new("--duration") { Description = "Stop automatically after this time (e.g. 00:02:00); default runs until interrupted" };
        Option<string?> envFile = new("--env-file") { Description = "Load KEY=VALUE lines (venue credentials) into this process before starting; the file is read here, never by a launching host" };
        Command command = new("run", "Run a live or sandbox trading node until interrupted");
        command.Options.Add(config);
        command.Options.Add(duration);
        Option<string?> control = new("--control") { Description = "Serve the node control channel under this name (named pipe on Windows, Unix socket elsewhere)" };
        Option<TimeSpan> controlHeartbeat = new("--control-heartbeat") { Description = "Heartbeat interval on the control channel", DefaultValueFactory = _ => TimeSpan.FromSeconds(5) };
        Option<bool> halted = new("--halted") { Description = "Start with trading halted: the node runs and places nothing until a host releases it over the control channel" };
        Option<TimeSpan?> reconcileInterval = new("--reconcile-interval") { Description = "How often the node checks itself against its venues while it runs (for example 00:05:00); left out, it checks once at start-up" };
        Option<bool> displayPrices = new("--display-prices") { Description = "Subscribe quotes for the instruments the node holds so that a monitor has a moving price; no strategy receives them" };
        Option<string?> maxLoss = new("--max-loss") { Description = "Stop trading once a period has lost this much, what open positions are down included: an amount (\"1000 USDT\") or a share of equity (\"2%\")" };
        Option<TimeSpan?> lossPeriod = new("--loss-period") { Description = "The window --max-loss is measured over (default 1.00:00:00, a UTC day)" };
        Option<string?> onLossLimit = new("--on-loss-limit") { Description = "What reaching --max-loss does: stop-trading (the default: nothing that adds until a host resumes the node, exits still allowed), deny-adds (deny that one order and judge the next on its own merits), or flatten (stop trading and close what is open with reduce-only orders)" };
        Option<string?> maxExposure = new("--max-exposure") { Description = "The most the account may carry at once: an amount (\"50000 USDT\") or a share of equity (\"50%\")" };
        Option<int?> maxPositions = new("--max-open-positions") { Description = "The most open positions across the account" };
        Option<int?> maxPositionsPer = new("--max-open-positions-per-instrument") { Description = "The most open positions on one instrument" };
        Option<int?> maxOrders = new("--max-working-orders") { Description = "The most orders working across the account" };
        Option<int?> maxOrdersPer = new("--max-working-orders-per-instrument") { Description = "The most orders working on one instrument" };
        command.Options.Add(envFile);
        command.Options.Add(control);
        command.Options.Add(controlHeartbeat);
        command.Options.Add(halted);
        command.Options.Add(displayPrices);
        command.Options.Add(reconcileInterval);
        command.Options.Add(maxLoss);
        command.Options.Add(lossPeriod);
        command.Options.Add(onLossLimit);
        command.Options.Add(maxExposure);
        command.Options.Add(maxPositions);
        command.Options.Add(maxPositionsPer);
        command.Options.Add(maxOrders);
        command.Options.Add(maxOrdersPer);
        command.SetAction(async (parseResult, ct) =>
        {
            using ILoggerFactory loggerFactory = CreateLogging(parseResult.GetValue(logLevel)!);
            if (parseResult.GetValue(envFile) is { } envPath)
            {
                int loaded = EnvFile.Load(envPath);
                loggerFactory.CreateLogger("bytex").LogInformation("Loaded {Count} variables from {Path}", loaded, envPath);
            }

            PluginRegistry registry = CreateRegistry(parseResult.GetValue(plugins));
            string json = await File.ReadAllTextAsync(parseResult.GetValue(config)!, ct).ConfigureAwait(false);
            // Strict: a member this engine does not know is a setting somebody believed they had made, and a node that
            // skips it starts anyway and does the opposite of what was asked. See BytexJson.Strict.
            TradingNodeConfig nodeConfig = ReadConfig<TradingNodeConfig>(json, parseResult.GetValue(config)!) ?? throw new InvalidOperationException("Invalid node configuration.");

            // Adding a strategy to a running node (R5.10, control protocol 6) needs the node to know what a path means,
            // and the engine deliberately leaves that to the host: a path, not a strategy, is what crosses a control
            // channel. Nothing here supplied it, so `add-strategy` was refused on every node this command ever started -
            // a shipped feature that could not be reached from the tool that ships it.
            //
            // A path here means a file holding one strategy definition: the same object as an entry of `strategies` in
            // the node's own configuration, provider id and payload included. Nothing new to learn, it reaches a
            // document through the document provider exactly as a configured strategy does, and a definition read from
            // a file is held to the same rule as one read from the configuration - an unknown member is refused rather
            // than skipped.
            nodeConfig = nodeConfig with
            {
                StrategyFromPath = path =>
                {
                    string definitionJson = File.ReadAllText(path);
                    StrategyDefinition definition = ReadConfig<StrategyDefinition>(definitionJson, path)
                        ?? throw new InvalidOperationException($"{path} does not hold a strategy definition.");
                    return registry.CreateStrategy(definition);
                },
            };

            // What the command line says about the switch and the limits overrides what the file says, one value at a
            // time: a host that passes none of these runs the node exactly as its configuration describes it. A
            // configuration may leave the risk section out or write it as null, which is not a reason to fail: it
            // means the node was given no limits.
            OrderPolicyConfig riskConfig = nodeConfig.TradingRuntime.OrderPolicy is { } configured ? configured : new OrderPolicyConfig();
            RiskLimits limits = riskConfig.Limits is { } configuredLimits ? configuredLimits : new RiskLimits();
            if (parseResult.GetValue(maxLoss) is { } lossText)
            {
                if (!RiskLimit.TryParse(lossText, out RiskLimit? loss))
                {
                    Console.Error.WriteLine($"--max-loss '{lossText}' is neither an amount ('1000 USDT') nor a share of equity ('2%').");
                    return 2;
                }

                limits = limits with { MaxLossPerPeriod = loss };
            }

            if (parseResult.GetValue(maxExposure) is { } exposureText)
            {
                if (!RiskLimit.TryParse(exposureText, out RiskLimit? exposure))
                {
                    Console.Error.WriteLine($"--max-exposure '{exposureText}' is neither an amount ('50000 USDT') nor a share of equity ('50%').");
                    return 2;
                }

                limits = limits with { MaxExposure = exposure };
            }

            if (parseResult.GetValue(lossPeriod) is { } period)
            {
                limits = limits with { LossPeriod = period };
            }

            if (parseResult.GetValue(onLossLimit) is { } onLossText)
            {
                LossLimitBreach? breach = onLossText.ToLowerInvariant() switch
                {
                    "stop-trading" or "stoptrading" or "stop" => LossLimitBreach.StopTrading,
                    "deny-adds" or "denyadds" or "deny" => LossLimitBreach.DenyAdds,
                    "flatten" or "close" => LossLimitBreach.Flatten,
                    _ => null,
                };
                if (breach is not { } action)
                {
                    Console.Error.WriteLine($"--on-loss-limit '{onLossText}' is none of 'stop-trading', 'deny-adds' or 'flatten'.");
                    return 2;
                }

                limits = limits with { OnLossLimit = action };
            }

            if (parseResult.GetValue(maxPositions) is { } positions)
            {
                limits = limits with { MaxOpenPositions = positions };
            }

            if (parseResult.GetValue(maxPositionsPer) is { } positionsPer)
            {
                limits = limits with { MaxOpenPositionsPerInstrument = positionsPer };
            }

            if (parseResult.GetValue(maxOrders) is { } orders)
            {
                limits = limits with { MaxWorkingOrders = orders };
            }

            if (parseResult.GetValue(maxOrdersPer) is { } ordersPer)
            {
                limits = limits with { MaxWorkingOrdersPerInstrument = ordersPer };
            }

            nodeConfig = nodeConfig with
            {
                TradingRuntime = nodeConfig.TradingRuntime with { OrderPolicy = riskConfig with { Limits = limits } },
                StartHalted = nodeConfig.StartHalted || parseResult.GetValue(halted),
                DisplayPrices = nodeConfig.DisplayPrices || parseResult.GetValue(displayPrices),
                ReconciliationInterval = parseResult.GetValue(reconcileInterval) ?? nodeConfig.ReconciliationInterval,
            };

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // Ctrl+C above covers a person at a terminal. SIGTERM is how everything else asks a process to stop - a
            // container, a service manager, a host shutting down a node it started - and without this the process is
            // terminated where it stands, which costs the node its chance to save what its strategies know and to
            // tell its venues it is going. Handling it turns that into the ordinary stop path.
            using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cts.Cancel();
            });
            if (parseResult.GetValue(duration) is { } limit)
            {
                cts.CancelAfter(limit);
            }

            await using TradingNode node = new(nodeConfig, registry, loggerFactory);
            NodeControlServer? controlServer = null;
            if (parseResult.GetValue(control) is { } controlName)
            {
                controlServer = new NodeControlServer(controlName, node, parseResult.GetValue(controlHeartbeat), loggerFactory);
                controlServer.StopRequested += (cancelOrders, closePositions) =>
                {
                    if (cancelOrders || closePositions)
                    {
                        node.Loop.Post(() =>
                        {
                            foreach (Bytex.Core.Trading.Strategy strategy in node.TradingRuntime.ModuleHost.Strategies)
                            {
                                ShutdownHelper.Flatten(strategy, node.TradingRuntime, cancelOrders, closePositions);
                            }
                        });
                    }

                    cts.Cancel();
                };
                controlServer.Start();
            }

            try
            {
                await node.RunAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                if (controlServer is not null)
                {
                    await controlServer.DisposeAsync().ConfigureAwait(false);
                }
            }

            return 0;
        });
        return command;
    }

    // ----- catalog -----

    private static Command BuildCatalogCommand(Option<string> logLevel)
    {
        Option<string> path = new("--path", "-p") { Description = "Catalog root: a directory, or s3://bucket/prefix", Required = true };
        Command catalog = new("catalog", "Inspect and populate a Parquet data catalog");

        Command list = new("list", "List the data held in the catalog");
        list.Options.Add(path);
        list.SetAction(parseResult =>
        {
            MarketArchive cat = new(parseResult.GetValue(path)!);
            IReadOnlyList<Instrument> instruments = cat.Instruments();
            Console.WriteLine($"Instruments ({instruments.Count}):");
            foreach (Instrument instrument in instruments)
            {
                Console.WriteLine($"  {instrument.Id}  {instrument.GetType().Name}  tick={instrument.PriceIncrement} step={instrument.SizeIncrement}");
            }

            Console.WriteLine();
            Console.WriteLine("Data:");
            foreach (CatalogEntry entry in cat.Entries())
            {
                Console.WriteLine($"  {entry.Kind,-12} {entry.Key,-50} files={entry.FileCount,-4} {entry.Start} -> {entry.End}  {entry.SizeBytes / 1024.0:F1} KB");
            }

            return 0;
        });

        Command info = new("info", "What the catalog holds: ranges, rows, files and size per data set");
        info.Options.Add(path);
        info.SetAction(parseResult =>
        {
            MarketArchive cat = new(parseResult.GetValue(path)!);
            // With row counts: this command is the one place a person asks what is in there, and how many rows is the
            // figure they mean - the documentation said this command reported it and it never did. It costs a read of
            // each file's footer, which is why the catalog does not count them unless asked.
            IReadOnlyList<CatalogEntry> entries = [.. cat.Entries(rows: true).OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal)];
            long total = entries.Sum(e => e.SizeBytes);
            long totalRows = entries.Sum(e => e.Rows ?? 0);

            Console.WriteLine($"{entries.Count} data set(s), {totalRows.ToString("N0", CultureInfo.InvariantCulture)} row(s), {Size(total)} in total, under {cat.RootPath}");
            Console.WriteLine();

            foreach (IGrouping<string, CatalogEntry> byKind in entries.GroupBy(e => e.Kind))
            {
                Console.WriteLine($"{byKind.Key} ({Size(byKind.Sum(e => e.SizeBytes))})");
                foreach (CatalogEntry entry in byKind)
                {
                    string span = entry.Start is { } from && entry.End is { } to
                        ? $"{from.ToDateTimeUtc():yyyy-MM-dd HH:mm:ss} -> {to.ToDateTimeUtc():yyyy-MM-dd HH:mm:ss}"
                        : "nothing recorded";

                    Console.WriteLine($"  {entry.Key,-46} {(entry.Rows ?? 0).ToString("N0", CultureInfo.InvariantCulture),12} row(s)  {entry.FileCount,4} file(s)  {Size(entry.SizeBytes),10}  {span}");
                }

                Console.WriteLine();
            }

            // Said here rather than only by `check`, because somebody looking at how much data they have is exactly
            // the person about to try to read all of it.
            IReadOnlyList<CatalogProblem> problems = cat.Check();
            Console.WriteLine(problems.Count == 0
                ? "Nothing here would stop a streaming read."
                : $"{problems.Count} problem(s) would stop a streaming read - run `bytex catalog check` to see them.");

            return 0;

            static string Size(long bytes) => bytes switch
            {
                >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
                >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
                >= 1024 => $"{bytes / 1024.0:F1} KB",
                _ => $"{bytes} B",
            };
        });

        Command check = new("check", "Report anything in the catalog that would stop a streaming read");
        check.Options.Add(path);
        check.SetAction(parseResult =>
        {
            MarketArchive cat = new(parseResult.GetValue(path)!);
            IReadOnlyList<CatalogProblem> problems = cat.Check();
            if (problems.Count == 0)
            {
                Console.WriteLine("Nothing to report.");
                return 0;
            }

            foreach (CatalogProblem problem in problems)
            {
                Console.WriteLine($"{problem.Kind} {problem.Key} [{problem.File}]");
                Console.WriteLine($"  {problem.Detail}");
            }

            // A non-zero exit, because this is the thing a scheduled job wants to notice.
            return 1;
        });

        Option<string?> consolidateKind = new("--kind", "-k") { Description = "quotes | trades | bars | book_deltas | book_depth | funding; all kinds when omitted" };
        Option<string?> consolidateKey = new("--key") { Description = "Instrument id or bar type; every key of the kind when omitted" };
        Command consolidate = new("consolidate", "Rewrite a data set as one ordered file, dropping records held twice");
        consolidate.Options.Add(path);
        consolidate.Options.Add(consolidateKind);
        consolidate.Options.Add(consolidateKey);
        consolidate.SetAction(async (parseResult, ct) =>
        {
            MarketArchive cat = new(parseResult.GetValue(path)!);
            string? onlyKind = parseResult.GetValue(consolidateKind);
            string? onlyKey = parseResult.GetValue(consolidateKey);

            CatalogEntry[] targets = [.. cat.Entries()
                .Where(e => onlyKind is null || string.Equals(e.Kind, onlyKind, StringComparison.OrdinalIgnoreCase))
                .Where(e => onlyKey is null || string.Equals(e.Key, onlyKey, StringComparison.OrdinalIgnoreCase))];

            if (targets.Length == 0)
            {
                Console.Error.WriteLine("No data set matches; `bytex catalog info` lists what is here.");
                return 1;
            }

            foreach (CatalogEntry entry in targets)
            {
                int rows = await cat.ConsolidateAsync(entry.Kind, entry.Key, ct).ConfigureAwait(false);

                // Not "-> 1": a data set past the file bound consolidates to the fewest files the bound allows, and
                // the count is exactly that, so it is computed rather than assumed.
                int files = (rows + MarketArchive.MaxRowsPerFile - 1) / MarketArchive.MaxRowsPerFile;
                Console.WriteLine(rows == 0
                    ? $"{entry.Kind} {entry.Key}: already one file, left alone"
                    : $"{entry.Kind} {entry.Key}: {entry.FileCount} files -> {files}, {rows} record(s)");
            }

            return 0;
        });

        Option<string> file = new("--file", "-f") { Description = "Input file", Required = true };
        Option<string> kind = new("--kind", "-k") { Description = "bars | quotes | trades", Required = true };
        Option<string> marketKey = new("--instrument", "-i") { Description = "Instrument id, e.g. bx-market:v2/BINANCE/BTCUSDT", Required = true };
        Option<string?> candleSeries = new("--candle-series") { Description = "Bar type for bars, e.g. bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider" };
        Option<string> tsFormat = new("--timestamp-format") { Description = "iso | unix_s | unix_ms | unix_us | unix_ns | .NET format", DefaultValueFactory = _ => "iso" };
        Option<bool> noHeader = new("--no-header") { Description = "The first line is data rather than a header; --columns then names each field by position" };
        Option<string?> columnMap = new("--columns") { Description = "field=source pairs: 'timestamp=0,open=1,close=4' by position, or 'close=last' by header name" };
        Option<string> separator = new("--separator") { Description = "Field separator: one character, or 'tab'", DefaultValueFactory = _ => "," };
        Command importCsv = new("import-csv", "Import a CSV file into the catalog");
        importCsv.Options.Add(path);
        importCsv.Options.Add(file);
        importCsv.Options.Add(kind);
        importCsv.Options.Add(marketKey);
        importCsv.Options.Add(candleSeries);
        importCsv.Options.Add(tsFormat);
        importCsv.Options.Add(noHeader);
        importCsv.Options.Add(columnMap);
        importCsv.Options.Add(separator);
        importCsv.SetAction(async (parseResult, ct) =>
        {
            MarketArchive cat = new(parseResult.GetValue(path)!);
            MarketKey id = MarketKey.Parse(parseResult.GetValue(marketKey)!);
            Instrument instrument = cat.Instrument(id) ?? throw new InvalidOperationException($"Instrument {id} is not in the catalog; add it first with 'catalog add-instrument'.");
            CsvColumns columns;
            try
            {
                columns = CsvImport.Columns(
                    parseResult.GetValue(tsFormat)!,
                    parseResult.GetValue(noHeader),
                    parseResult.GetValue(columnMap),
                    parseResult.GetValue(separator)!);
            }
            catch (ArgumentException e)
            {
                // What was wrong with the request, rather than a parse failure five hundred rows into the file.
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            string input = parseResult.GetValue(file)!;
            switch (parseResult.GetValue(kind)!.ToLowerInvariant())
            {
                case "bars":
                    {
                        CandleSeries bt = CandleSeries.Parse(parseResult.GetValue(candleSeries) ?? throw new InvalidOperationException("--candle-series is required for bars."));
                        IReadOnlyList<Bar> bars = CsvLoader.LoadBars(input, instrument, bt, columns);
                        await cat.WriteBarsAsync(bars, ct).ConfigureAwait(false);
                        Console.WriteLine($"Imported {bars.Count} bars for {bt}");
                        Nothing(bars.Count, columns);
                        break;
                    }

                case "quotes":
                    {
                        IReadOnlyList<QuoteTick> quotes = CsvLoader.LoadQuoteTicks(input, instrument, columns);
                        await cat.WriteQuoteTicksAsync(quotes, ct).ConfigureAwait(false);
                        Console.WriteLine($"Imported {quotes.Count} quotes for {id}");
                        Nothing(quotes.Count, columns);
                        break;
                    }

                case "trades":
                    {
                        IReadOnlyList<TradeTick> trades = CsvLoader.LoadTradeTicks(input, instrument, columns);
                        await cat.WriteTradeTicksAsync(trades, ct).ConfigureAwait(false);
                        Console.WriteLine($"Imported {trades.Count} trades for {id}");
                        Nothing(trades.Count, columns);
                        break;
                    }

                default:
                    Console.Error.WriteLine("Unknown kind; expected bars, quotes, or trades.");
                    return 1;
            }

            return 0;
        });

        Option<string> instrumentFile = new("--file", "-f") { Description = "Instrument definition (JSON)", Required = true };
        Command addInstrument = new("add-instrument", "Add an instrument definition to the catalog");
        addInstrument.Options.Add(path);
        addInstrument.Options.Add(instrumentFile);
        addInstrument.SetAction(async (parseResult, ct) =>
        {
            MarketArchive cat = new(parseResult.GetValue(path)!);
            string json = await File.ReadAllTextAsync(parseResult.GetValue(instrumentFile)!, ct).ConfigureAwait(false);
            Instrument instrument = InstrumentJson.Deserialize(json);
            await cat.WriteInstrumentsAsync([instrument], ct).ConfigureAwait(false);
            Console.WriteLine($"Added {instrument.Id}");
            return 0;
        });

        Option<string> venue = new("--venue") { Description = "BINANCE | BYBIT | KUCOIN | OKX", Required = true };
        Option<string?> quote = new("--quote") { Description = "Only instruments with this quote currency" };
        Option<bool> futures = new("--futures") { Description = "Load perpetual/futures instruments instead of spot" };
        Option<string?> fetchBaseUrl = new("--base-url") { Description = "Override the venue's REST address (a proxy or a test venue)" };
        Option<string?> instrumentType = new("--instrument-type") { Description = "For a venue with more than two markets, the market to load by its own name (BINANCE: Spot | UsdMFutures | CoinMFutures; OKX: Spot | Swap | Futures)" };
        Command fetchInstruments = new("fetch-instruments", "Download instrument definitions from a venue into the catalog");
        fetchInstruments.Options.Add(path);
        fetchInstruments.Options.Add(instrumentType);
        fetchInstruments.Options.Add(venue);
        fetchInstruments.Options.Add(quote);
        fetchInstruments.Options.Add(futures);
        fetchInstruments.Options.Add(fetchBaseUrl);
        fetchInstruments.SetAction(async (parseResult, ct) =>
        {
            using ILoggerFactory loggerFactory = CreateLogging(parseResult.GetValue(logLevel)!);
            MarketArchive cat = new(parseResult.GetValue(path)!);
            Dictionary<string, string>? filters = parseResult.GetValue(quote) is { } q ? new Dictionary<string, string> { ["quote"] = q } : null;
            bool useFutures = parseResult.GetValue(futures);
            string? restBase = parseResult.GetValue(fetchBaseUrl);
            IReadOnlyList<Instrument> instruments;
            switch (parseResult.GetValue(venue)!.ToUpperInvariant())
            {
                case "BINANCE":
                    {
                        // Binance has three markets rather than two, so --futures cannot select between them on its
                        // own. Its USD-margined contracts are what --futures means on every other venue here; its
                        // coin-margined ones are asked for with --instrument-type, which names the venue's own word
                        // for the market, exactly as OKX's third market is below.
                        // IsDefined as well as TryParse: this parse accepts any number, so "--instrument-type 99"
                        // would otherwise become an account type the adapter has no host for and fail somewhere
                        // further in than the flag that caused it.
                        BinanceAccountType account = Enum.TryParse(parseResult.GetValue(instrumentType), ignoreCase: true, out BinanceAccountType chosen)
                            && Enum.IsDefined(chosen)
                                ? chosen
                                : useFutures ? BinanceAccountType.UsdMFutures : BinanceAccountType.Spot;

                        BinanceDataClientConfig cfg = new() { AccountType = account, BaseUrlHttp = restBase };
                        using BinanceHttp http = new(cfg, loggerFactory.CreateLogger("binance"));
                        BinanceInstrumentProvider provider = new(http, cfg.AccountType, null, loggerFactory.CreateLogger("binance"));
                        await provider.LoadAllAsync(ct, filters).ConfigureAwait(false);
                        instruments = provider.GetAll();
                        break;
                    }

                case "BYBIT":
                    {
                        BybitDataClientConfig cfg = new() { ProductType = useFutures ? BybitProductType.Linear : BybitProductType.Spot, BaseUrlHttp = restBase };
                        using BybitHttp http = new(cfg, loggerFactory.CreateLogger("bybit"));
                        BybitInstrumentProvider provider = new(http, cfg.ProductType, null, loggerFactory.CreateLogger("bybit"));
                        await provider.LoadAllAsync(ct, filters).ConfigureAwait(false);
                        instruments = provider.GetAll();
                        break;
                    }

                case "KUCOIN":
                    {
                        KucoinDataClientConfig cfg = new() { ProductType = useFutures ? KucoinProductType.Futures : KucoinProductType.Spot, BaseUrlHttp = restBase };
                        using KucoinHttp http = new(cfg, loggerFactory.CreateLogger("kucoin"));
                        InstrumentProviderBase provider = useFutures
                            ? new KucoinFuturesInstrumentProvider(http, null, loggerFactory.CreateLogger("kucoin"))
                            : new KucoinInstrumentProvider(http, null, loggerFactory.CreateLogger("kucoin"));
                        await provider.LoadAllAsync(ct, filters).ConfigureAwait(false);
                        instruments = provider.GetAll();
                        break;
                    }

                case "OKX":
                    {
                        // OKX has three markets rather than two, so --futures cannot select between them on its own.
                        // Its perpetuals are what --futures means on every other venue here; its dated contracts are
                        // asked for with --instrument-type, which names the venue's own word for the market.
                        OkxInstrumentType type = Enum.TryParse(parseResult.GetValue(instrumentType), ignoreCase: true, out OkxInstrumentType named)
                            ? named
                            : useFutures ? OkxInstrumentType.Swap : OkxInstrumentType.Spot;

                        OkxDataClientConfig cfg = new() { InstrumentType = type, BaseUrlHttp = restBase };
                        using OkxHttp http = new(cfg, loggerFactory.CreateLogger("okx"));
                        OkxInstrumentProvider provider = new(http, type, null, loggerFactory.CreateLogger("okx"));
                        await provider.LoadAllAsync(ct, filters).ConfigureAwait(false);
                        instruments = provider.GetAll();
                        break;
                    }

                default:
                    Console.Error.WriteLine("Unknown venue; expected BINANCE, BYBIT, KUCOIN or OKX.");
                    return 1;
            }

            await cat.WriteInstrumentsAsync(instruments, ct).ConfigureAwait(false);
            Console.WriteLine($"Stored {instruments.Count} instruments");
            return 0;
        });

        catalog.Subcommands.Add(list);
        catalog.Subcommands.Add(info);
        catalog.Subcommands.Add(check);
        catalog.Subcommands.Add(consolidate);
        catalog.Subcommands.Add(importCsv);
        catalog.Subcommands.Add(addInstrument);
        catalog.Subcommands.Add(fetchInstruments);
        Command recovery = new("recovery", "List retained superseded and uncommitted objects without deleting them");
        recovery.Options.Add(path);
        recovery.SetAction(result =>
        {
            MarketArchive archive = new(result.GetValue(path)!);
            Console.WriteLine(JsonSerializer.Serialize(archive.RecoveryObjects()));
            return 0;
        });
        catalog.Subcommands.Add(recovery);
        return catalog;
    }

    private static Command BuildVersionCommand()
    {
        Command command = new("version", "Print the engine version");
        command.SetAction(_ =>
        {
            Console.WriteLine("bytex " + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            return 0;
        });
        return command;
    }

    // ----- helpers -----

    private static ILoggerFactory CreateLogging(string level)
    {
        LogLevel minimum = Enum.TryParse(level, true, out LogLevel parsed) ? parsed : LogLevel.Information;
        return LoggerFactory.Create(b => b.SetMinimumLevel(minimum).AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
            o.UseUtcTimestamp = true;
        }));
    }

    /// <summary>
    /// What every venue this engine knows about is, without constructing a client or holding a key: its families, the
    /// instruments each returns, what a key is made of, what a client must be configured with, what the venue charges
    /// before an instrument is loaded, and what it publishes for free. A host reads this instead of keeping a table
    /// of venue facts written by hand from the adapter source - which is how "is this a perpetual" came to be decided
    /// by whether a symbol contained "-PERP".
    /// </summary>
    private static Command BuildVenuesCommand(Option<string?> plugins)
    {
        Option<bool> json = new("--json") { Description = "Print the venues as JSON and nothing else on standard output" };
        Command command = new("venues", "What each venue offers: its families, keys, configuration, fees and free data");
        command.Options.Add(json);
        command.SetAction(parseResult =>
        {
            PluginRegistry registry = CreateRegistry(parseResult.GetValue(plugins));
            List<VenueDescriptor> venues = [.. registry.Plugins.OfType<IVenuePlugin>().Select(p => p.Describe()).OrderBy(v => v.Venue.Value, StringComparer.Ordinal)];

            if (parseResult.GetValue(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(venues, BytexJson.Options));
                return 0;
            }

            foreach (VenueDescriptor venue in venues)
            {
                Console.WriteLine($"{venue.Venue} ({venue.DisplayName}) · broker tag: {venue.BrokerTag} · rebate: {venue.BrokerProgramme}");
                foreach (VenueFamily family in venue.Families)
                {
                    Console.WriteLine($"  {family.Name}: {string.Join(", ", family.InstrumentClasses)}{(family.PaysFunding ? " · pays funding" : string.Empty)} · {Collateral(family.Collateral)}");
                    Console.WriteLine($"    http {family.HttpBase}");
                    Console.WriteLine($"    ws   {family.WsBase ?? "handed out by the venue at connect time"}");
                    Console.WriteLine($"    key  {string.Join(", ", family.Key.Parts.Select(k => k.Required ? k.Variable : k.Variable + " (optional)"))}");
                    Console.WriteLine($"    fees maker {family.DefaultFees.Maker}, taker {family.DefaultFees.Taker} (an instrument's own rates win)");
                    if (family.Config.Count > 0)
                    {
                        Console.WriteLine($"    set  {string.Join(", ", family.Config.Select(c => $"{c.Key}={c.Value}"))}");
                    }

                    if (family.IgnoredConfig.Count > 0)
                    {
                        Console.WriteLine($"    ignores {string.Join(", ", family.IgnoredConfig)}");
                    }

                    foreach (VenueDataset dataset in family.FreeDatasets)
                    {
                        Console.WriteLine($"    free {dataset.Kind}: {dataset.Address}");
                    }
                }
            }

            return 0;
        });

        return command;

        // Printed for every family because it is the only thing that tells two families of one venue apart: this
        // venue's own word for them - "inverse", "usdc-futures" - says nothing to a reader who does not already know
        // the venue, which is the whole reason the declaration carries the fact rather than the name.
        static string Collateral(VenueCollateral collateral) => collateral.Kind switch
        {
            CollateralKind.None => "nothing is borrowed",
            CollateralKind.QuoteCurrency => "collateral: what the instrument is priced in",
            CollateralKind.BaseCurrency => "collateral: the instrument's own base currency (inverse)",
            CollateralKind.Currencies => "collateral: " + string.Join(", ", collateral.Currencies.Select(c => c.Code)),
            _ => "collateral: not stated",
        };
    }

    /// <summary>
    /// An import that read no rows is almost always a file whose first line is data being read as a header (R9.9).
    /// Said here rather than left as a cheerful "Imported 0", which is the same words as a file that really was empty.
    /// </summary>
    private static void Nothing(int rows, Data.CsvColumns columns)
    {
        if (rows == 0 && columns.HasHeader)
        {
            Console.WriteLine("Nothing was read. If the file's first line is data rather than a header, add --no-header together with --columns.");
        }
    }

    private static PluginRegistry CreateRegistry(string? pluginDirectory)
    {
        PluginRegistry registry = new();
        registry.AddPlugin(new BinancePlugin());
        registry.AddPlugin(new BitgetPlugin());
        registry.AddPlugin(new BybitPlugin());
        registry.AddPlugin(new GatePlugin());
        registry.AddPlugin(new HyperliquidPlugin());
        registry.AddPlugin(new KrakenPlugin());
        registry.AddPlugin(new KucoinPlugin());
        registry.AddPlugin(new OkxPlugin());
        registry.AddPlugin(new TardisPlugin());
        registry.AddPlugin(new DatabentoPlugin());
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());
        registry.AddIndicatorFactory(new BuiltinIndicatorFactory());

        List<IPlugin> loaded = [];
        if (pluginDirectory is not null)
        {
            foreach (IPlugin plugin in PluginLoader.LoadFromDirectory(pluginDirectory))
            {
                registry.AddPlugin(plugin);
                loaded.Add(plugin);
            }
        }

        // Last, because it runs documents against whatever node types the plugins above brought with them (R13.8).
        registry.AddPlugin(new DocumentsPlugin(PluginNodes.Catalog(loaded)));

        return registry;
    }
}
