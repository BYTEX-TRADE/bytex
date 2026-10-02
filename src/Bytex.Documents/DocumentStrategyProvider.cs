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

        // The path shape becomes the inline shape, and then there is ONE deserialization for both.
        //
        // It used to have a deserialization of its own, into a record carrying three of this config's eighteen
        // members, so a payload naming any of the other fifteen was read and most of it discarded in silence. What
        // that cost was not evenly spread: "useHyphensInClientOrderIds": false stayed true, and a venue that
        // refuses a hyphen in a client order id refused every order the document placed - while the adapter's own
        // message told the operator to set the flag that could not be set. warmupBars went the same way, which is a
        // live node starting with empty indicators.
        //
        // A list of fields to keep in step with a record is a list that falls behind it, and this one already had:
        // "environment" was added when a paper run came up with backtest rules, and the rest were left. So the list
        // is gone rather than longer, and a member added to the config from now on is honoured by both shapes with
        // nobody editing this method.
        DocumentStrategyConfig config;
        if (payload.TryGetProperty("documentPath", out JsonElement pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            config = Settings(WithDocumentFrom(payload, pathElement.GetString()!));
        }
        else if (payload.TryGetProperty("document", out _))
        {
            config = Settings(payload);
        }
        else
        {
            config = new DocumentStrategyConfig { Document = DocumentJson.Deserialize(payload) };
        }

        // What the NODE said about this strategy, over what the payload said.
        //
        // Every member of StrategyConfig and of RuntimeModuleConfig is here, and that is the point: this was a list of six
        // where StrategyConfig has seven, and the seventh was UseHyphensInClientOrderIds - so that setting could
        // not be reached from a node's own config block either, by any shape of payload.
        //
        // A member with no "unset" value is combined rather than chosen. A flag defaulting to FALSE is on if either
        // says on; a flag defaulting to TRUE is off if either says off. Nothing else is defensible while the member
        // is not nullable: a false that came from a default cannot be told from a false somebody wrote, and picking
        // a winner would silently discard the other - which is the defect this method already had once.
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
                UseHyphensInClientOrderIds = definition.Config.UseHyphensInClientOrderIds && config.UseHyphensInClientOrderIds,
                RuntimeModuleId = definition.Config.RuntimeModuleId ?? config.RuntimeModuleId,
                LogEvents = definition.Config.LogEvents && config.LogEvents,
                LogCommands = definition.Config.LogCommands && config.LogCommands,
            };
        }

        if (config.StrategyId is null)
        {
            string tag = new string(config.Document.Name.Where(char.IsLetterOrDigit).Take(16).ToArray());
            config = config with { StrategyId = new Core.Model.Identifiers.StrategyId($"{(tag.Length == 0 ? "Document" : tag)}-{config.Document.Id[..Math.Min(6, config.Document.Id.Length)]}") };
        }

        return config;
    }

    /// <summary>
    /// The payload as the inline shape: everything it said, with the document read from the file it named.
    ///
    /// <para>
    /// Rewriting the JSON rather than copying fields across is what makes the two shapes the same code. The element
    /// returned is CLONED, because a document holds a JsonElement for a node's params and for its layout, and those
    /// would dangle the moment the JsonDocument they were parsed from left scope.
    /// </para>
    /// </summary>
    private static JsonElement WithDocumentFrom(JsonElement payload, string path)
    {
        if (payload.TryGetProperty("document", out _))
        {
            throw new InvalidOperationException(
                "This payload says where its document is twice: 'documentPath' names a file and 'document' carries "
                + "one inline. One would be read and the other ignored, so the run would trace to a document nobody "
                + "can point at afterwards. Keep one.");
        }

        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(path));
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in payload.EnumerateObject())
            {
                if (!property.NameEquals("documentPath"))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WritePropertyName("document");
            file.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        using JsonDocument rewritten = JsonDocument.Parse(buffer.ToArray());
        return rewritten.RootElement.Clone();
    }

    /// <summary>
    /// The whole payload, settings and all. Nothing here names a member, so nothing here can fall behind the record.
    /// </summary>
    private static DocumentStrategyConfig Settings(JsonElement payload)
    {
        DocumentStrategyConfig config = payload.Deserialize<DocumentStrategyConfig>(DocumentJson.Options)
            ?? throw new InvalidOperationException("Cannot read the document payload.");

        return config.Document is null
            ? throw new InvalidOperationException("The payload's document is empty.")
            : config;
    }
}

/// <summary>Registers the document provider and the built-in catalog when loaded as a plugin.</summary>
public sealed class DocumentsPlugin : IPlugin
{
    private readonly NodeCatalog _catalog;

    /// <summary>The documents plugin with the built-in node types alone.</summary>
    public DocumentsPlugin()
        : this(NodeCatalog.Default)
    {
    }

    /// <summary>
    /// The documents plugin with a catalog of the host's own (R13.8): the built-in types plus whatever node-type
    /// providers it loaded. A host composes that catalog with <see cref="NodeCatalogComposer.Compose"/>.
    /// </summary>
    public DocumentsPlugin(NodeCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public string Id => "bytex.documents";

    public string Version => typeof(DocumentsPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddStrategyProvider(new DocumentStrategyProvider(_catalog));
    }
}
