using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Bybit;

/// <summary>
/// Bybit history read the way a running node reads it, without a node. The data client answers
/// <c>RequestFundingRates</c> with this, and anything that stores history - a catalog download, a tool - calls the
/// same method, so what is stored and what a node receives can never disagree about a venue's behaviour.
/// <para>
/// Paging, ordering and page sizes are venue behaviour and belong here rather than in whatever wants the data: a
/// second copy elsewhere is a copy that silently stops matching this one the day the venue changes a default.
/// </para>
/// </summary>
public static class BybitHistory
{
    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: the candle history a
    /// node receives, fetched without one.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(BybitHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(http, instrument, barType, UnixNanos.FromDateTimeOffset(start), UnixNanos.FromDateTimeOffset(end), null, UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given; one page of the
    /// venue's own size when it is not.
    /// <para>
    /// The venue answers newest first, so each page is reversed and prepended, and the window is walked backwards
    /// from the end until a page comes back short or reaches the start. A bar is stamped at its close, which is its
    /// start plus the interval: the venue reports a candle by the time it opened.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(
        BybitHttp http,
        Instrument instrument,
        BarType barType,
        UnixNanos? start,
        UnixNanos? end,
        int? limit,
        UnixNanos now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(instrument);

        List<Bar> bars = new();
        // No limit and a start means "the window", not "a page of it": this venue answers newest first, so a
        // page cap with a start given returned the LAST thousand bars of the window and dropped the beginning.
        int count = BarWindow.Wanted(limit, start, BybitVenue.KlinePage);
        long intervalMs = barType.Spec.IntervalNanos / 1_000_000L;

        // As Binance: the venue filters by a candle's open, so the window is asked for one interval earlier and the
        // shared rule decides what belongs.
        long? from = start is { } widened ? Math.Max(0, widened.ToMilliseconds() - intervalMs) : null;
        long to = (end ?? now).ToMilliseconds();
        while (bars.Count < count)
        {
            int asked = Math.Min(BybitVenue.KlinePage, count - bars.Count);
            Dictionary<string, string> query = new()
            {
                ["category"] = http.Category,
                ["symbol"] = BybitVenue.ToRawSymbol(barType.InstrumentId),
                ["interval"] = BybitVenue.Interval(barType.Spec),
                ["limit"] = asked.ToString(CultureInfo.InvariantCulture),
                ["end"] = to.ToString(CultureInfo.InvariantCulture),
            };
            if (from is { } s)
            {
                query["start"] = s.ToString(CultureInfo.InvariantCulture);
            }

            JsonElement result = await http.GetPublicAsync("/v5/market/kline", query, ct).ConfigureAwait(false);
            List<Bar> page = new();
            long oldest = long.MaxValue;
            foreach (JsonElement k in result.GetProperty("list").EnumerateArray())
            {
                long startMs = long.Parse(k[0].GetString()!, CultureInfo.InvariantCulture);
                oldest = Math.Min(oldest, startMs);
                UnixNanos close = UnixNanos.FromMilliseconds(startMs).AddNanos(barType.Spec.IntervalNanos);
                page.Add(new Bar(barType, instrument.MakePrice(k[1].DecValue()), instrument.MakePrice(k[2].DecValue()), instrument.MakePrice(k[3].DecValue()), instrument.MakePrice(k[4].DecValue()),
                    instrument.MakeQuantity(k[5].DecValue()), close, close));
            }

            if (page.Count == 0)
            {
                break;
            }

            page.Reverse();
            bars.InsertRange(0, page);
            if (page.Count < asked || (from is { } s2 && oldest <= s2))
            {
                break;
            }

            to = oldest - 1;
        }

        return BarWindow.Capped(BarWindow.Closed(bars, barType, start, end, now), start, count);
    }
    /// <summary>
    /// Every funding rate charged between <paramref name="start"/> and <paramref name="end"/>, oldest first.
    /// <para>
    /// `/v5/market/funding/history` answers newest first, two hundred rows to a page, so a period is walked backwards
    /// from its end - each page asking for what is older than the last - and turned round at the finish.
    /// </para>
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        BybitHttp http,
        InstrumentId instrumentId,
        DateTimeOffset start,
        DateTimeOffset? end = null,
        CancellationToken ct = default) =>
        FetchFundingRatesAsync(
            http,
            instrumentId,
            UnixNanos.FromDateTimeOffset(start).ToMilliseconds(),
            end is { } e ? UnixNanos.FromDateTimeOffset(e).ToMilliseconds() : null,
            null,
            ct);

    /// <inheritdoc cref="FetchFundingRatesAsync(BybitHttp, InstrumentId, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>
    internal static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        BybitHttp http,
        InstrumentId instrumentId,
        long? startMs,
        long? endMs,
        int? limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        List<FundingRateUpdate> rates = new();
        long? until = endMs;
        while (true)
        {
            Dictionary<string, string> query = new()
            {
                ["category"] = http.Category,
                ["symbol"] = BybitVenue.ToRawSymbol(instrumentId),
                ["limit"] = BybitVenue.FundingPage.ToString(CultureInfo.InvariantCulture),
            };
            if (startMs is { } begins)
            {
                query["startTime"] = begins.ToString(CultureInfo.InvariantCulture);
            }

            if (until is { } ends)
            {
                query["endTime"] = ends.ToString(CultureInfo.InvariantCulture);
            }

            JsonElement result = await http.GetPublicAsync("/v5/market/funding/history", query, ct).ConfigureAwait(false);
            JsonElement list = result.GetProperty("list");
            int before = rates.Count;
            long oldest = long.MaxValue;
            foreach (JsonElement r in list.EnumerateArray())
            {
                long ms = long.Parse(r.Str("fundingRateTimestamp"), CultureInfo.InvariantCulture);
                oldest = Math.Min(oldest, ms);
                UnixNanos ts = UnixNanos.FromMilliseconds(ms);
                rates.Add(new FundingRateUpdate(instrumentId, r.Dec("fundingRate"), null, ts, ts));
            }

            if (rates.Count == before
                || list.GetArrayLength() < BybitVenue.FundingPage
                || (limit is { } wanted && rates.Count >= wanted)
                || oldest == long.MaxValue)
            {
                break;
            }

            // One millisecond before the oldest of this page, or the same page comes back for ever.
            until = oldest - 1L;
        }

        rates.Reverse();
        return rates;
    }
}
