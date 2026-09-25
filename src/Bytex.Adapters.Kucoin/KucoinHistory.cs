using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Kucoin;

/// <summary>
/// KuCoin's candle history read the way a running node sees it. The data client answers <c>RequestBars</c> with this, and
/// anything that stores history (a catalog download) should call it too, so stored bars and live bars never disagree
/// about what an interval looked like.
/// </summary>
public static class KucoinHistory
{
    private const int Page = KucoinVenue.CandlePage;
    private const int SeedPages = 5;

    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: every bar that closes at or
    /// after <paramref name="start"/> and opens at or before <paramref name="end"/>, never the candle that is still forming.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(KucoinHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(http, instrument, barType, UnixNanos.FromDateTimeOffset(start), UnixNanos.FromDateTimeOffset(end), null, UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given. As in the other
    /// adapters, <paramref name="end"/> bounds a candle's open time and <paramref name="start"/> its close time. The venue
    /// filters by a candle's start in seconds, returns at most 1500 rows newest first with close before high and low, and
    /// includes the candle that is still forming at <paramref name="now"/>, which is left out.
    /// <para>
    /// The venue shows an interval without a trade as a flat candle at the previous close with no volume, but it writes
    /// those candles only when the next trade comes, so quiet stretches are missing from its answer: the one since the
    /// last trade always, older ones on some pairs. The same flat bars are put in here, anywhere in the window, up to the
    /// last interval that has closed by <paramref name="now"/> and <paramref name="end"/>. When the window itself starts in
    /// a quiet stretch, the close before it is looked up so that the stretch is filled as well. The result is continuous
    /// from its first bar to its last.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(KucoinHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(instrument);

        // One entry point for both of the venue's markets, because a caller storing history has an instrument and a
        // window and no business knowing that this venue answers for them out of two different APIs on two hosts.
        if (http.ProductType == KucoinProductType.Futures)
        {
            return await FetchFuturesBarsAsync(http, instrument, barType, start, end, limit, now, ct).ConfigureAwait(false);
        }

        long interval = barType.Spec.IntervalNanos;
        if (interval <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; KuCoin history has time bars only.", nameof(barType));
        }

        long intervalSeconds = interval / UnixNanos.NanosPerSecond;
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        long lastClose = Math.Min(lastOpen / interval * interval + interval, now.Value / interval * interval);
        long endAt = lastOpen / UnixNanos.NanosPerSecond + 1;
        long? startAt = start is { } s ? Math.Max(0, s.Value / UnixNanos.NanosPerSecond - intervalSeconds) : null;
        int take = BarWindow.Wanted(limit, start, Page);

        List<Bar> bars = new();
        while (bars.Count < take)
        {
            long pageStart = Math.Max(startAt ?? 0, endAt - Page * intervalSeconds);
            JsonElement data = await CandlesAsync(http, barType, pageStart, endAt, ct).ConfigureAwait(false);
            int count = 0;
            long oldest = long.MaxValue;
            foreach (JsonElement k in data.EnumerateArray())
            {
                count++;
                long open = k[0].LongValue();
                oldest = Math.Min(oldest, open);
                UnixNanos close = new(open * UnixNanos.NanosPerSecond + interval);
                if (open * UnixNanos.NanosPerSecond > lastOpen || close > now || (start is { } from && close < from))
                {
                    continue;
                }

                bars.Add(ToBar(k, barType, instrument, close));
            }

            // No rows, the start of the range reached, or an answer that goes no further back than the last one.
            if (count == 0 || pageStart <= (startAt ?? 0) || oldest >= endAt)
            {
                break;
            }

            endAt = oldest;
        }

        List<Bar> ordered = bars.DistinctBy(b => b.TsEvent).OrderBy(b => b.TsEvent).ToList();

        if (start is { } windowStart)
        {
            long firstClose = (windowStart.Value + interval - 1) / interval * interval;
            if (firstClose <= lastClose && (ordered.Count == 0 || ordered[0].TsEvent.Value > firstClose)
                && await CloseBeforeAsync(http, barType, instrument, firstClose - interval, ct).ConfigureAwait(false) is { } before)
            {
                // Stands where the bar before the window would be, carries the last close known there, and is dropped again.
                UnixNanos at = new(firstClose - interval);
                ordered.Insert(0, new Bar(barType, before, before, before, before, instrument.MakeQuantity(0m), at, at));
                List<Bar> seeded = FillQuietIntervals(ordered, barType, instrument, interval, lastClose);
                seeded.RemoveAt(0);
                IReadOnlyList<Bar> window = BarWindow.Closed(seeded, barType, start, end, now);
                return BarWindow.Capped(window, start, take);
            }
        }

        List<Bar> filled = FillQuietIntervals(ordered, barType, instrument, interval, lastClose);
        IReadOnlyList<Bar> bounded = BarWindow.Closed(filled, barType, start, end, now);
        return BarWindow.Capped(bounded, start, take);
    }

    private static Task<JsonElement> CandlesAsync(KucoinHttp http, BarType barType, long startAt, long endAt, CancellationToken ct) =>
        http.GetPublicAsync("/api/v1/market/candles", new Dictionary<string, string>
        {
            ["symbol"] = KucoinVenue.ToRawSymbol(barType.InstrumentId),
            ["type"] = KucoinVenue.Interval(barType.Spec),
            ["startAt"] = startAt.ToString(CultureInfo.InvariantCulture),
            ["endAt"] = endAt.ToString(CultureInfo.InvariantCulture),
        }, ct);

    private static Bar ToBar(JsonElement k, BarType barType, Instrument instrument, UnixNanos close) =>
        new(barType, instrument.MakePrice(k[1].DecValue()), instrument.MakePrice(k[3].DecValue()), instrument.MakePrice(k[4].DecValue()), instrument.MakePrice(k[2].DecValue()),
            instrument.MakeQuantity(k[5].DecValue()), close, close);

    /// <summary>The close of the newest candle that opened before <paramref name="beforeOpen"/>, looked for a few pages back.</summary>
    private static async Task<Price?> CloseBeforeAsync(KucoinHttp http, BarType barType, Instrument instrument, long beforeOpen, CancellationToken ct)
    {
        long intervalSeconds = barType.Spec.IntervalNanos / UnixNanos.NanosPerSecond;
        long beforeOpenSeconds = beforeOpen / UnixNanos.NanosPerSecond;
        long endAt = beforeOpenSeconds;
        for (int page = 0; page < SeedPages && endAt > 0; page++)
        {
            long pageStart = Math.Max(0, endAt - Page * intervalSeconds);
            JsonElement data = await CandlesAsync(http, barType, pageStart, endAt, ct).ConfigureAwait(false);
            long newest = long.MinValue;
            Price? close = null;
            foreach (JsonElement k in data.EnumerateArray())
            {
                long open = k[0].LongValue();
                if (open < beforeOpenSeconds && open > newest)
                {
                    newest = open;
                    close = instrument.MakePrice(k[2].DecValue());
                }
            }

            if (close is not null || pageStart == 0)
            {
                return close;
            }

            endAt = pageStart;
        }

        return null;
    }

    internal static List<Bar> FillQuietIntervals(List<Bar> bars, BarType barType, Instrument instrument, long interval, long lastClose)
    {
        if (bars.Count == 0 || interval <= 0)
        {
            return bars;
        }

        List<Bar> result = new(bars.Count);
        for (int i = 0; i < bars.Count; i++)
        {
            result.Add(bars[i]);
            long until = i + 1 < bars.Count ? bars[i + 1].TsEvent.Value : lastClose + 1;
            Price close = bars[i].Close;
            for (long ts = bars[i].TsEvent.Value + interval; ts < until && ts <= lastClose; ts += interval)
            {
                UnixNanos at = new(ts);
                result.Add(new Bar(barType, close, close, close, close, instrument.MakeQuantity(0m), at, at));
            }
        }

        return result;
    }

    // ----- futures -----
    //
    // Everything about a futures candle differs from a spot one, and every difference is silent rather than loud. The
    // row is [start, open, HIGH, LOW, close, volume, turnover] where spot is [start, open, CLOSE, HIGH, LOW, volume];
    // the answer is oldest first where spot is newest first; the start is in milliseconds where spot is in seconds;
    // the length is a number of minutes where spot is a word; and one request answers with 200 rows where spot gives
    // 1500 and this market's own documentation says 500. A fetch copied across from spot would return plausible bars
    // with open, high, low and close shuffled, in the wrong order, covering a fraction of the window asked for.
    //
    // The two agree on one thing, measured rather than assumed: an interval with no trade in it is simply absent from
    // the answer, and the venue never writes a zero-volume candle for it. ETHUSDCM returned 198 rows across 201
    // minutes with no zero-volume row among them. So the flat bars are put in here exactly as they are for spot, and
    // a caller cannot tell the two markets apart by the continuity of what comes back.

    private static async Task<IReadOnlyList<Bar>> FetchFuturesBarsAsync(KucoinHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct)
    {
        long interval = barType.Spec.IntervalNanos;
        if (interval <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; KuCoin history has time bars only.", nameof(barType));
        }

        long intervalMs = interval / UnixNanos.NanosPerMillisecond;
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        long lastClose = Math.Min(lastOpen / interval * interval + interval, now.Value / interval * interval);
        long toMs = lastOpen / UnixNanos.NanosPerMillisecond;
        int take = BarWindow.Wanted(limit, start, KucoinFuturesVenue.CandlePage);

        // With a start the window is given. Without one the caller wants the newest `take`, so the window is walked
        // back far enough to hold them - and one interval further, because a candle is filtered by its start and the
        // one covering the boundary starts before it.
        long fromMs = start is { } s
            ? (s.Value / UnixNanos.NanosPerMillisecond) - intervalMs
            : toMs - ((take + 1L) * intervalMs);

        List<Bar> bars = new();
        long cursor = Math.Max(0, fromMs);
        while (cursor <= toMs)
        {
            JsonElement data = await FuturesCandlesAsync(http, barType, cursor, toMs, ct).ConfigureAwait(false);
            int count = 0;
            long newest = long.MinValue;
            foreach (JsonElement k in data.EnumerateArray())
            {
                count++;
                long openMs = k[0].LongValue();
                newest = Math.Max(newest, openMs);
                long open = openMs * UnixNanos.NanosPerMillisecond;
                UnixNanos close = new(open + interval);
                if (open > lastOpen || close > now || (start is { } from && close < from))
                {
                    continue;
                }

                bars.Add(ToFuturesBar(k, barType, instrument, close));
            }

            // Nothing there, or a page that did not fill and so reached the end of what the venue holds.
            if (count < KucoinFuturesVenue.CandlePage)
            {
                break;
            }

            cursor = newest + intervalMs;
        }

        List<Bar> ordered = bars.DistinctBy(b => b.TsEvent).OrderBy(b => b.TsEvent).ToList();

        if (start is { } windowStart)
        {
            long firstClose = (windowStart.Value + interval - 1) / interval * interval;
            if (firstClose <= lastClose && (ordered.Count == 0 || ordered[0].TsEvent.Value > firstClose)
                && await FuturesCloseBeforeAsync(http, barType, instrument, firstClose - interval, ct).ConfigureAwait(false) is { } before)
            {
                UnixNanos at = new(firstClose - interval);
                ordered.Insert(0, new Bar(barType, before, before, before, before, instrument.MakeQuantity(0m), at, at));
                List<Bar> seeded = FillQuietIntervals(ordered, barType, instrument, interval, lastClose);
                seeded.RemoveAt(0);
                return BarWindow.Capped(BarWindow.Closed(seeded, barType, start, end, now), start, take);
            }
        }

        List<Bar> filled = FillQuietIntervals(ordered, barType, instrument, interval, lastClose);
        return BarWindow.Capped(BarWindow.Closed(filled, barType, start, end, now), start, take);
    }

    private static Task<JsonElement> FuturesCandlesAsync(KucoinHttp http, BarType barType, long fromMs, long toMs, CancellationToken ct) =>
        http.GetPublicAsync("/api/v1/kline/query", new Dictionary<string, string>
        {
            ["symbol"] = KucoinFuturesVenue.ToRawSymbol(barType.InstrumentId),
            ["granularity"] = KucoinFuturesVenue.Granularity(barType.Spec).ToString(CultureInfo.InvariantCulture),
            ["from"] = fromMs.ToString(CultureInfo.InvariantCulture),
            ["to"] = toMs.ToString(CultureInfo.InvariantCulture),
        }, ct);

    /// <summary>
    /// A futures row as a bar. The volume the venue reports is a number of contracts, turned into base currency here
    /// so that a bar off this venue measures the same thing as a bar off any other.
    /// </summary>
    private static Bar ToFuturesBar(JsonElement k, BarType barType, Instrument instrument, UnixNanos close) =>
        new(barType,
            instrument.MakePrice(k[1].DecValue()),
            instrument.MakePrice(k[2].DecValue()),
            instrument.MakePrice(k[3].DecValue()),
            instrument.MakePrice(k[4].DecValue()),
            KucoinFuturesVenue.ToQuantity(instrument, k[5].DecValue()),
            close,
            close);

    /// <summary>The close of the newest candle that opened before <paramref name="beforeOpen"/>, looked for a few pages back.</summary>
    private static async Task<Price?> FuturesCloseBeforeAsync(KucoinHttp http, BarType barType, Instrument instrument, long beforeOpen, CancellationToken ct)
    {
        long interval = barType.Spec.IntervalNanos;
        long intervalMs = interval / UnixNanos.NanosPerMillisecond;
        long beforeOpenMs = beforeOpen / UnixNanos.NanosPerMillisecond;
        long toMs = beforeOpenMs;
        for (int page = 0; page < SeedPages && toMs > 0; page++)
        {
            long fromMs = Math.Max(0, toMs - (KucoinFuturesVenue.CandlePage * intervalMs));
            JsonElement data = await FuturesCandlesAsync(http, barType, fromMs, toMs, ct).ConfigureAwait(false);
            long newest = long.MinValue;
            Price? close = null;
            foreach (JsonElement k in data.EnumerateArray())
            {
                long openMs = k[0].LongValue();
                if (openMs < beforeOpenMs && openMs > newest)
                {
                    newest = openMs;
                    close = instrument.MakePrice(k[4].DecValue());
                }
            }

            if (close is not null || fromMs == 0)
            {
                return close;
            }

            toMs = fromMs;
        }

        return null;
    }

    /// <summary>
    /// The funding a perpetual was charged at each settlement in a window. The venue answers newest first here and
    /// oldest first for candles, on the same API; this returns oldest first, which is the order everything that
    /// stores history expects and the order every other venue is read in.
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        KucoinHttp http,
        InstrumentId instrumentId,
        DateTimeOffset start,
        DateTimeOffset? end = null,
        CancellationToken ct = default) =>
        FetchFundingRatesAsync(
            http,
            instrumentId,
            UnixNanos.FromDateTimeOffset(start).ToMilliseconds(),
            end is { } e ? UnixNanos.FromDateTimeOffset(e).ToMilliseconds() : null,
            ct);

    /// <inheritdoc cref="FetchFundingRatesAsync(KucoinHttp, InstrumentId, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>
    internal static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        KucoinHttp http,
        InstrumentId instrumentId,
        long? startMs,
        long? endMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        long to = endMs ?? UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow).ToMilliseconds();
        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["symbol"] = KucoinFuturesVenue.ToRawSymbol(instrumentId),
            ["from"] = (startMs ?? 0).ToString(CultureInfo.InvariantCulture),
            ["to"] = to.ToString(CultureInfo.InvariantCulture),
        };

        JsonElement data = await http.GetPublicAsync("/api/v1/contract/funding-rates", query, ct).ConfigureAwait(false);
        List<FundingRateUpdate> rates = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return rates;
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            UnixNanos at = row.Ms("timepoint");
            rates.Add(new FundingRateUpdate(instrumentId, row.Dec("fundingRate"), null, at, at));
        }

        return [.. rates.DistinctBy(r => r.TsEvent).OrderBy(r => r.TsEvent.Value)];
    }
}
