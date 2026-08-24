using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tardis;
using Bytex.Backtest;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Plugins;
using Bytex.Core.Serialization;
using Bytex.Data;
using Bytex.Indicators;
using Bytex.Live;
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
        root.Subcommands.Add(BuildVersionCommand());

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    // ----- backtest -----

    private static Command BuildBacktestCommand(Option<string> logLevel, Option<string?> plugins)
    {
        Option<string> config = new("--config", "-c") { Description = "Path to a backtest run configuration (JSON)", Required = true };
        Option<string?> output = new("--output", "-o") { Description = "Directory for reports (overrides the configuration)" };
        Command command = new("backtest", "Run one or more backtests from a configuration file");
        command.Options.Add(config);
        command.Options.Add(output);
        command.SetAction(async (parseResult, ct) =>
        {
            using ILoggerFactory loggerFactory = CreateLogging(parseResult.GetValue(logLevel)!);
            PluginRegistry registry = CreateRegistry(parseResult.GetValue(plugins));
            string path = parseResult.GetValue(config)!;
            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            List<BacktestRunConfig> runs = ParseRuns(json);
            string? outputDir = parseResult.GetValue(output);
            if (outputDir is not null)
            {
                runs = runs.Select(r => r with { OutputDirectory = outputDir }).ToList();
            }

            BacktestNode node = new(registry, loggerFactory);
            IReadOnlyList<BacktestResult> results = await node.RunAsync(runs, ct).ConfigureAwait(false);
            foreach (BacktestResult result in results)
            {
                Console.WriteLine();
                Console.WriteLine(result.Summary());
            }

            return 0;
        });
        return command;
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
        Command command = new("run", "Run a live or sandbox trading node until interrupted");
        command.Options.Add(config);
        command.Options.Add(duration);
        command.SetAction(async (parseResult, ct) =>
        {
            using ILoggerFactory loggerFactory = CreateLogging(parseResult.GetValue(logLevel)!);
            PluginRegistry registry = CreateRegistry(parseResult.GetValue(plugins));
            string json = await File.ReadAllTextAsync(parseResult.GetValue(config)!, ct).ConfigureAwait(false);
            TradingNodeConfig nodeConfig = JsonSerializer.Deserialize<TradingNodeConfig>(json, BytexJson.Options) ?? throw new InvalidOperationException("Invalid node configuration.");

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
            await node.RunAsync(cts.Token).ConfigureAwait(false);
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

        Option<string> venue = new("--venue") { Description = "BINANCE | BYBIT", Required = true };
        Option<string?> quote = new("--quote") { Description = "Only instruments with this quote currency" };
        Option<bool> futures = new("--futures") { Description = "Load perpetual/futures instruments instead of spot" };
        Command fetchInstruments = new("fetch-instruments", "Download instrument definitions from a venue into the catalog");
        fetchInstruments.Options.Add(path);
        fetchInstruments.Options.Add(venue);
        fetchInstruments.Options.Add(quote);
        fetchInstruments.Options.Add(futures);
        fetchInstruments.SetAction(async (parseResult, ct) =>
        {
            using ILoggerFactory loggerFactory = CreateLogging(parseResult.GetValue(logLevel)!);
            ParquetDataCatalog cat = new(parseResult.GetValue(path)!);
            Dictionary<string, string>? filters = parseResult.GetValue(quote) is { } q ? new Dictionary<string, string> { ["quote"] = q } : null;
            bool useFutures = parseResult.GetValue(futures);
            IReadOnlyList<Instrument> instruments;
            switch (parseResult.GetValue(venue)!.ToUpperInvariant())
            {
                case "BINANCE":
                    {
                        BinanceDataClientConfig cfg = new() { AccountType = useFutures ? BinanceAccountType.UsdMFutures : BinanceAccountType.Spot };
                        using BinanceHttp http = new(cfg, loggerFactory.CreateLogger("binance"));
                        BinanceInstrumentProvider provider = new(http, cfg.AccountType, null, loggerFactory.CreateLogger("binance"));
                        await provider.LoadAllAsync(ct, filters).ConfigureAwait(false);
                        instruments = provider.GetAll();
                        break;
                    }

                case "BYBIT":
                    {
                        BybitDataClientConfig cfg = new() { ProductType = useFutures ? BybitProductType.Linear : BybitProductType.Spot };
                        using BybitHttp http = new(cfg, loggerFactory.CreateLogger("bybit"));
                        BybitInstrumentProvider provider = new(http, cfg.ProductType, null, loggerFactory.CreateLogger("bybit"));
                        await provider.LoadAllAsync(ct, filters).ConfigureAwait(false);
                        instruments = provider.GetAll();
                        break;
                    }

                default:
                    Console.Error.WriteLine("Unknown venue; expected BINANCE or BYBIT.");
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

    private static PluginRegistry CreateRegistry(string? pluginDirectory)
    {
        PluginRegistry registry = new();
        registry.AddPlugin(new BinancePlugin());
        registry.AddPlugin(new BybitPlugin());
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
