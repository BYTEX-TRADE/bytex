using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
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
        RootCommand root = new("BYTEX trading engine command-line interface");

        Option<string> logLevel = new("--log-level") { Description = "Minimum log level (Trace, Debug, Information, Warning, Error)", DefaultValueFactory = _ => "Information" };
        Option<string?> plugins = new("--plugins") { Description = "Directory containing plugin assemblies" };
        root.Options.Add(logLevel);
        root.Options.Add(plugins);

        root.Subcommands.Add(BuildBacktestCommand(logLevel, plugins));
        root.Subcommands.Add(BuildRunCommand(logLevel, plugins));
        root.Subcommands.Add(BuildCatalogCommand(logLevel));
        root.Subcommands.Add(DocumentCommands.Build());
        root.Subcommands.Add(KeyCommands.Build(logLevel));
        root.Subcommands.Add(BuildVenuesCommand(plugins));
        root.Subcommands.Add(BuildVersionCommand());

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
            if (ParseSearch(json) is { } search)
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
            if (ParseBatch(json) is { } batch)
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

            List<BacktestRunConfig> runs = ParseRuns(json);
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
    private static BacktestSearchConfig? ParseSearch(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("run", out _)
            || !doc.RootElement.TryGetProperty("space", out _))
        {
            return null;
        }

        BacktestSearchConfig search = JsonSerializer.Deserialize<BacktestSearchConfig>(json, BytexJson.Options)
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
    private static BacktestBatchConfig? ParseBatch(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("run", out _))
        {
            return null;
        }

        return JsonSerializer.Deserialize<BacktestBatchConfig>(json, BytexJson.Options)
            ?? throw new InvalidOperationException("The batch configuration could not be read.");
    }

    private static List<BacktestRunConfig> ParseRuns(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.EnumerateArray().Select(e => JsonSerializer.Deserialize<BacktestRunConfig>(e.GetRawText(), BytexJson.Options)!).ToList();
        }

        return [JsonSerializer.Deserialize<BacktestRunConfig>(json, BytexJson.Options)!];
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
            TradingNodeConfig nodeConfig = JsonSerializer.Deserialize<TradingNodeConfig>(json, BytexJson.Options) ?? throw new InvalidOperationException("Invalid node configuration.");

            // What the command line says about the switch and the limits overrides what the file says, one value at a
            // time: a host that passes none of these runs the node exactly as its configuration describes it. A
            // configuration may leave the risk section out or write it as null, which is not a reason to fail: it
            // means the node was given no limits.
            RiskEngineConfig riskConfig = nodeConfig.Kernel.RiskEngine is { } configured ? configured : new RiskEngineConfig();
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
                Kernel = nodeConfig.Kernel with { RiskEngine = riskConfig with { Limits = limits } },
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
                            foreach (Bytex.Core.Trading.Strategy strategy in node.Kernel.Trader.Strategies)
                            {
                                ShutdownHelper.Flatten(strategy, node.Kernel, cancelOrders, closePositions);
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
        Option<string> path = new("--path", "-p") { Description = "Catalog root directory", Required = true };
        Command catalog = new("catalog", "Inspect and populate a Parquet data catalog");

        Command list = new("list", "List the data held in the catalog");
        list.Options.Add(path);
        list.SetAction(parseResult =>
        {
            ParquetDataCatalog cat = new(parseResult.GetValue(path)!);
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

        Option<string> file = new("--file", "-f") { Description = "Input file", Required = true };
        Option<string> kind = new("--kind", "-k") { Description = "bars | quotes | trades", Required = true };
        Option<string> instrumentId = new("--instrument", "-i") { Description = "Instrument id, e.g. BTCUSDT.BINANCE", Required = true };
        Option<string?> barType = new("--bar-type") { Description = "Bar type for bars, e.g. BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL" };
        Option<string> tsFormat = new("--timestamp-format") { Description = "iso | unix_s | unix_ms | unix_us | unix_ns | .NET format", DefaultValueFactory = _ => "iso" };
        Command importCsv = new("import-csv", "Import a CSV file into the catalog");
        importCsv.Options.Add(path);
        importCsv.Options.Add(file);
        importCsv.Options.Add(kind);
        importCsv.Options.Add(instrumentId);
        importCsv.Options.Add(barType);
        importCsv.Options.Add(tsFormat);
        importCsv.SetAction(async (parseResult, ct) =>
        {
            ParquetDataCatalog cat = new(parseResult.GetValue(path)!);
            InstrumentId id = InstrumentId.Parse(parseResult.GetValue(instrumentId)!);
            Instrument instrument = cat.Instrument(id) ?? throw new InvalidOperationException($"Instrument {id} is not in the catalog; add it first with 'catalog add-instrument'.");
            CsvColumns columns = new() { TimestampFormat = parseResult.GetValue(tsFormat)! };
            string input = parseResult.GetValue(file)!;
            switch (parseResult.GetValue(kind)!.ToLowerInvariant())
            {
                case "bars":
                    {
                        BarType bt = BarType.Parse(parseResult.GetValue(barType) ?? throw new InvalidOperationException("--bar-type is required for bars."));
                        IReadOnlyList<Bar> bars = CsvLoader.LoadBars(input, instrument, bt, columns);
                        await cat.WriteBarsAsync(bars, ct).ConfigureAwait(false);
                        Console.WriteLine($"Imported {bars.Count} bars for {bt}");
                        break;
                    }

                case "quotes":
                    {
                        IReadOnlyList<QuoteTick> quotes = CsvLoader.LoadQuoteTicks(input, instrument, columns);
                        await cat.WriteQuoteTicksAsync(quotes, ct).ConfigureAwait(false);
                        Console.WriteLine($"Imported {quotes.Count} quotes for {id}");
                        break;
                    }

                case "trades":
                    {
                        IReadOnlyList<TradeTick> trades = CsvLoader.LoadTradeTicks(input, instrument, columns);
                        await cat.WriteTradeTicksAsync(trades, ct).ConfigureAwait(false);
                        Console.WriteLine($"Imported {trades.Count} trades for {id}");
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
            ParquetDataCatalog cat = new(parseResult.GetValue(path)!);
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
            ParquetDataCatalog cat = new(parseResult.GetValue(path)!);
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
        catalog.Subcommands.Add(importCsv);
        catalog.Subcommands.Add(addInstrument);
        catalog.Subcommands.Add(fetchInstruments);
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
                    Console.WriteLine($"  {family.Name}: {string.Join(", ", family.InstrumentClasses)}{(family.PaysFunding ? " · pays funding" : string.Empty)}");
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
        registry.AddPlugin(new DocumentsPlugin());
        registry.AddPlugin(new TardisPlugin());
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());
        registry.AddIndicatorFactory(new BuiltinIndicatorFactory());
        if (pluginDirectory is not null)
        {
            foreach (IPlugin plugin in PluginLoader.LoadFromDirectory(pluginDirectory))
            {
                registry.AddPlugin(plugin);
            }
        }

        return registry;
    }
}
