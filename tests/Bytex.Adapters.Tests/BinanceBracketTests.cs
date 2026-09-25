using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why (R4.12): this venue publishes two different margin figures and only one of them is true.
//
// Without a key it gives `requiredMarginPercent` 5.0 and `maintMarginPercent` 2.5 per symbol. Those cannot be what it
// requires: 5 percent supports at most 20x and the venue grants 125x, which cannot need more than 0.8 percent. They
// are a venue-wide default. With a key, `/fapi/v1/leverageBracket` gives the real per-notional brackets - 0.008 and
// 0.004 at the bracket a position starts in.
//
// The difference is not cosmetic and it is not small. Instrument.InitialMarginRate is Math.Max(1/leverage,
// MarginInit), so the instrument's margin is a FLOOR: against the public default, a strategy written for 50x was
// sized, margined and liquidated exactly as though it were 20x. The run completes, the numbers look like a result,
// and they belong to a strategy nobody wrote.
//
// Measured 2026-09-25: there is no public source for the brackets. The documented endpoint refuses an
// unauthenticated call, the web interface's own bracket feed rejects it, and no futures-data path serves it. So the
// two cases below - with a key and without - are the whole of what this venue can be asked, and each has to say
// something honest.
public sealed class BinanceBracketTests
{
    /// <summary>What this venue answers for BTCUSDT's brackets: the widest tier first, the highest leverage in it.</summary>
    private const string Brackets = """
        [
          {
            "symbol": "BTCUSDT",
            "notionalCoef": 1.0,
            "brackets": [
              { "bracket": 1, "initialLeverage": 125, "notionalCap": 50000,   "notionalFloor": 0,     "maintMarginRatio": 0.004, "cum": 0.0 },
              { "bracket": 2, "initialLeverage": 100, "notionalCap": 600000,  "notionalFloor": 50000, "maintMarginRatio": 0.005, "cum": 50.0 },
              { "bracket": 3, "initialLeverage": 50,  "notionalCap": 3000000, "notionalFloor": 600000,"maintMarginRatio": 0.01,  "cum": 3050.0 }
            ]
          }
        ]
        """;

    private static async Task<Instrument> LoadAsync(LoopbackServer server, bool withKey)
    {
        BinanceDataClientConfig config = new()
        {
            AccountType = BinanceAccountType.UsdMFutures,
            BaseUrlHttp = server.HttpBase,
            ApiKey = withKey ? "test-key" : null,
            ApiSecret = withKey ? "test-secret" : null,
        };

        using BinanceHttp http = new(config, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures, new InstrumentProviderConfig { LoadAll = true }, null);
        await provider.LoadAllAsync(CancellationToken.None);

        return provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BINANCE"))!;
    }

    private static Routes Venue() => new Routes()
        .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo)
        .On("GET", BinanceVenue.LeverageBracketPath, Brackets);

    [Fact]
    public async Task With_a_key_the_margin_is_what_the_venue_really_requires()
    {
        await using LoopbackServer server = new(Venue().Handle);

        Instrument perp = await LoadAsync(server, withKey: true);

        // The bracket a position starts in: the highest leverage the venue grants, and the lowest maintenance it
        // takes. Everything above is the venue charging more as a position grows, which is not a property of the
        // instrument.
        Assert.Equal(1m / 125m, perp.MarginInit);
        Assert.Equal(0.004m, perp.MarginMaint);
        Assert.Equal(125m, perp.MaxLeverage);

        // And the point of all of it: with the real floor, a leverage the venue grants is no longer clamped.
        Assert.Equal(1m / 50m, perp.InitialMarginRate(50m));
    }

    [Fact]
    public async Task Without_a_key_the_venue_wide_default_is_kept_and_the_ceiling_is_unknown()
    {
        await using LoopbackServer server = new(Venue().Handle);

        Instrument perp = await LoadAsync(server, withKey: false);

        Assert.Equal(0.05m, perp.MarginInit);
        Assert.Equal(0.025m, perp.MarginMaint);

        // Null is the honest answer and it is not the same as unlimited: nothing read a ceiling, so nothing may
        // claim one. Anything deciding whether a configured leverage is reachable has to branch on this.
        Assert.Null(perp.MaxLeverage);
    }

    [Fact]
    public async Task The_clamp_this_exists_to_remove_is_demonstrably_there_without_a_key()
    {
        // Stated as a test rather than a comment, because it is the defect: asked for 50x against the public
        // default, the engine charges the margin of 20x. Nothing errors, nothing warns, and the result is a result.
        await using LoopbackServer server = new(Venue().Handle);

        Instrument coarse = await LoadAsync(server, withKey: false);

        Assert.Equal(0.05m, coarse.InitialMarginRate(50m));
        Assert.Equal(coarse.InitialMarginRate(20m), coarse.InitialMarginRate(50m));
    }

    [Fact]
    public async Task A_key_that_cannot_read_the_brackets_keeps_the_default_rather_than_failing_the_catalog()
    {
        // A key without the right permission, or a venue having a moment. An instrument list is still worth having,
        // so the figures fall back and the loss is logged rather than thrown - what is lost is the difference
        // between the real margin and a default six times larger, which is worth a line in a log.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo)
            .On("GET", BinanceVenue.LeverageBracketPath, _ => new StubResponse(401, """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}"""))
            .Handle);

        Instrument perp = await LoadAsync(server, withKey: true);

        Assert.Equal(0.05m, perp.MarginInit);
        Assert.Null(perp.MaxLeverage);
    }

    [Fact]
    public async Task Spot_asks_for_no_brackets_at_all()
    {
        // A cash account borrows nothing, so there is no margin to read and no request to spend on finding out.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo)
            .Handle);

        BinanceDataClientConfig config = new()
        {
            AccountType = BinanceAccountType.Spot,
            BaseUrlHttp = server.HttpBase,
            ApiKey = "test-key",
            ApiSecret = "test-secret",
        };

        using BinanceHttp http = new(config, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot, new InstrumentProviderConfig { LoadAll = true }, null);
        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Empty(server.RequestsTo(BinanceVenue.LeverageBracketPath));

        Instrument spot = provider.Find(InstrumentId.Parse("BTCUSDT.BINANCE"))!;
        Assert.Equal(0m, spot.MarginInit);
        Assert.Null(spot.MaxLeverage);
    }
}
