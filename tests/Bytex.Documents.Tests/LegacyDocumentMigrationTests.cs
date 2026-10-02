using System.Text.Json;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Tests;

public sealed class LegacyDocumentMigrationTests
{
    private const string Legacy = """
        {"schemaVersion":"1.0","name":"Original","instruments":[{"ref":"primary","instrumentId":"EUR/USD.SIM"}],
        "barTypes":[{"ref":"main","instrument":"primary","step":5,"aggregation":"minute","priceType":"last","source":"external"}],
        "nodes":[{"id":"bars","type":"data.bars","params":{"barType":"main"}}]}
        """;

    [Fact]
    public void Migration_preserves_symbol_and_sampling_and_renames_node_references()
    {
        StrategyDocument converted = DocumentJson.Deserialize(LegacyDocumentMigration.Convert(Legacy));
        Assert.Equal("2.0", converted.SchemaVersion);
        Assert.Equal("Original", converted.Name);
        Assert.Equal("bx-market:v2/SIM/EUR%2FUSD", converted.Instruments[0].MarketKey);
        Assert.Equal(5, converted.CandleSeriesDefinitions[0].Step);
        Assert.Equal("provider", converted.CandleSeriesDefinitions[0].Source);
        Assert.Equal("main", converted.Nodes[0].Params!.Value.GetProperty("candleSeries").GetString());
    }

    [Fact]
    public void Unknown_settings_are_refused_instead_of_lost()
    {
        string unknown = Legacy.Replace("\"name\":\"Original\"", "\"name\":\"Original\",\"unknownSetting\":true", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => LegacyDocumentMigration.Convert(unknown));
    }

    [Fact]
    public void Missing_ids_are_source_derived_and_repeated_conversion_is_identical()
    {
        string first = LegacyDocumentMigration.Convert(Legacy);
        Assert.Equal(first, LegacyDocumentMigration.Convert(Legacy));
        StrategyDocument document = DocumentJson.Deserialize(first);
        Assert.Matches("^[a-f0-9]{32}$", document.Id);
        string changed = Legacy.Replace("Original", "Different", StringComparison.Ordinal);
        Assert.NotEqual(document.Id, DocumentJson.Deserialize(LegacyDocumentMigration.Convert(changed)).Id);
    }

    [Fact]
    public void Explicit_legacy_ids_are_preserved_verbatim()
    {
        string input = Legacy.Replace("\"name\":", "\"id\":\"owner-stable-id\",\"name\":", StringComparison.Ordinal);
        Assert.Equal("owner-stable-id", DocumentJson.Deserialize(LegacyDocumentMigration.Convert(input)).Id);
    }

    [Fact]
    public void Migration_does_not_accept_unknown_or_already_migrated_versions()
    {
        Assert.Throws<JsonException>(() => LegacyDocumentMigration.Convert(Legacy.Replace("1.0", "2.0", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => LegacyDocumentMigration.Convert(Legacy.Replace("1.0", "9.0", StringComparison.Ordinal)));
    }
}
