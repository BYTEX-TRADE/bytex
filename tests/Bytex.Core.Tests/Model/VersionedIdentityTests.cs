using Bytex.Core.Migration;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Core.Tests.Model;

public sealed class VersionedIdentityTests
{
    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData("ETH-USDT-SWAP")]
    [InlineData("BRK.B")]
    [InlineData("EUR/USD")]
    [InlineData("EUR%2FUSD")]
    [InlineData("asset:contract")]
    public void Escaped_market_and_candle_identities_preserve_symbols(string symbol)
    {
        MarketKey market = new(new Symbol(symbol), new Venue("SIM"));
        CandleSeries series = new(market, new SamplingRule(5, SamplingMethod.Minute, PriceType.Last));

        Assert.StartsWith("bx-market:v2/", market.Value);
        Assert.Equal(market, MarketKey.Parse(market.Value));
        Assert.Equal(symbol, MarketKey.Parse(market.Value).Symbol.Value);
        Assert.Equal(series, CandleSeries.Parse(series.ToString()));
    }

    [Fact]
    public void Legacy_readers_are_explicit_and_new_parsers_reject_old_identities()
    {
        const string oldMarket = "BTCUSDT.BINANCE";
        const string oldCandle = "BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL";
        Assert.False(MarketKey.TryParse(oldMarket, out _));
        Assert.False(CandleSeries.TryParse(oldCandle, out _));
        Assert.Equal("bx-market:v2/BINANCE/BTCUSDT", LegacyIdentityReader.ReadMarket(oldMarket).Value);
        Assert.Equal("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider", LegacyIdentityReader.ReadCandle(oldCandle).ToString());
    }

    [Theory]
    [InlineData("bx-candle:v2/SIM/BTCUSDT/minute/0/last/provider")]
    [InlineData("bx-candle:v2/SIM/BTCUSDT/minute/-1/last/provider")]
    [InlineData("bx-candle:v2/SIM/BTCUSDT/999/1/last/provider")]
    [InlineData("bx-candle:v2/SIM/BTCUSDT/minute/1/999/provider")]
    [InlineData("bx-candle:v2/SIM/BTCUSDT/minute/1/last/999")]
    [InlineData("bx-candle:v3/SIM/BTCUSDT/minute/1/last/provider")]
    public void Invalid_or_unknown_series_are_rejected(string text) => Assert.False(CandleSeries.TryParse(text, out _));

    [Fact]
    public void Percent_literal_and_separator_have_different_market_identities()
    {
        Assert.NotEqual(new MarketKey(new Symbol("EUR/USD"), new Venue("SIM")),
            new MarketKey(new Symbol("EUR%2FUSD"), new Venue("SIM")));
    }
}
