using System.Text.Json;
using Bytex.Cli.Tests.Support;
using Bytex.Data;
using Parquet.Serialization;

namespace Bytex.Cli.Tests;

public sealed class MigrationCommandTests
{
    private const string LegacyDocument = """
        {"schemaVersion":"1.0","name":"Original","instruments":[{"ref":"primary","instrumentId":"EUR/USD.SIM"}],
        "barTypes":[{"ref":"main","instrument":"primary","step":5,"aggregation":"minute","priceType":"last","source":"external"}],
        "nodes":[{"id":"bars","type":"data.bars","params":{"barType":"main"}}]}
        """;

    [Fact]
    public async Task Document_conversion_preserves_original_and_repeats_without_overwrite()
    {
        using TempDirectory temp = new();
        string source = temp.File("original.json", LegacyDocument), target = temp.Combine("v2.json");
        string[] args = ["migrate", "document", "--source", source, "--destination", target];
        CliResult converted = await CliRunner.RunAsync(args);
        Assert.True(converted.ExitCode == 0, converted.AllOutput);
        Assert.Equal(LegacyDocument, File.ReadAllText(source));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(target));
        Assert.Equal("2.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("bx-market:v2/SIM/EUR%2FUSD", json.RootElement.GetProperty("instruments")[0].GetProperty("marketKey").GetString());
        CliResult repeated = await CliRunner.RunAsync(args);
        Assert.True(repeated.ExitCode == 0, repeated.AllOutput);
        File.WriteAllText(target, "owner content");
        Assert.NotEqual(0, (await CliRunner.RunAsync(args)).ExitCode);
        Assert.Equal("owner content", File.ReadAllText(target));
        Assert.NotEqual(0, (await CliRunner.RunAsync(["migrate", "document", "--source", source, "--destination", source])).ExitCode);
        Assert.Equal(LegacyDocument, File.ReadAllText(source));
    }

    [Fact]
    public async Task Archive_conversion_resumes_and_recovery_output_does_not_delete_objects()
    {
        using TempDirectory temp = new();
        string source = temp.Combine("source"), target = temp.Combine("target");
        LocalObjectStore original = new(source);
        const string key = "quotes/BTCUSDT.SIM/10-10.parquet";
        await original.WriteAsync(key, stream => ParquetSerializer.SerializeAsync(new[] { new LegacyQuote() }, stream));
        byte[] bytes = File.ReadAllBytes(Path.Combine(source, key));
        string[] args = ["migrate", "archive", "--source", source, "--destination", target];
        CliResult converted = await CliRunner.RunAsync(args);
        Assert.True(converted.ExitCode == 0, converted.AllOutput);
        CliResult resumed = await CliRunner.RunAsync(args);
        Assert.True(resumed.ExitCode == 0, resumed.AllOutput);
        MarketArchive archive = new(target);
        Assert.Single(archive.Segments("quotes", "bx-market:v2/SIM/BTCUSDT"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(source, key)));
        string orphan = "quotes/" + Bytex.Core.Serialization.ObjectKeyCodec.Encode("bx-market:v2/SIM/BTCUSDT") + "/segments/" + Guid.NewGuid().ToString("N") + ".parquet";
        await archive.Store.WriteTextAsync(orphan, "retained staging bytes");
        CliResult recovery = await CliRunner.RunAsync(["catalog", "recovery", "--path", target]);
        Assert.True(recovery.ExitCode == 0, recovery.AllOutput);
        using JsonDocument report = JsonDocument.Parse(recovery.StdOut);
        Assert.Single(report.RootElement.EnumerateArray());
        Assert.Contains("uncommitted", recovery.StdOut, StringComparison.Ordinal);
        Assert.True(archive.Store.Exists(orphan));
        Assert.NotEqual(0, (await CliRunner.RunAsync(["migrate", "archive", "--source", source, "--destination", source])).ExitCode);
    }

    [Fact]
    public async Task Legacy_csv_flag_is_refused_and_v2_option_is_advertised()
    {
        CliResult help = await CliRunner.RunAsync(["catalog", "import-csv", "--help"]);
        Assert.Contains("--candle-series", help.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("--bar-type", help.StdOut, StringComparison.Ordinal);
        Assert.NotEqual(0, (await CliRunner.RunAsync(["catalog", "import-csv", "--bar-type", "BTCUSDT.SIM-1-MINUTE-LAST-EXTERNAL"])).ExitCode);
    }

    public sealed class LegacyQuote
    {
        public long TsEvent { get; set; } = 9;
        public long TsInit { get; set; } = 10;
        public decimal Bid { get; set; } = 1.2345m;
        public decimal Ask { get; set; } = 1.2346m;
        public decimal BidSize { get; set; } = 0.123m;
        public decimal AskSize { get; set; } = 0.456m;
        public byte PricePrecision { get; set; } = 4;
        public byte SizePrecision { get; set; } = 3;
    }
}
