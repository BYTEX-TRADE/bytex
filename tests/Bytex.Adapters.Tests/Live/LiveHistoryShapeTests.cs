using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests.Live;

// Why (R12.10), the second half: a candle recording is a photograph too, and the fields a bar is built from are the
// ones a strategy is fed. A venue changing which array position the close sits in, or sending a string where it sent a
// number, leaves every offline test passing and every backtest wrong - and wrong in the worst way, because the numbers
// look like numbers.
//
// So each venue's own history helper is asked for a handful of recent bars and the result is held to what a bar has to
// be. These are the invariants a parse cannot fake: nothing is zero, the extremes contain the open and the close, and
// the stamps march forward one interval at a time.
[Collection(LiveVenueCollection.Name)]
public sealed class LiveHistoryShapeTests
{

    private static Task<(Instrument Instrument, IReadOnlyList<Bar> Bars)> FetchAsync(string family) =>
        FetchAsync(family, DateTimeOffset.UtcNow.AddHours(-12), DateTimeOffset.UtcNow);

    private static async Task<(Instrument Instrument, IReadOnlyList<Bar> Bars)> FetchAsync(string family, DateTimeOffset start, DateTimeOffset end)
    {
        InstrumentProviderConfig all = new() { LoadAll = true, LogWarnings = false };
        using CancellationTokenSource cts = new(LiveVenue.Timeout);

        switch (family)
        {
            case "binance-spot":
            {
                BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot }, null);
                BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await BinanceHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "bybit-linear":
            {
                BybitHttp http = new(new BybitDataClientConfig { ProductType = BybitProductType.Linear }, null);
                BybitInstrumentProvider provider = new(http, BybitProductType.Linear, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await BybitHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "okx-swap":
            {
                OkxHttp http = new(new OkxDataClientConfig(), null);
                OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await OkxHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "kucoin-spot":
            {
                KucoinHttp http = new(new KucoinDataClientConfig { ProductType = KucoinProductType.Spot });
                KucoinInstrumentProvider provider = new(http, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await KucoinHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "kraken-spot":
            {
                KrakenHttp http = new(new KrakenDataClientConfig());
                KrakenInstrumentProvider provider = new(http, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await KrakenHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "bitget-usdt":
            {
                BitgetHttp http = new(new BitgetDataClientConfig { ProductType = BitgetProductType.UsdtFutures });
                BitgetInstrumentProvider provider = new(http, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await BitgetHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "gate-futures":
            {
                GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures });
                GateFuturesInstrumentProvider provider = new(http, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await GateHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            case "hyperliquid":
            {
                HyperliquidHttp http = new(new HyperliquidDataClientConfig());
                HyperliquidInstrumentProvider provider = new(http, all, null);
                await provider.LoadAllAsync(cts.Token);
                Instrument instrument = Pick(provider.GetAll());
                return (instrument, await HyperliquidHistory.FetchBarsAsync(http, instrument, Hourly(instrument), start, end, cts.Token));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(family), family, "no such family in this test");
        }
    }

    /// <summary>A bitcoin market, by what it is rather than by a spelling, because the spellings are the venues' own.</summary>
    private static Instrument Pick(IReadOnlyList<Instrument> instruments) =>
        instruments.FirstOrDefault(i =>
            (i.BaseCurrency?.Code.Equals("BTC", StringComparison.OrdinalIgnoreCase) == true
                || i.BaseCurrency?.Code.Equals("XBT", StringComparison.OrdinalIgnoreCase) == true)
            && i.QuoteCurrency.Code.StartsWith("USD", StringComparison.OrdinalIgnoreCase))
        ?? instruments.First(i => i.Id.Symbol.Value.Contains("BTC", StringComparison.OrdinalIgnoreCase)
            || i.Id.Symbol.Value.Contains("XBT", StringComparison.OrdinalIgnoreCase));

    private static CandleSeries Hourly(Instrument instrument) =>
        new(instrument.Id, new SamplingRule(1, SamplingMethod.Hour, PriceType.Last), CandleOrigin.Provider);

    /// <summary>
    /// <b>The last bar of a window is the one that opens at the end of it.</b>
    ///
    /// <para>
    /// <see cref="Bytex.Core.Model.Data.BarWindow.Closed"/> states the rule for every venue: a bar belongs to a
    /// window when it opens at or before the end and closes at or after the start. Each helper computes the bound
    /// it sends from that, and a venue whose endTime means something slightly different - excluding the candle that
    /// opens on it, say, where the recording says it includes it - answers one bar short of the window asked for,
    /// successfully, with no error to notice.
    /// </para>
    ///
    /// <para>
    /// That is what this is for. A live comparison had two venues answering different counts over the same windows,
    /// 49 against 50, and every offline test passed on both because both recordings encode what was measured when
    /// they were taken. Only the venue can say which recording has since drifted.
    /// </para>
    ///
    /// <para>
    /// The assertion is the LAST bar rather than the count, on purpose: a venue with no trades in some interior
    /// minute may genuinely publish no candle for it, and a count would fail for a market being quiet rather than
    /// for a bound being wrong. The window is anchored on whole hours well inside the day, where a bitcoin market
    /// has data on every venue here.
    /// </para>
    /// </summary>
    [LiveTheory]
    [InlineData("binance-spot")]
    [InlineData("bybit-linear")]
    [InlineData("okx-swap")]
    [InlineData("kucoin-spot")]
    [InlineData("kraken-spot")]
    [InlineData("bitget-usdt")]
    [InlineData("gate-futures")]
    [InlineData("hyperliquid")]
    public async Task The_last_bar_of_a_window_is_the_one_that_opens_at_its_end(string family)
    {
        (Instrument instrument, IReadOnlyList<Bar> bars) = await WindowAsync(family);

        Assert.NotEmpty(bars);

        DateTimeOffset end = WindowEnd();
        long interval = bars[0].CandleSeries.Spec.IntervalNanos;
        Bytex.Core.Model.Primitives.UnixNanos wanted = Bytex.Core.Model.Primitives.UnixNanos.FromDateTimeOffset(end).AddNanos(interval);

        Assert.Equal(wanted, bars[^1].EventTime);
        Assert.True(
            bars[^1].EventTime.Value - interval <= Bytex.Core.Model.Primitives.UnixNanos.FromDateTimeOffset(end).Value,
            $"{family} {instrument.Id}: the newest bar opens after the end of the window");
    }

    /// <summary>
    /// Whole hours, well inside the previous day: both ends land on a bar boundary, so the window's last open is
    /// exactly the end and there is no partly formed candle anywhere near it.
    /// </summary>
    private static DateTimeOffset WindowEnd()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddHours(-4);
    }

    private static async Task<(Instrument Instrument, IReadOnlyList<Bar> Bars)> WindowAsync(string family) =>
        await FetchAsync(family, WindowEnd().AddHours(-12), WindowEnd());

    [LiveTheory]
    [InlineData("binance-spot")]
    [InlineData("bybit-linear")]
    [InlineData("okx-swap")]
    [InlineData("kucoin-spot")]
    [InlineData("kraken-spot")]
    [InlineData("bitget-usdt")]
    [InlineData("gate-futures")]
    [InlineData("hyperliquid")]
    public async Task A_candle_still_has_the_shape_the_recordings_assert(string family)
    {
        (Instrument instrument, IReadOnlyList<Bar> bars) = await FetchAsync(family);

        Assert.NotEmpty(bars);

        foreach (Bar bar in bars)
        {
            // A price that parsed as zero is what a moved array position or a renamed field looks like.
            Assert.True(bar.Open.Value > 0m, $"{family} {instrument.Id}: open is zero");
            Assert.True(bar.High.Value > 0m, $"{family} {instrument.Id}: high is zero");
            Assert.True(bar.Low.Value > 0m, $"{family} {instrument.Id}: low is zero");
            Assert.True(bar.Close.Value > 0m, $"{family} {instrument.Id}: close is zero");

            // And the one relation a candle always has, which catches two prices swapped as well as one missing.
            Assert.True(bar.High.Value >= bar.Low.Value, $"{family} {instrument.Id}: high below low");
            Assert.True(bar.High.Value >= bar.Open.Value && bar.High.Value >= bar.Close.Value, $"{family} {instrument.Id}: high is not the highest");
            Assert.True(bar.Low.Value <= bar.Open.Value && bar.Low.Value <= bar.Close.Value, $"{family} {instrument.Id}: low is not the lowest");
        }

        // Oldest first, and one interval apart: the order is what a run depends on, and a venue that starts answering
        // newest-first would otherwise be noticed by a strategy rather than by a test.
        long[] stamps = [.. bars.Select(b => b.EventTime.Value)];
        Assert.Equal(stamps.OrderBy(s => s).ToArray(), stamps);
        Assert.Equal(stamps.Distinct().Count(), stamps.Length);

        if (bars.Count > 1)
        {
            long interval = bars[0].CandleSeries.Spec.IntervalNanos;
            Assert.All(bars.Zip(bars.Skip(1)), pair => Assert.Equal(interval, pair.Second.EventTime.Value - pair.First.EventTime.Value));
        }

        // Stamped at the close, never in the future: the engine's own rule about when a bar exists.
        Assert.All(bars, b => Assert.True(
            b.EventTime.Value <= Bytex.Core.Model.Primitives.UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow).Value,
            $"{family}: a bar is stamped in the future, so an unclosed candle was returned as closed"));
    }
}
