using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Binance;

// Why: every price and quantity the engine sends to Binance is rounded with the increments parsed here.
// A wrong tick size or a mis-named contract means rejected orders or orders on the wrong market.
// Payloads follow the documented exchangeInfo shape and are served by a stub on 127.0.0.1.
public sealed class BinanceInstrumentProviderTests
{
    private static BinanceHttp Http(LoopbackServer server, BinanceAccountType type) =>
        new(new BinanceDataClientConfig { AccountType = type, BaseUrlHttp = server.HttpBase });

    [Fact]
    public async Task Spot_symbol_filters_become_precisions_increments_and_limits()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.Spot);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot);

        await provider.LoadAllAsync(CancellationToken.None);

        CurrencyPair btc = Assert.IsType<CurrencyPair>(provider.Find(InstrumentId.Parse("BTCUSDT.BINANCE")));
        Assert.Equal("BTCUSDT", btc.RawSymbol.Value);
        Assert.Equal(AssetClass.Crypto, btc.AssetClass);
        Assert.Equal(InstrumentClass.Spot, btc.InstrumentClass);
        Assert.Equal("BTC", btc.BaseCurrency.Code);
        Assert.Equal("USDT", btc.QuoteCurrency.Code);
        Assert.Equal("USDT", btc.SettlementCurrency.Code);
        Assert.Equal(2, btc.PricePrecision);
        Assert.Equal(5, btc.SizePrecision);
        Assert.Equal(new Price(0.01m, 2), btc.PriceIncrement);
        Assert.Equal(new Quantity(0.00001m, 5), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.00001m, 5), btc.MinQuantity);
        Assert.Equal(new Quantity(9000m, 5), btc.MaxQuantity);
        Assert.Equal(new Money(5m, Currencies.USDT), btc.MinNotional);
        Assert.Equal(new Money(9_000_000m, Currencies.USDT), btc.MaxNotional);
        Assert.Equal(new Price(0.01m, 2), btc.MinPrice);
        Assert.Equal(new Price(1_000_000m, 2), btc.MaxPrice);
        Assert.Equal(0.001m, btc.MakerFee);
        Assert.Equal(0.001m, btc.TakerFee);
        Assert.Equal(0m, btc.MarginInit);
    }

    [Fact]
    public async Task The_legacy_MIN_NOTIONAL_filter_and_a_non_usdt_quote_are_understood()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.Spot);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument eth = Assert.IsType<CurrencyPair>(provider.Find(InstrumentId.Parse("ETHBTC.BINANCE")));
        Assert.Equal(5, eth.PricePrecision);
        Assert.Equal(4, eth.SizePrecision);
        Assert.Equal("BTC", eth.QuoteCurrency.Code);
        Assert.Equal(new Money(0.0001m, Currencies.BTC), eth.MinNotional);
        Assert.Null(eth.MaxNotional);
    }

    [Fact]
    public async Task Symbols_that_are_not_trading_are_left_out()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.Spot);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(["BTCUSDT.BINANCE", "ETHBTC.BINANCE"], provider.GetAll().Select(i => i.Id.ToString()).Order());
        Assert.Null(provider.Find(InstrumentId.Parse("LUNAUSDT.BINANCE")));
    }

    [Fact]
    public async Task The_quote_filter_keeps_only_matching_instruments()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.Spot);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot);

        await provider.LoadAllAsync(CancellationToken.None, new Dictionary<string, string> { ["quote"] = "usdt" });

        Assert.Equal("BTCUSDT.BINANCE", Assert.Single(provider.GetAll()).Id.ToString());
    }

    [Fact]
    public async Task Loading_one_instrument_asks_the_venue_for_its_raw_symbol_only()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.UsdMFutures);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures);

        await provider.LoadAsync(InstrumentId.Parse("BTCUSDT-PERP.BINANCE"), CancellationToken.None);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/fapi/v1/exchangeInfo", request.Path);
        Assert.Equal("BTCUSDT", request.Query("symbol"));
        Assert.NotNull(provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BINANCE")));
    }

    [Fact]
    public async Task A_futures_perpetual_gets_the_PERP_suffix_swap_class_margin_asset_and_futures_fees()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.UsdMFutures);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures);

        await provider.LoadAllAsync(CancellationToken.None);

        CryptoPerpetual perp = Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BINANCE")));
        Assert.Equal("BTCUSDT", perp.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Swap, perp.InstrumentClass);
        Assert.Equal("USDT", perp.SettlementCurrency.Code);
        Assert.Equal(1, perp.PricePrecision);
        Assert.Equal(3, perp.SizePrecision);
        Assert.Equal(new Price(0.1m, 1), perp.PriceIncrement);
        Assert.Equal(new Quantity(0.001m, 3), perp.SizeIncrement);
        Assert.Equal(new Quantity(1000m, 3), perp.MaxQuantity);
        Assert.Equal(new Money(100m, Currencies.USDT), perp.MinNotional);
        Assert.Equal(new Price(556.8m, 1), perp.MinPrice);
        Assert.Equal(0.0002m, perp.MakerFee);
        Assert.Equal(0.0005m, perp.TakerFee);
    }

    [Fact]
    public async Task A_dated_futures_contract_keeps_its_venue_symbol_and_carries_activation_and_expiry()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.UsdMFutures);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures);

        await provider.LoadAllAsync(CancellationToken.None);

        CryptoFuture future = Assert.IsType<CryptoFuture>(provider.Find(InstrumentId.Parse("BTCUSDT_250926.BINANCE")));
        Assert.Equal("BTCUSDT_250926", future.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Future, future.InstrumentClass);
        Assert.Equal("BTC", future.Underlying.Code);
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 8, 0, 0, TimeSpan.Zero), future.Activation.ToDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2025, 9, 26, 8, 0, 0, TimeSpan.Zero), future.Expiration.ToDateTimeOffset());
        Assert.Equal(1_758_873_600_000_000_000L, future.Expiration.Value);
        Assert.Equal(new Money(5m, Currencies.USDT), future.MinNotional);
        Assert.Null(provider.Find(InstrumentId.Parse("BTCUSDT_250926-PERP.BINANCE")));
        Assert.Null(provider.Find(InstrumentId.Parse("ETHUSDT_230630.BINANCE"))); // status SETTLING
    }

    [Fact]
    public async Task Prices_and_quantities_made_by_a_parsed_instrument_land_on_the_venue_grid()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo).Handle);
        using BinanceHttp http = Http(server, BinanceAccountType.Spot);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot);
        await provider.LoadAllAsync(CancellationToken.None);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT.BINANCE"))!;

        Assert.Equal("25000.13", btc.MakePrice(25_000.126m).ToString());
        Assert.Equal("0.12345", btc.MakeQuantity(0.123459m).ToString()); // quantities round down, never up
    }
}
