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

    /// <summary>
    /// Binance's coin-margined perpetual and one quarterly as that family really publishes them - tradability in
    /// contractStatus, a contract size, quantities in whole contracts - plus one the venue is not trading yet.
    /// </summary>
    private const string CoinMExchangeInfo = """
        {
          "timezone": "UTC", "serverTime": 1700000000000,
          "symbols": [
            {
              "symbol": "BTCUSD_PERP", "pair": "BTCUSD", "contractType": "PERPETUAL",
              "deliveryDate": 4133404800000, "onboardDate": 1597042800000, "contractStatus": "TRADING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "BTC", "quoteAsset": "USD", "marginAsset": "BTC",
              "pricePrecision": 1, "quantityPrecision": 0, "contractSize": 100,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "1000", "maxPrice": "4520958", "tickSize": "0.1" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" }
              ]
            },
            {
              "symbol": "BTCUSD_261225", "pair": "BTCUSD", "contractType": "CURRENT_QUARTER",
              "deliveryDate": 1798185600000, "onboardDate": 1766563200000, "contractStatus": "TRADING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "BTC", "quoteAsset": "USD", "marginAsset": "BTC",
              "pricePrecision": 1, "quantityPrecision": 0, "contractSize": 100,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "2109.4", "maxPrice": "3515698.4", "tickSize": "0.1" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" }
              ]
            },
            {
              "symbol": "EGLDUSD_PERP", "pair": "EGLDUSD", "contractType": "PERPETUAL",
              "deliveryDate": 4133404800000, "onboardDate": 1766563200000, "contractStatus": "PENDING_TRADING",
              "baseAsset": "EGLD", "quoteAsset": "USD", "marginAsset": "EGLD",
              "pricePrecision": 3, "quantityPrecision": 0, "contractSize": 10,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.5", "maxPrice": "10000", "tickSize": "0.001" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Binances_coin_margined_family_is_reachable_through_instrument_type()
    {
        // Binance has three markets and --futures can only choose between two, so the third is named the way OKX's
        // third is. A capability nothing can reach is not a capability, and this switch is the only way a user gets
        // at it - which is why the test is here as well as in the adapter's own suite.
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Json(CoinMExchangeInfo));
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(
            ["catalog", "fetch-instruments", "--path", catalog, "--venue", "BINANCE", "--instrument-type", "CoinMFutures", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("/dapi/v1/exchangeInfo", venue.Requests.Select(r => r.Path));

        // Two of the three: the one the venue is not trading yet is left out, which is the field this family
        // publishes under a different name from its sibling's.
        IReadOnlyList<Instrument> stored = new ParquetDataCatalog(catalog).Instruments();
        Assert.Equal(["BTCUSD_261225.BINANCE", "BTCUSD_PERP.BINANCE"], stored.Select(i => i.Id.Value).Order());

        // And what is stored is a coin-margined contract rather than a linear one under another name: inverse,
        // sized in whole contracts, carrying the venue's own contract size as its multiplier.
        Instrument perp = stored.Single(i => i.Id == InstrumentId.Parse("BTCUSD_PERP.BINANCE"));
        Assert.True(perp.IsInverse);
        Assert.Equal(new Quantity(1m, 0), perp.SizeIncrement);
        Assert.Equal(100m, perp.Multiplier.Value);
        Assert.Equal(Currencies.BTC, perp.SettlementCurrency);
    }

    [Fact]
    public async Task The_usd_margined_family_is_still_what_futures_means_on_binance()
    {
        // The switch that existed before there were three markets keeps meaning what it meant: a stored script must
        // not start fetching a different market because a family was added beside it.
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(_ => StubResponse.Json("""{"timezone":"UTC","serverTime":1700000000000,"symbols":[]}"""));

        CliResult result = await CliRunner.RunAsync(
            ["catalog", "fetch-instruments", "--path", temp.Combine("catalog"), "--venue", "BINANCE", "--futures", "--base-url", venue.HttpBase]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/fapi/v1/exchangeInfo", venue.Requests.Select(r => r.Path));
        Assert.DoesNotContain("/dapi/v1/exchangeInfo", venue.Requests.Select(r => r.Path));
    }
}
