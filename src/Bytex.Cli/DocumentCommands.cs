using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Cli;

/// <summary>The <c>bytex documents</c> command group: validate, catalog, schema, examples.</summary>
internal static class DocumentCommands
{
    /// <param name="plugins">
    /// The root's <c>--plugins</c> option. A document may use node types a plugin brings (R13.8), so validating one,
    /// printing the catalog and printing the schema all have to see the same types a run would; without the option they
    /// see the built-in catalog, exactly as before.
    /// </param>
    public static Command Build(Option<string?> plugins)
    {
        Command documents = new("documents", "Work with strategy documents (the visual-strategy file format)");

        Option<string> file = new("--document", "-d") { Description = "Path to a strategy document (JSON)", Required = true };
        Option<string?> catalog = new("--catalog") { Description = "Catalog root (a directory or s3://bucket/prefix); enables instrument and data-range checks" };
        Option<string> environment = new("--environment") { Description = "backtest | sandbox | live (which mode's rules apply)", DefaultValueFactory = _ => "backtest" };
        Option<bool> json = new("--json") { Description = "Print the report as JSON" };
        Command validate = new("validate", "Validate a document: structure, semantics, and (with a catalog) context");
        validate.Options.Add(file);
        validate.Options.Add(catalog);
        validate.Options.Add(environment);
        validate.Options.Add(json);
        validate.SetAction(async (parseResult, ct) =>
        {
            // --json is a promise about the SHAPE of the answer, and every way this command can fail has to keep it.
            // It did not: the document was read BEFORE the flag was looked at, so one that could not be read printed
            // the exception's own words as prose - and something parsing the output, a script, a CI step or a model
            // deciding what to fix next, got a parse error where the finding was. The exit code was right, which is
            // what let it survive: whatever checked only the code was satisfied.
            //
            // Strict document reading did not create this. Broken JSON always did it. What changed is how often it
            // happens, because a misspelled member now stops the read rather than being skipped.
            bool asJson = parseResult.GetValue(json);

            // Written where a caller will look for it: with --json the report goes to STDOUT whatever the outcome,
            // because the exit code already carries the failure and `validate --json | jq` has to see something.
            // Prose keeps going to stderr, which is where a person's error messages belong.
            int Refused(string code, string message, string? fix)
            {
                Finding finding = new(FindingLevel.Block, code, message, Fix: fix);
                if (!asJson)
                {
                    Console.Error.WriteLine(finding.ToString() + (fix is null ? string.Empty : $"  → {fix}"));
                    return 1;
                }

                // The same keys the success path writes, so a caller parses one shape whatever happened. There is no
                // document to name, and nothing was validated, so the name is null and the stage is the one it
                // stopped at.
                Console.WriteLine(Report(new { name = (string?)null, valid = false, stoppedAt = "read", findings = new[] { finding } }));
                return 1;
            }

            string path = parseResult.GetValue(file)!;
            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // This threw with nothing handling it at all, so a path that does not exist answered with a stack
                // trace - on either stream, in neither format.
                return Refused("DOCUMENT_UNREADABLE", $"The document at '{path}' could not be read: {e.Message}", "Check the path.");
            }

            StrategyDocument document;
            try
            {
                document = DocumentJson.Deserialize(text);
            }
            catch (JsonException e)
            {
                return Refused("DOCUMENT_UNREADABLE",
                    e.Message,
                    "A member this engine does not model is refused rather than skipped; see docs/concepts/documents.md.");
            }

            string asked = parseResult.GetValue(environment) ?? nameof(TradingEnvironment.Backtest);
            if (!Enum.TryParse(asked, true, out TradingEnvironment env) || !Enum.IsDefined(env))
            {
                // Never fall back to a mode the caller did not ask for: the fallback was Backtest, the most
                // permissive of the three, so a typo in "live" reported a document as fit to trade.
                return Refused("ENVIRONMENT_UNKNOWN",
                    $"Unknown environment '{asked}'.",
                    "Use one of: backtest, sandbox, live.");
            }

            IValidationContext context = parseResult.GetValue(catalog) is { } catalogPath ? new CatalogValidationContext(new MarketArchive(catalogPath), env) : new EmptyValidationContext(env);
            ValidationReport report = new DocumentValidator(PluginNodes.Catalog(parseResult.GetValue(plugins))).Validate(document, context);
            if (asJson)
            {
                Console.WriteLine(Report(new { name = document.Name, valid = report.IsValid, stoppedAt = report.StoppedAt, findings = report.Findings }));
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
        catalogCmd.SetAction(parseResult =>
        {
            Console.WriteLine(PluginNodes.Catalog(parseResult.GetValue(plugins)).ExportJson());
            return 0;
        });

        Command schema = new("schema", "Print the JSON Schema for strategy documents");
        schema.SetAction(parseResult =>
        {
            Console.WriteLine(DocumentSchemaExporter.ExportJson(PluginNodes.Catalog(parseResult.GetValue(plugins))));
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
        private readonly MarketArchive _catalog;
        private readonly Lazy<IReadOnlyList<CatalogEntry>> _entries;

        public CatalogValidationContext(MarketArchive catalog, TradingEnvironment target)
        {
            _catalog = catalog;
            _entries = new Lazy<IReadOnlyList<CatalogEntry>>(() => catalog.Entries());
            TargetEnvironment = target;
        }

        public TradingEnvironment TargetEnvironment { get; }

        public bool ProvidesInstruments => true;

        public Instrument? Instrument(MarketKey id) => _catalog.Instrument(id);

        public (UnixNanos Start, UnixNanos End)? DataRange(CandleSeries candleSeries)
        {
            string key = candleSeries.ToString();
            CatalogEntry? entry = _entries.Value.FirstOrDefault(e => string.Equals(e.Kind, "bars", StringComparison.OrdinalIgnoreCase) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry is { Start: { } start, End: { } end } ? (start, end) : null;
        }
    }

    /// <summary>
    /// The one shape `--json` prints, success or failure. Both paths go through it so they cannot drift into two
    /// formats: a caller that parses the good case must be able to parse the bad one.
    /// </summary>
    private static string Report(object payload) =>
        JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            });

}
