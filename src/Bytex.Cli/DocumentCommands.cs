using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Cli;

/// <summary>The <c>bytex documents</c> command group: validate, catalog, schema, examples.</summary>
internal static class DocumentCommands
{
    public static Command Build()
    {
        Command documents = new("documents", "Work with strategy documents (the visual-strategy file format)");

        Option<string> file = new("--document", "-d") { Description = "Path to a strategy document (JSON)", Required = true };
        Option<string?> catalog = new("--catalog") { Description = "Catalog root; enables instrument and data-range checks" };
        Option<string> environment = new("--environment") { Description = "backtest | sandbox | live (which mode's rules apply)", DefaultValueFactory = _ => "backtest" };
        Option<bool> json = new("--json") { Description = "Print the report as JSON" };
        Command validate = new("validate", "Validate a document: structure, semantics, and (with a catalog) context");
        validate.Options.Add(file);
        validate.Options.Add(catalog);
        validate.Options.Add(environment);
        validate.Options.Add(json);
        validate.SetAction(async (parseResult, ct) =>
        {
            string text = await File.ReadAllTextAsync(parseResult.GetValue(file)!, ct).ConfigureAwait(false);
            StrategyDocument document;
            try
            {
                document = DocumentJson.Deserialize(text);
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine("BLOCK JSON: " + e.Message);
                return 1;
            }

            string asked = parseResult.GetValue(environment) ?? nameof(TradingEnvironment.Backtest);
            if (!Enum.TryParse(asked, true, out TradingEnvironment env) || !Enum.IsDefined(env))
            {
                // Never fall back to a mode the caller did not ask for: the fallback was Backtest, the most
                // permissive of the three, so a typo in "live" reported a document as fit to trade.
                Console.Error.WriteLine($"Unknown environment '{asked}'. Use one of: backtest, sandbox, live.");
                return 1;
            }

            IValidationContext context = parseResult.GetValue(catalog) is { } catalogPath ? new CatalogValidationContext(new ParquetDataCatalog(catalogPath), env) : new EmptyValidationContext(env);
            ValidationReport report = new DocumentValidator().Validate(document, context);
            if (parseResult.GetValue(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { document.Name, valid = report.IsValid, stoppedAt = report.StoppedAt, findings = report.Findings }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }));
            }
            else
            {
                foreach (Finding finding in report.Findings)
                {
                    Console.WriteLine(finding.ToString() + (finding.Fix is null ? string.Empty : $"  → {finding.Fix}"));
                }

                Console.WriteLine(report.IsValid ? $"{document.Name}: valid ({report.Warnings.Count()} warnings)" : $"{document.Name}: {report.Blocks.Count()} blocking findings");
            }

            return report.IsValid ? 0 : 1;
        });

        Command catalogCmd = new("catalog", "Print the node catalog (types, ports, parameters) as JSON");
        catalogCmd.SetAction(_ =>
        {
            Console.WriteLine(NodeCatalog.Default.ExportJson());
            return 0;
        });

        Command schema = new("schema", "Print the JSON Schema for strategy documents");
        schema.SetAction(_ =>
        {
            Console.WriteLine(DocumentSchemaExporter.ExportJson());
            return 0;
        });

        Option<string> outDir = new("--out", "-o") { Description = "Directory to write the example documents into", DefaultValueFactory = _ => "documents" };
        Command examples = new("examples", "Write the built-in example documents to a directory");
        examples.Options.Add(outDir);
        examples.SetAction(async (parseResult, ct) =>
        {
            string dir = parseResult.GetValue(outDir)!;
            Directory.CreateDirectory(dir);
            Assembly assembly = typeof(DocumentStrategy).Assembly;
            int count = 0;
            foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Bytex.Documents.Examples.", StringComparison.Ordinal)))
            {
                string name = resource["Bytex.Documents.Examples.".Length..];
                await using Stream stream = assembly.GetManifestResourceStream(resource)!;
                await using FileStream target = File.Create(Path.Combine(dir, name));
                await stream.CopyToAsync(target, ct).ConfigureAwait(false);
                Console.WriteLine($"wrote {Path.Combine(dir, name)}");
                count++;
            }

            return count > 0 ? 0 : 1;
        });

        documents.Subcommands.Add(validate);
        documents.Subcommands.Add(catalogCmd);
        documents.Subcommands.Add(schema);
        documents.Subcommands.Add(examples);
        return documents;
    }

    /// <summary>Validation context backed by a Parquet catalog: instruments and bar data ranges.</summary>
    private sealed class CatalogValidationContext : IValidationContext
    {
        private readonly ParquetDataCatalog _catalog;
        private readonly Lazy<IReadOnlyList<CatalogEntry>> _entries;

        public CatalogValidationContext(ParquetDataCatalog catalog, TradingEnvironment target)
        {
            _catalog = catalog;
            _entries = new Lazy<IReadOnlyList<CatalogEntry>>(catalog.Entries);
            TargetEnvironment = target;
        }

        public TradingEnvironment TargetEnvironment { get; }

        public bool ProvidesInstruments => true;

        public Instrument? Instrument(InstrumentId id) => _catalog.Instrument(id);

        public (UnixNanos Start, UnixNanos End)? DataRange(BarType barType)
        {
            string key = barType.ToString();
            CatalogEntry? entry = _entries.Value.FirstOrDefault(e => string.Equals(e.Kind, "bars", StringComparison.OrdinalIgnoreCase) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry is { Start: { } start, End: { } end } ? (start, end) : null;
        }
    }
}
