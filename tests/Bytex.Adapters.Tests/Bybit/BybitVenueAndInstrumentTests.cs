using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bybit;

// Why: hosts, stream routes, symbol naming and instrument increments decide where Bybit orders go and how they
// are rounded. Expected values come from the Bybit V5 documentation and docs/integrations/bybit.md.
public sealed class BybitVenueAndInstrumentTests
{
    [Theory]
    [InlineData(false, "https://api.bybit.com")]
    [InlineData(true, "https://api-testnet.bybit.com")]
    public void Rest_base_url_depends_on_testnet_only(bool testnet, string expected)
    {
        Assert.Equal(expected, BybitVenue.HttpBase(new BybitDataClientConfig { Testnet = testnet, ProductType = BybitProductType.Spot }));
        Assert.Equal(expected, BybitVenue.HttpBase(new BybitDataClientConfig { Testnet = testnet, ProductType = BybitProductType.Linear }));
    }

    [Theory]
    [InlineData(BybitProductType.Spot, false, "wss://stream.bybit.com/v5/public/spot")]
    [InlineData(BybitProductType.Linear, false, "wss://stream.bybit.com/v5/public/linear")]
    [InlineData(BybitProductType.Spot, true, "wss://stream-testnet.bybit.com/v5/public/spot")]
    [InlineData(BybitProductType.Linear, true, "wss://stream-testnet.bybit.com/v5/public/linear")]
    public void Public_stream_route_carries_the_product_category(BybitProductType type, bool testnet, string expected)
    {
        Assert.Equal(expected, BybitVenue.WsPublic(new BybitDataClientConfig { ProductType = type, Testnet = testnet }));
    }

    [Theory]
    [InlineData(false, "wss://stream.bybit.com/v5/private")]
    [InlineData(true, "wss://stream-testnet.bybit.com/v5/private")]
    public void Private_stream_route_is_shared_by_all_products(bool testnet, string expected)
    {
        Assert.Equal(expected, BybitVenue.WsPrivate(new BybitExecutionClientConfig { ProductType = BybitProductType.Linear, Testnet = testnet }));
    }

    [Fact]
    public void Explicit_base_urls_keep_the_v5_routes()
    {
        BybitDataClientConfig config = new() { ProductType = BybitProductType.Linear, BaseUrlHttp = "http://127.0.0.1:9", BaseUrlWs = "ws://127.0.0.1:9" };

        Assert.Equal("http://127.0.0.1:9", BybitVenue.HttpBase(config));
        Assert.Equal("ws://127.0.0.1:9/v5/public/linear", BybitVenue.WsPublic(config));
        Assert.Equal("ws://127.0.0.1:9/v5/private", BybitVenue.WsPrivate(config));
    }

    [Theory]
    [InlineData("BTCUSDT", BybitProductType.Spot, "BTCUSDT.BYBIT")]
    [InlineData("BTCUSDT", BybitProductType.Linear, "BTCUSDT-PERP.BYBIT")]
    [InlineData("1000PEPEUSDT", BybitProductType.Linear, "1000PEPEUSDT-PERP.BYBIT")]
    [InlineData("ETHPERP", BybitProductType.Linear, "ETHPERP-PERP.BYBIT")] // USDC perpetuals are literally named "...PERP" at the venue
    public void Instrument_ids_distinguish_spot_from_linear_and_round_trip_to_the_raw_symbol(string raw, BybitProductType type, string expected)
    {
        InstrumentId id = BybitVenue.ToInstrumentId(raw, type);

        Assert.Equal(expected, id.ToString());
        Assert.Equal(raw, BybitVenue.ToRawSymbol(id));
    }

    [Theory]
    [InlineData(1, BarAggregation.Minute, "1")]
    [InlineData(3, BarAggregation.Minute, "3")]
    [InlineData(5, BarAggregation.Minute, "5")]
    [InlineData(15, BarAggregation.Minute, "15")]
    [InlineData(30, BarAggregation.Minute, "30")]
    [InlineData(1, BarAggregation.Hour, "60")]
    [InlineData(2, BarAggregation.Hour, "120")]
    [InlineData(4, BarAggregation.Hour, "240")]
    [InlineData(6, BarAggregation.Hour, "360")]
    [InlineData(12, BarAggregation.Hour, "720")]
    [InlineData(1, BarAggregation.Day, "D")]
    [InlineData(1, BarAggregation.Week, "W")]
    [InlineData(1, BarAggregation.Month, "M")]
    public void Every_documented_kline_interval_maps_to_its_bybit_code(int step, BarAggregation aggregation, string expected)
    {
        Assert.Equal(expected, BybitVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Theory]
    [InlineData(1, BarAggregation.Second)]
    [InlineData(8, BarAggregation.Hour)]
    [InlineData(3, BarAggregation.Day)]
    public void Intervals_bybit_does_not_offer_are_refused(int step, BarAggregation aggregation)
    {
        Assert.Throws<NotSupportedException>(() => BybitVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Fact]
    public void The_plugin_registers_both_factories_under_the_name_BYBIT_and_the_documented_json_config_binds()
    {
        Core.Plugins.PluginRegistry registry = new();
        registry.AddPlugin(new BybitPlugin());
        string json = """
            { "productType": "linear", "testnet": true, "apiKey": null, "apiSecret": null,
              "instrumentProvider": { "loadIds": ["BTCUSDT-PERP.BYBIT"] }, "defaultTriggerType": "markPrice", "recvWindowMs": 8000 }
            """;

        BybitExecutionClientConfig config = (BybitExecutionClientConfig)System.Text.Json.JsonSerializer.Deserialize(json, registry.ExecutionClientFactories["BYBIT"].ConfigType, Core.Serialization.BytexJson.Options)!;

        Assert.IsType<BybitDataClientFactory>(registry.DataClientFactories["BYBIT"]);
        Assert.Equal(BybitProductType.Linear, config.ProductType);
        Assert.True(config.Testnet);
        Assert.Equal(TriggerType.MarkPrice, config.DefaultTriggerType);
        Assert.Equal(8000, config.RecvWindowMs);
        Assert.Equal([InstrumentId.Parse("BTCUSDT-PERP.BYBIT")], config.InstrumentProvider.LoadIds);
    }

    // ----- instruments-info -----

    private static BybitHttp Http(LoopbackServer server, BybitProductType type) => new(new BybitDataClientConfig { ProductType = type, BaseUrlHttp = server.HttpBase });

    [Fact]
    public async Task A_spot_instrument_takes_its_size_step_from_basePrecision_and_skips_closed_symbols()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.SpotInstruments).Handle);
        using BybitHttp http = Http(server, BybitProductType.Spot);
        BybitInstrumentProvider provider = new(http, BybitProductType.Spot);

        await provider.LoadAllAsync(CancellationToken.None);

        CurrencyPair btc = Assert.IsType<CurrencyPair>(Assert.Single(provider.GetAll()));
        Assert.Equal("BTCUSDT.BYBIT", btc.Id.ToString());
        Assert.Equal("BTCUSDT", btc.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Spot, btc.InstrumentClass);
        Assert.Equal("BTC", btc.BaseCurrency.Code);
        Assert.Equal("USDT", btc.QuoteCurrency.Code);
        Assert.Equal(new Price(0.01m, 2), btc.PriceIncrement);
        Assert.Equal(new Quantity(0.000001m, 6), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.000048m, 6), btc.MinQuantity);
        Assert.Equal(0.001m, btc.MakerFee);
        Assert.Equal(0.001m, btc.TakerFee);
        Assert.Equal("spot", Assert.Single(server.Requests).Query("category"));
    }

    [Fact(Skip = "BUG: spot lotSizeFilter has no minNotionalValue; the minimum order value is minOrderAmt, which BybitInstrumentProvider never reads, so spot instruments carry no MinNotional")]
    public async Task A_spot_instrument_takes_its_minimum_notional_from_minOrderAmt()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.SpotInstruments).Handle);
        using BybitHttp http = Http(server, BybitProductType.Spot);
        BybitInstrumentProvider provider = new(http, BybitProductType.Spot);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(new Money(1m, Currencies.USDT), provider.Find(InstrumentId.Parse("BTCUSDT.BYBIT"))!.MinNotional);
    }

    [Fact]
    public async Task Linear_instruments_are_read_across_cursor_pages_until_the_cursor_is_empty()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", r => StubResponse.Json(r.Query("cursor") is null ? BybitPayloads.LinearInstrumentsPage1 : BybitPayloads.LinearInstrumentsPage2)).Handle);
        using BybitHttp http = Http(server, BybitProductType.Linear);
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(2, provider.Count);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("linear", server.Requests[0].Query("category"));
        Assert.Equal("cursor-page-2", server.Requests[1].Query("cursor"));
    }

    [Fact]
    public async Task A_linear_perpetual_gets_the_PERP_suffix_qtyStep_settle_coin_and_derivative_fees()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.LinearInstrumentsPage1.Replace("cursor-page-2", string.Empty, StringComparison.Ordinal)).Handle);
        using BybitHttp http = Http(server, BybitProductType.Linear);
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);

        await provider.LoadAsync(InstrumentId.Parse("BTCUSDT-PERP.BYBIT"), CancellationToken.None);

        CryptoPerpetual perp = Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BYBIT")));
        Assert.Equal("BTCUSDT", Assert.Single(server.Requests).Query("symbol"));
        Assert.Equal(InstrumentClass.Swap, perp.InstrumentClass);
        Assert.Equal("USDT", perp.SettlementCurrency.Code);
        Assert.Equal(new Price(0.1m, 1), perp.PriceIncrement);
        Assert.Equal(new Quantity(0.001m, 3), perp.SizeIncrement);
        Assert.Equal(new Quantity(0.001m, 3), perp.MinQuantity);
        Assert.Equal(new Quantity(100m, 3), perp.MaxQuantity);
        Assert.Equal(new Money(5m, Currencies.USDT), perp.MinNotional);
        Assert.Equal(new Price(0.1m, 1), perp.MinPrice);
        Assert.Equal(new Price(199_999.8m, 1), perp.MaxPrice);
        Assert.Equal(0.0002m, perp.MakerFee);
        Assert.Equal(0.00055m, perp.TakerFee);
    }

    [Fact]
    public async Task A_dated_linear_contract_is_a_future_with_launch_and_delivery_times()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.LinearInstrumentsPage2).Handle);
        using BybitHttp http = Http(server, BybitProductType.Linear);
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);

        await provider.LoadAllAsync(CancellationToken.None);

        CryptoFuture future = Assert.IsType<CryptoFuture>(Assert.Single(provider.GetAll()));
        Assert.Equal("BTCUSDT-26SEP25", future.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Future, future.InstrumentClass);
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 8, 0, 0, TimeSpan.Zero), future.Activation.ToDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2025, 9, 26, 8, 0, 0, TimeSpan.Zero), future.Expiration.ToDateTimeOffset());
        Assert.Equal(new Price(0.5m, 1), future.PriceIncrement);
    }

    [Fact(Skip = "BUG: BybitVenue.ToInstrumentId appends -PERP to every linear symbol, so the dated contract BTCUSDT-26SEP25 becomes 'BTCUSDT-26SEP25-PERP.BYBIT', a perpetual name on a CryptoFuture")]
    public async Task A_dated_linear_contract_is_not_named_as_a_perpetual()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/v5/market/instruments-info", BybitPayloads.LinearInstrumentsPage2).Handle);
        using BybitHttp http = Http(server, BybitProductType.Linear);
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal("BTCUSDT-26SEP25.BYBIT", Assert.Single(provider.GetAll()).Id.ToString());
    }

    [Fact]
    public async Task A_venue_error_envelope_surfaces_as_a_BybitApiException_with_code_and_message()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json(BybitPayloads.Error(10001, "params error: category invalid")));
        using BybitHttp http = Http(server, BybitProductType.Spot);
        BybitInstrumentProvider provider = new(http, BybitProductType.Spot);

        BybitApiException error = await Assert.ThrowsAsync<BybitApiException>(() => provider.LoadAllAsync(CancellationToken.None));

        Assert.Equal(10001, error.Code);
        Assert.Equal("params error: category invalid", error.RetMsg);
    }
}
