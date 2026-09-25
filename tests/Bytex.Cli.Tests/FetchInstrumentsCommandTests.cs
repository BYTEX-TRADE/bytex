using Bytex.Cli.Tests.Support;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data;

namespace Bytex.Cli.Tests;

// Why: `catalog fetch-instruments` is the first command the documentation asks a new user to run, and the only one
// that turns a venue's answer into the catalog every later command reads. Its refusals were already pinned; this is
// the path that works - the venue is a stub on 127.0.0.1, reached through --base-url.
public sealed class FetchInstrumentsCommandTests
{
    private const string SpotExchangeInfo = """
        {
          "timezone": "UTC",
          "serverTime": 1700000000000,
          "symbols": [
            {
              "symbol": "BTCUSDT", "status": "TRADING",
              "baseAsset": "BTC", "baseAssetPrecision": 8, "quoteAsset": "USDT", "quotePrecision": 8, "quoteAssetPrecision": 8,
              "orderTypes": ["LIMIT", "MARKET"],
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.01000000", "maxPrice": "1000000.00000000", "tickSize": "0.01000000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00001000", "maxQty": "9000.00000000", "stepSize": "0.00001000" },
                { "filterType": "NOTIONAL", "minNotional": "5.00000000", "maxNotional": "9000000.00000000" }
              ],
              "permissions": []
            },
            {
              "symbol": "ETHBTC", "status": "TRADING",
              "baseAsset": "ETH", "baseAssetPrecision": 8, "quoteAsset": "BTC", "quotePrecision": 8, "quoteAssetPrecision": 8,
              "orderTypes": ["LIMIT"],
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.00001000", "maxPrice": "922327.00000000", "tickSize": "0.00001000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00010000", "maxQty": "100000.00000000", "stepSize": "0.00010000" }
              ],
              "permissions": []
            },
            {
              "symbol": "LUNAUSDT", "status": "BREAK",
              "baseAsset": "LUNA", "baseAssetPrecision": 8, "quoteAsset": "USDT", "quotePrecision": 8, "quoteAssetPrecision": 8,
              "orderTypes": ["LIMIT"],
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.00010000", "maxPrice": "1000.00000000", "tickSize": "0.00010000" },
                { "filterType": "LOT_SIZE", "minQty": "0.01000000", "maxQty": "9000000.00000000", "stepSize": "0.01000000" }
              ],
              "permissions": []
            }
          ]
        }
        """;

    [Fact]
    public async Task What_the_venue_lists_is_stored_in_the_catalog_with_its_grid_and_its_limits()
    {
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Json(SpotExchangeInfo));
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", catalog, "--venue", "BINANCE", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Stored 2 instruments", result.StdOut, StringComparison.Ordinal); // the halted pair is left out
        IReadOnlyList<Instrument> stored = new ParquetDataCatalog(catalog).Instruments();
        Assert.Equal(["BTCUSDT.BINANCE", "ETHBTC.BINANCE"], stored.Select(i => i.Id.Value).Order());
        Instrument btc = stored.Single(i => i.Id == InstrumentId.Parse("BTCUSDT.BINANCE"));
        Assert.Equal(new Price(0.01m, 2), btc.PriceIncrement);
        Assert.Equal(new Quantity(0.00001m, 5), btc.SizeIncrement);
        Assert.Equal(new Money(5m, Currencies.USDT), btc.MinNotional);
    }

    [Fact]
    public async Task The_quote_filter_narrows_what_is_stored()
    {
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Json(SpotExchangeInfo));
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", catalog, "--venue", "BINANCE", "--quote", "USDT", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Instrument only = Assert.Single(new ParquetDataCatalog(catalog).Instruments());
        Assert.Equal(InstrumentId.Parse("BTCUSDT.BINANCE"), only.Id);
    }

    [Fact]
    public async Task A_venue_that_answers_with_an_error_stores_nothing_and_exits_non_zero()
    {
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Error(418, """{"code":-1003,"msg":"Way too many requests"}"""));
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", catalog, "--venue", "BINANCE", "--base-url", venue.HttpBase]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(new ParquetDataCatalog(catalog).Instruments());
    }

    private const string KucoinContracts = """
        {"code":"200000","data":[
          {"symbol":"XBTUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":0.001,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,"maxPrice":1000000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":null},
          {"symbol":"XBTUSDM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USD","settleCurrency":"XBT",
           "multiplier":-1.0,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,"maxPrice":1000000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":true,"expireDate":null}
        ]}
        """;

    [Fact]
    public async Task KuCoin_perpetuals_are_fetched_into_the_catalog_like_any_other_venues()
    {
        // This command used to answer --futures for KuCoin with "not available for it" and exit 1. The adapter now
        // has the family, and a capability nothing can reach is not a capability: the test is here rather than only
        // in the adapter's own suite because this switch is the only way a user gets at it.
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Json(KucoinContracts));
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", catalog, "--venue", "KUCOIN", "--futures", "--base-url", venue.HttpBase]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/api/v1/contracts/active", venue.Requests.Select(r => r.Path));

        // One instrument, not two: the venue listed an inverse contract beside the linear one and it is not in the
        // catalog, because a quantity of an inverse contract cannot be expressed in base currency without a price.
        Instrument stored = Assert.Single(new ParquetDataCatalog(catalog).Instruments());
        Assert.Equal("XBTUSDT-PERP.KUCOIN", stored.Id.ToString());

        // In base currency, not in contracts - the same unit a user sizes in on every other venue.
        Assert.Equal(new Quantity(0.001m, 3), stored.SizeIncrement);
    }

    [Fact]
    public async Task KuCoin_spot_is_still_what_the_command_fetches_when_futures_is_not_asked_for()
    {
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Json("""{"code":"200000","data":[]}"""));

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", temp.Combine("catalog"), "--venue", "KUCOIN", "--base-url", venue.HttpBase]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/api/v2/symbols", venue.Requests.Select(r => r.Path));
    }
}
