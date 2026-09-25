using Bytex.Adapters.Binance;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Live.Network;

namespace Bytex.Adapters.Tests.Binance;

// Why: a wrong base URL sends futures orders to the spot API,
// and a wrong symbol mapping sends them to the wrong market. Expected values are the hosts, routes and naming
// rules from the Binance documentation and docs/integrations/binance.md.
public sealed class BinanceVenueTests
{
    [Theory]
    [InlineData(BinanceAccountType.Spot, "https://api.binance.com")]
    [InlineData(BinanceAccountType.UsdMFutures, "https://fapi.binance.com")]
    [InlineData(BinanceAccountType.CoinMFutures, "https://dapi.binance.com")]
    public void Rest_base_url_depends_on_the_account_type(BinanceAccountType type, string expected)
    {
        BinanceDataClientConfig config = new() { AccountType = type };

        Assert.Equal(expected, BinanceVenue.HttpBase(config));
    }

    [Theory]
    [InlineData(BinanceAccountType.Spot, "wss://stream.binance.com:9443")]
    [InlineData(BinanceAccountType.UsdMFutures, "wss://fstream.binance.com")]
    [InlineData(BinanceAccountType.CoinMFutures, "wss://dstream.binance.com")]
    public void Stream_base_url_depends_on_the_account_type(BinanceAccountType type, string expected)
    {
        BinanceExecutionClientConfig config = new() { AccountType = type };

        Assert.Equal(expected, BinanceVenue.WsBase(config));
    }

    [Fact]
    public void Explicit_base_urls_override_the_defaults_for_both_transports()
    {
        BinanceDataClientConfig config = new() { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = "http://127.0.0.1:9", BaseUrlWs = "ws://127.0.0.1:9" };

        Assert.Equal("http://127.0.0.1:9", BinanceVenue.HttpBase(config));
        Assert.Equal("ws://127.0.0.1:9", BinanceVenue.WsBase(config));
    }

    [Theory]
    [InlineData(BinanceAccountType.Spot, "/api/v3")]
    [InlineData(BinanceAccountType.UsdMFutures, "/fapi/v1")]
    [InlineData(BinanceAccountType.CoinMFutures, "/dapi/v1")]
    public void Rest_prefix_separates_the_spot_and_futures_apis(BinanceAccountType type, string expected)
    {
        Assert.Equal(expected, BinanceVenue.ApiPrefix(type));
    }

    [Theory]
    [InlineData("BTCUSDT", BinanceAccountType.Spot, "PERPETUAL", "BTCUSDT.BINANCE")]
    [InlineData("BTCUSDT", BinanceAccountType.UsdMFutures, "PERPETUAL", "BTCUSDT-PERP.BINANCE")]
    [InlineData("BTCUSDT_250926", BinanceAccountType.UsdMFutures, "CURRENT_QUARTER", "BTCUSDT_250926.BINANCE")]
    [InlineData("ETHUSDT_251226", BinanceAccountType.UsdMFutures, "NEXT_QUARTER", "ETHUSDT_251226.BINANCE")]
    [InlineData("BTCUSDT_250926", BinanceAccountType.UsdMFutures, "PERPETUAL", "BTCUSDT_250926.BINANCE")] // streams and order rows carry no contract type
    [InlineData("1000SHIBUSDT", BinanceAccountType.UsdMFutures, "PERPETUAL", "1000SHIBUSDT-PERP.BINANCE")]

    // The coin-margined family marks its own perpetuals, with an underscore where the engine's convention uses a
    // dash, so nothing is added: a second marking of the same fact would have to be undone from an id that no
    // longer says which family it came from.
    [InlineData("BTCUSD_PERP", BinanceAccountType.CoinMFutures, "PERPETUAL", "BTCUSD_PERP.BINANCE")]
    [InlineData("BTCUSD_261225", BinanceAccountType.CoinMFutures, "CURRENT_QUARTER", "BTCUSD_261225.BINANCE")]
    [InlineData("BTCUSD_270326", BinanceAccountType.CoinMFutures, "NEXT_QUARTER", "BTCUSD_270326.BINANCE")]
    public void Instrument_ids_follow_the_documented_naming_for_spot_perpetual_and_dated_contracts(string raw, BinanceAccountType type, string contractType, string expected)
    {
        InstrumentId id = BinanceVenue.ToInstrumentId(raw, type, contractType);

        Assert.Equal(expected, id.ToString());
        Assert.Equal(new Venue("BINANCE"), id.Venue);
    }

    [Theory]
    [InlineData("BTCUSDT", BinanceAccountType.Spot, "PERPETUAL")]
    [InlineData("BTCUSDT", BinanceAccountType.UsdMFutures, "PERPETUAL")]
    [InlineData("BTCUSDT_250926", BinanceAccountType.UsdMFutures, "CURRENT_QUARTER")]
    [InlineData("BTCUSDT_250926", BinanceAccountType.UsdMFutures, "PERPETUAL")]
    [InlineData("BTCUSD_PERP", BinanceAccountType.CoinMFutures, "PERPETUAL")]
    [InlineData("BTCUSD_261225", BinanceAccountType.CoinMFutures, "CURRENT_QUARTER")]
    public void Raw_symbol_survives_the_round_trip_through_an_instrument_id(string raw, BinanceAccountType type, string contractType)
    {
        InstrumentId id = BinanceVenue.ToInstrumentId(raw, type, contractType);

        Assert.Equal(raw, BinanceVenue.ToRawSymbol(id));
    }

    [Theory]
    [InlineData(1, BarAggregation.Second, "1s")]
    [InlineData(1, BarAggregation.Minute, "1m")]
    [InlineData(3, BarAggregation.Minute, "3m")]
    [InlineData(5, BarAggregation.Minute, "5m")]
    [InlineData(15, BarAggregation.Minute, "15m")]
    [InlineData(30, BarAggregation.Minute, "30m")]
    [InlineData(1, BarAggregation.Hour, "1h")]
    [InlineData(2, BarAggregation.Hour, "2h")]
    [InlineData(4, BarAggregation.Hour, "4h")]
    [InlineData(6, BarAggregation.Hour, "6h")]
    [InlineData(8, BarAggregation.Hour, "8h")]
    [InlineData(12, BarAggregation.Hour, "12h")]
    [InlineData(1, BarAggregation.Day, "1d")]
    [InlineData(3, BarAggregation.Day, "3d")]
    [InlineData(1, BarAggregation.Week, "1w")]
    [InlineData(1, BarAggregation.Month, "1M")]
    public void Every_documented_kline_interval_maps_to_its_binance_code(int step, BarAggregation aggregation, string expected)
    {
        Assert.Equal(expected, BinanceVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Theory]
    [InlineData(2, BarAggregation.Minute)]
    [InlineData(10, BarAggregation.Minute)]
    [InlineData(3, BarAggregation.Hour)]
    [InlineData(2, BarAggregation.Week)]
    [InlineData(100, BarAggregation.Tick)]
    public void Intervals_binance_does_not_offer_are_refused_rather_than_approximated(int step, BarAggregation aggregation)
    {
        Assert.Throws<NotSupportedException>(() => BinanceVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Fact]
    public void Configured_credentials_are_used_as_given()
    {
        BinanceExecutionClientConfig config = new() { ApiKey = "test-key", ApiSecret = "test-secret" };

        Assert.Equal(("test-key", "test-secret"), BinanceVenue.Credentials(config));
    }

    [Fact]
    public void The_documented_binance_signature_example_is_reproduced_by_the_signer_the_adapter_uses()
    {
        // "SIGNED endpoint examples for POST /api/v3/order" in the Binance API documentation.
        string secret = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
        string query = "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559";

        Assert.Equal("c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71", HmacSigner.Sha256Hex(secret, query));
    }

    [Fact]
    public void The_plugin_registers_both_factories_under_the_name_BINANCE()
    {
        Core.Plugins.PluginRegistry registry = new();

        registry.AddPlugin(new BinancePlugin());

        Assert.IsType<BinanceDataClientFactory>(registry.DataClientFactories["BINANCE"]);
        Assert.IsType<BinanceExecutionClientFactory>(registry.ExecutionClientFactories["BINANCE"]);
        Assert.Equal(typeof(BinanceDataClientConfig), registry.DataClientFactories["BINANCE"].ConfigType);
        Assert.Equal(typeof(BinanceExecutionClientConfig), registry.ExecutionClientFactories["BINANCE"].ConfigType);
    }

    [Fact]
    public void The_documented_json_configuration_deserializes_into_the_typed_config()
    {
        // The configuration block from docs/integrations/binance.md with futures selected.
        string json = """
            {
              "accountType": "usdMFutures",
              "apiKey": null, "apiSecret": null,
              "baseUrlHttp": null, "baseUrlWs": null,
              "instrumentProvider": { "loadAll": false, "loadIds": ["BTCUSDT-PERP.BINANCE"], "filters": { "quote": "USDT" } },
              "handleRevisedBars": true
            }
            """;

        BinanceDataClientConfig config = Core.Serialization.BytexJson.Deserialize<BinanceDataClientConfig>(json)!;

        Assert.Equal(BinanceAccountType.UsdMFutures, config.AccountType);
        Assert.True(config.HandleRevisedBars);
        Assert.False(config.InstrumentProvider.LoadAll);
        Assert.Equal([InstrumentId.Parse("BTCUSDT-PERP.BINANCE")], config.InstrumentProvider.LoadIds);
        Assert.Equal("USDT", config.InstrumentProvider.Filters["quote"]);
        Assert.True(config.UseAggTrades);
    }

    [Fact]
    public void The_documented_coin_margined_configuration_deserializes_into_the_typed_config()
    {
        // The spelling a host writes, from docs/integrations/binance.md. A value the enum does not accept would
        // leave the client on the default family - a node that starts, connects and receives the wrong market.
        string json = """
            {
              "accountType": "coinMFutures",
              "instrumentProvider": { "loadAll": false, "loadIds": ["BTCUSD_PERP.BINANCE"] }
            }
            """;

        BinanceDataClientConfig config = Core.Serialization.BytexJson.Deserialize<BinanceDataClientConfig>(json)!;

        Assert.Equal(BinanceAccountType.CoinMFutures, config.AccountType);
        Assert.Equal([InstrumentId.Parse("BTCUSD_PERP.BINANCE")], config.InstrumentProvider.LoadIds);
    }
}
