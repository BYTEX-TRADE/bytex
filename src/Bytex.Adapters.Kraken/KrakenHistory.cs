using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Kraken;

/// <summary>
/// Kraken's candle and funding history, read the way a running node sees it. The data clients answer
/// <c>RequestBars</c> and <c>RequestFundingRates</c> with this, and anything that stores history - a catalog download
/// with no node at all - calls the same methods, so stored bars and live bars never disagree about what an interval
/// looked like.
/// </summary>
public static class KrakenHistory
{
    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: every bar that closes
    /// at or after the start and opens at or before the end, never the candle that is still forming.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(KrakenHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(
            http,
            instrument,
            barType,
            UnixNanos.FromDateTimeOffset(start),
            UnixNanos.FromDateTimeOffset(end),
            null,
            UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given. As in the other
    /// adapters, <paramref name="end"/> bounds a candle's open time and <paramref name="start"/> its close time.
    /// <para>
    /// One entry point for both of Kraken's platforms, because a caller storing history has an instrument and a
    /// window and no business knowing that this venue answers for them out of two APIs on two hosts, with candles
    /// stamped in different units, paged from opposite ends, and capped at different numbers.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(KrakenHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(instrument);

        if (barType.Spec.IntervalNanos <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; Kraken history has time bars only.", nameof(barType));
        }

        return http.ProductType == KrakenProductType.Futures
            ? await FetchFuturesBarsAsync(http, instrument, barType, start, end, limit, now, ct).ConfigureAwait(false)
            : await FetchSpotBarsAsync(http, instrument, barType, start, end, limit, now, ct).ConfigureAwait(false);
    }

    // ----- spot -----
    //
    // One request and no paging loop, which is a fact about the venue rather than a shortcut.
    //
    // /0/public/OHLC takes `since` as a lower bound and answers with at most 720 intervals - and when the window
    // asked for is wider than that, it answers with the NEWEST 720 rather than the oldest. Measured: `since=0` on
    // one-minute candles returned the 720 most recent, and `since` set 2000 minutes back returned the same 720 rows
    // starting 720 minutes ago. So there is no parameter that reaches further back and nothing to page with: a loop
    // would ask for an older window, be handed the newest page again, and either spin forever or return the newest
    // bars believing they were the oldest.
    //
    // The consequence is worth stating plainly because it decides what this venue can be used for: one-minute spot
    // history older than twelve hours cannot be fetched from Kraken at all. Longer bars reach proportionally
    // further - 720 days of daily candles, and the weekly series came back complete at 678 rows because the venue
    // holds fewer than 720 weeks.
    //
    // The row is [open time in SECONDS, open, high, low, close, vwap, volume, count] and the last row is the candle
    // still forming - the answer's own `last` field is the open time of the last CLOSED one. BarWindow.Closed drops
    // it, so nothing here has to read `last` to know which row not to trust.

    private static async Task<IReadOnlyList<Bar>> FetchSpotBarsAsync(KrakenHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct)
    {
        long interval = barType.Spec.IntervalNanos;
        int take = BarWindow.Wanted(limit, start, KrakenVenue.CandlePage);
        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["pair"] = KrakenVenue.ToRawSymbol(barType.InstrumentId),
            ["interval"] = KrakenVenue.Interval(barType.Spec).ToString(CultureInfo.InvariantCulture),
        };

        if (start is { } from)
        {
            // A candle is filtered by its open and the one covering the start of the window opens before it, so the
            // bound is moved back one interval; the window rule then drops anything that closed too early.
            long since = Math.Max(0, (from.Value - interval) / UnixNanos.NanosPerSecond);
            query["since"] = since.ToString(CultureInfo.InvariantCulture);
        }

        JsonElement result = await http
            .GetPublicAsync(KrakenVenue.RestVersion + "/public/OHLC", query, ct)
            .ConfigureAwait(false);

        List<Bar> bars = new();
        foreach (JsonElement row in Rows(result))
        {
            long openSeconds = row[0].LongValue();
            UnixNanos close = new((openSeconds * UnixNanos.NanosPerSecond) + interval);
            bars.Add(new Bar(
                barType,
                instrument.MakePrice(row[1].DecValue()),
                instrument.MakePrice(row[2].DecValue()),
                instrument.MakePrice(row[3].DecValue()),
                instrument.MakePrice(row[4].DecValue()),
                instrument.MakeQuantity(row[6].DecValue()),
                close,
                close));
        }

        return BarWindow.Capped(BarWindow.Closed(bars, barType, start, end, now), start, take);
    }

    /// <summary>
    /// The candle rows out of a spot answer. The result is an object holding one property per pair plus a
    /// <c>last</c> cursor, and the pair's property name is whichever of the venue's three spellings the request
    /// happened to use - <c>pair=XBTUSD</c> comes back as <c>XXBTZUSD</c> and <c>pair=BTC/USD</c> comes back as
    /// <c>BTC/USD</c> - so the rows are found by being the array rather than by a name.
    /// </summary>
    private static IEnumerable<JsonElement> Rows(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty property in result.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in property.Value.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 7)
                {
                    yield return row;
                }
            }
        }
    }

    // ----- futures -----
    //
    // Everything about a futures candle differs from a spot one. It comes from a separate service on the futures
    // host that shares neither the derivatives API's envelope nor its error shape: success is
    // { candles, more_candles } with no wrapper, and a failure is PLAIN TEXT with an HTTP 400.
    //
    // The row is an object rather than an array - { time, open, high, low, close, volume } - the time is the open in
    // MILLISECONDS where spot is seconds, `from` and `to` are in SECONDS and both inclusive by a candle's open, one
    // request answers with 2000 rows where the service documents 5000, and it pages FORWARD from `from` where spot
    // cannot page at all. A fetch copied across from spot would read four real prices out of the wrong slots and
    // cover a fraction of the window asked for.
    //
    // It does page, though, which is the one thing it has that spot does not: `more_candles` says whether the window
    // was cut short, so a wide window is walked in 2000-row steps and the whole of it really comes back.

    private static async Task<IReadOnlyList<Bar>> FetchFuturesBarsAsync(KrakenHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct)
    {
        long interval = barType.Spec.IntervalNanos;
        long intervalSeconds = interval / UnixNanos.NanosPerSecond;
        int take = BarWindow.Wanted(limit, start, KrakenFuturesVenue.CandlePage);
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        long toSeconds = lastOpen / UnixNanos.NanosPerSecond;

        // With a start the window is given. Without one the caller wants the newest `take`, so the window is walked
        // back far enough to hold them - and one interval further, because a candle is filtered by its open and the
        // one covering the boundary starts before it.
        long fromSeconds = start is { } s
            ? Math.Max(0, (s.Value / UnixNanos.NanosPerSecond) - intervalSeconds)
            : Math.Max(0, toSeconds - ((Math.Min(take, KrakenFuturesVenue.CandlePage) + 1L) * intervalSeconds));

        List<Bar> bars = new();
        long cursor = fromSeconds;
        while (cursor <= toSeconds)
        {
            JsonElement page = await CandlesAsync(http, barType, cursor, toSeconds, ct).ConfigureAwait(false);
            int count = 0;
            long newest = long.MinValue;
            if (page.TryGetProperty("candles", out JsonElement candles) && candles.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement candle in candles.EnumerateArray())
                {
                    count++;
                    long openMs = candle.Long("time");
                    newest = Math.Max(newest, openMs);
                    UnixNanos close = new((openMs * UnixNanos.NanosPerMillisecond) + interval);
                    bars.Add(new Bar(
                        barType,
                        instrument.MakePrice(candle.Dec("open")),
                        instrument.MakePrice(candle.Dec("high")),
                        instrument.MakePrice(candle.Dec("low")),
                        instrument.MakePrice(candle.Dec("close")),
                        instrument.MakeQuantity(candle.Dec("volume")),
                        close,
                        close));
                }
            }

            // The service says for itself whether it cut the window short, so the end of the history is read off its
            // own answer rather than guessed from a page that did not fill.
            if (count == 0 || !page.Bool("more_candles") || newest == long.MinValue)
            {
                break;
            }

            cursor = (newest / 1000L) + intervalSeconds;
        }

        return BarWindow.Capped(BarWindow.Closed(bars, barType, start, end, now), start, take);
    }

    private static Task<JsonElement> CandlesAsync(KrakenHttp http, BarType barType, long fromSeconds, long toSeconds, CancellationToken ct) =>
        http.GetUnwrappedAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{KrakenFuturesVenue.ChartsPath}/{KrakenFuturesVenue.TradeCandles}/{Uri.EscapeDataString(KrakenFuturesVenue.ToRawSymbol(barType.InstrumentId))}/{KrakenFuturesVenue.Resolution(barType.Spec)}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from"] = fromSeconds.ToString(CultureInfo.InvariantCulture),
                ["to"] = toSeconds.ToString(CultureInfo.InvariantCulture),
            },
            ct);

    // ----- funding -----

    /// <summary>
    /// The funding a perpetual was charged at each settlement in a window, oldest first.
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        KrakenHttp http,
        InstrumentId instrumentId,
        DateTimeOffset start,
        DateTimeOffset? end = null,
        CancellationToken ct = default) =>
        FetchFundingRatesAsync(
            http,
            instrumentId,
            UnixNanos.FromDateTimeOffset(start),
            end is { } e ? UnixNanos.FromDateTimeOffset(e) : null,
            null,
            ct);

    /// <summary>
    /// The funding settlements of a window, oldest first, the newest <paramref name="limit"/> of them when a limit
    /// is given.
    /// <para>
    /// The endpoint takes no window and has no paging. Measured: it answered 8786 hourly settlements for
    /// <c>PF_XBTUSD</c> - one year, to the hour - and answered the identical 8786 rows for a request carrying
    /// <c>from</c> and <c>to</c> an hour apart. It ignores both parameters silently, so the window is applied here
    /// and a paging loop written against those parameters would fetch the same year over and over.
    /// </para>
    /// <para>
    /// A year is therefore all the funding history this venue has, which is worth knowing before a multi-year
    /// backtest of a perpetual is priced as though its carry were free before that.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        KrakenHttp http,
        InstrumentId instrumentId,
        UnixNanos? start,
        UnixNanos? end,
        int? limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.ProductType != KrakenProductType.Futures)
        {
            throw new ArgumentException(
                "Kraken spot pays no funding and has none to fetch. Point the client at the futures platform.",
                nameof(http));
        }

        JsonElement answer = await http.GetPublicAsync(
            KrakenFuturesVenue.ApiV4 + "/historicalfundingrates",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = KrakenFuturesVenue.ToRawSymbol(instrumentId),
            },
            ct).ConfigureAwait(false);

        if (!answer.TryGetProperty("rates", out JsonElement rates) || rates.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<FundingRateUpdate> settlements = new();
        foreach (JsonElement rate in rates.EnumerateArray())
        {
            UnixNanos at = rate.Iso("timestamp");
            if (at == default || (start is { } from && at < from) || (end is { } to && at > to))
            {
                continue;
            }

            // The relative rate, not the absolute amount beside it. See KrakenFuturesVenue.RelativeRateField: the
            // other field is a charge per contract in the settlement currency and reads as a rate of 133% on
            // bitcoin.
            settlements.Add(new FundingRateUpdate(
                instrumentId,
                rate.Dec(KrakenFuturesVenue.RelativeRateField),
                null,
                at,
                at));
        }

        List<FundingRateUpdate> ordered = [.. settlements.DistinctBy(r => r.TsEvent).OrderBy(r => r.TsEvent.Value)];

        // A limit is counted from whichever end the caller anchored, exactly as it is for bars: with a start the
        // window begins there, and with none the newest settlements are the ones wanted.
        if (limit is { } wanted && ordered.Count > wanted)
        {
            return start is null ? [.. ordered.TakeLast(wanted)] : [.. ordered.Take(wanted)];
        }

        return ordered;
    }
}
