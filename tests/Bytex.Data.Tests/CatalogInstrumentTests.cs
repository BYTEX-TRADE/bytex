using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// The backtest node refuses to run without the instrument definition, and `bytex catalog import-csv`
// takes its precisions from it, so storing and finding definitions is the first thing a catalog must get right.
public sealed class CatalogInstrumentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Every_instrument_class_is_written_and_read_back_with_all_properties()
    {
        MarketArchive catalog = new(_temp.Root);
        IReadOnlyList<Instrument> originals = TestInstruments.All();

        await catalog.WriteInstrumentsAsync(originals);
        MarketArchive reopened = new(_temp.Root);

        IReadOnlyList<Instrument> restored = reopened.Instruments();
        Assert.Equal(originals.Count, restored.Count);
        foreach (Instrument original in originals)
        {
            InstrumentAssert.Same(original, restored.Single(i => i.Id == original.Id));
        }
    }

    [Fact]
    public async Task Instruments_can_be_filtered_by_venue()
    {
        MarketArchive catalog = new(_temp.Root);
        await catalog.WriteInstrumentsAsync(TestInstruments.All());

        IReadOnlyList<Instrument> binance = catalog.Instruments(new Venue("BINANCE"));

        Assert.Equal(["bx-market:v2/BINANCE/BTCUSDT", "bx-market:v2/BINANCE/BTCUSDT_250328"], binance.Select(i => i.Id.Value).Order(StringComparer.Ordinal));
        Assert.Empty(catalog.Instruments(new Venue("KRAKEN")));
    }

    [Fact]
    public async Task A_single_instrument_is_found_by_id()
    {
        MarketArchive catalog = new(_temp.Root);
        await catalog.WriteInstrumentsAsync(TestInstruments.All());

        Instrument? found = catalog.Instrument(MarketKey.Parse("bx-market:v2/XCME/ESH5"));

        Assert.NotNull(found);
        InstrumentAssert.Same(TestInstruments.EsFuture(), found);
    }

    [Fact]
    public async Task A_symbol_containing_a_slash_is_stored_and_found_again()
    {
        MarketArchive catalog = new(_temp.Root);
        await catalog.WriteInstrumentsAsync([TestInstruments.EurUsd()]);

        Instrument? found = catalog.Instrument(MarketKey.Parse("bx-market:v2/SIM/EUR%2FUSD"));

        Assert.NotNull(found);
        Assert.Equal("bx-market:v2/SIM/EUR%2FUSD", found.Id.Value);
        Assert.Equal("bx-market:v2/SIM/EUR%2FUSD", Assert.Single(catalog.Instruments()).Id.Value);
    }

    [Fact]
    public async Task An_unknown_id_gives_null_rather_than_an_exception()
    {
        MarketArchive catalog = new(_temp.Root);
        await catalog.WriteInstrumentsAsync([TestInstruments.BtcUsdt()]);

        Assert.Null(catalog.Instrument(MarketKey.Parse("bx-market:v2/BINANCE/DOGEUSDT")));
    }

    [Fact]
    public void An_empty_catalog_has_no_instruments()
    {
        MarketArchive catalog = new(_temp.Combine("fresh"));

        Assert.Empty(catalog.Instruments());
        Assert.Null(catalog.Instrument(MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT")));
    }

    [Fact]
    public async Task Writing_the_same_id_again_replaces_the_definition()
    {
        MarketArchive catalog = new(_temp.Root);
        await catalog.WriteInstrumentsAsync([TestInstruments.AppleEquity()]);

        Equity updated = new(
            new InstrumentSpec
            {
                Id = TestInstruments.AppleEquity().Id,
                AssetClass = AssetClass.Equity,
                InstrumentClass = InstrumentClass.Spot,
                QuoteCurrency = Currencies.USD,
                PricePrecision = 4,
                SizePrecision = 0,
                PriceIncrement = Price.Parse("0.0001"),
                SizeIncrement = Quantity.Parse("1"),
            },
            "US0378331005");
        await catalog.WriteInstrumentsAsync([updated]);

        Instrument only = Assert.Single(catalog.Instruments());
        Assert.Equal((byte)4, only.PricePrecision);
    }

    [Fact]
    public void The_root_path_is_made_absolute_and_created()
    {
        string root = _temp.Combine("nested", "catalog");

        MarketArchive catalog = new(root);

        Assert.True(Path.IsPathFullyQualified(catalog.RootPath));
        Assert.Equal(Path.GetFullPath(root), catalog.RootPath);
        Assert.True(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_root_path_is_rejected(string root)
    {
        Assert.Throws<ArgumentException>(() => new MarketArchive(root));
    }
}
