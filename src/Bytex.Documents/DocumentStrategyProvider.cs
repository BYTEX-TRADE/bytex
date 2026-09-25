using System.Text.Json;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents;

/// <summary>
/// The strategy provider for documents: <c>providerId = "bytex.document"</c>, payload = <see cref="DocumentStrategyConfig"/>
/// (a <c>document</c> plus optional <c>parameterOverrides</c> and the usual strategy settings).
/// </summary>
public sealed class DocumentStrategyProvider : IStrategyProvider
{
    public const string ProviderId = "bytex.document";

    private readonly NodeCatalog _catalog;

    public DocumentStrategyProvider(NodeCatalog? catalog = null)
    {
        _catalog = catalog ?? NodeCatalog.Default;
    }

    public string Id => ProviderId;

    public IReadOnlyList<StrategyDescriptor> Describe() =>
    [
        new StrategyDescriptor("document", "Strategy document", "A visual strategy document (node graph) run by the document runtime.", JsonSerializer.SerializeToElement(DocumentSchemaExporter.Export(_catalog)))
    ];

    public ValidationResult Validate(StrategyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            DocumentStrategyConfig config = ParsePayload(definition);
            ValidationReport report = new DocumentValidator(_catalog).Validate(config.Document);
            return report.IsValid ? ValidationResult.Valid : ValidationResult.Invalid(report.Blocks.Select(b => b.ToString()).ToArray());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return ValidationResult.Invalid(e.Message);
        }
    }

    public Strategy Create(StrategyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        DocumentStrategyConfig config = ParsePayload(definition);
        return new DocumentStrategy(config, _catalog);
    }

    /// <summary>Accepts either a full config payload (<c>{"document": {...}, ...}</c>) or a bare document.</summary>
    public static DocumentStrategyConfig ParsePayload(StrategyDefinition definition)
    {
        JsonElement payload = definition.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The document payload must be a JSON object.");
        }

        DocumentStrategyConfig config;
        if (payload.TryGetProperty("documentPath", out JsonElement pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            // {"documentPath": "strategies/x.json", "parameterOverrides": {...}} — the document lives in its own file.
            string path = pathElement.GetString()!;
            StrategyDocument fromFile = DocumentJson.Deserialize(File.ReadAllText(path));
            // Environment stays null when the payload does not name one, exactly as the two shapes below leave it:
            // the strategy then runs in the environment of the node that loaded it. Defaulting it to Backtest here
            // made a document loaded by path ignore its node - a paper run got backtest rules and no warm-up history.
            config = payload.Deserialize<DocumentStrategyConfigShell>(DocumentJson.Options) is { } shell
                ? new DocumentStrategyConfig { Document = fromFile, ParameterOverrides = shell.ParameterOverrides, StrategyId = shell.StrategyId, Environment = shell.Environment }
                : new DocumentStrategyConfig { Document = fromFile };
        }
        else if (payload.TryGetProperty("document", out JsonElement documentElement))
        {
            config = payload.Deserialize<DocumentStrategyConfig>(DocumentJson.Options) ?? throw new InvalidOperationException("Cannot read the document payload.");
            if (config.Document is null)
            {
                throw new InvalidOperationException("The payload's document is empty.");
            }
        }
        else
        {
            StrategyDocument document = DocumentJson.Deserialize(payload);
            config = new DocumentStrategyConfig { Document = document };
        }

        if (definition.Config is not null)
        {
            config = config with
            {
                StrategyId = definition.Config.StrategyId ?? config.StrategyId,
                OrderIdTag = definition.Config.OrderIdTag ?? config.OrderIdTag,
                OmsType = definition.Config.OmsType != Core.Model.OmsType.Unspecified ? definition.Config.OmsType : config.OmsType,
                ExternalOrderClaims = definition.Config.ExternalOrderClaims.Count > 0 ? definition.Config.ExternalOrderClaims : config.ExternalOrderClaims,
                ManageContingentOrders = definition.Config.ManageContingentOrders || config.ManageContingentOrders,
                ManageGtdExpiry = definition.Config.ManageGtdExpiry || config.ManageGtdExpiry,
            };
        }

        if (config.StrategyId is null)
        {
            string tag = new string(config.Document.Name.Where(char.IsLetterOrDigit).Take(16).ToArray());
            config = config with { StrategyId = new Core.Model.Identifiers.StrategyId($"{(tag.Length == 0 ? "Document" : tag)}-{config.Document.Id[..Math.Min(6, config.Document.Id.Length)]}") };
        }

        return config;
    }
}

/// <summary>The optional fields that may accompany a <c>documentPath</c> payload.</summary>
internal sealed record DocumentStrategyConfigShell
{
    public IReadOnlyDictionary<string, decimal>? ParameterOverrides { get; init; }

    public Core.Model.Identifiers.StrategyId? StrategyId { get; init; }

    public Core.Model.TradingEnvironment? Environment { get; init; }
}

/// <summary>Registers the document provider and the built-in catalog when loaded as a plugin.</summary>
public sealed class DocumentsPlugin : IPlugin
{
    public string Id => "bytex.documents";

    public string Version => typeof(DocumentsPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddStrategyProvider(new DocumentStrategyProvider());
    }
}
