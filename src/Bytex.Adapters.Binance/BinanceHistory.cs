using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Binance;

/// <summary>
/// Binance history read the way a running node reads it, without a node. The data client answers
/// <c>RequestFundingRates</c> with this, and anything that stores history - a catalog download, a tool - calls the
/// same method, so what is stored and what a node receives can never disagree about a venue's behaviour.
/// <para>
/// Paging, ordering and page sizes are venue behaviour and belong here rather than in whatever wants the data: a
/// second copy elsewhere is a copy that silently stops matching this one the day the venue changes a default.
/// </para>
/// </summary>
public static class BinanceHistory
{
    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: the candle history a
    /// node receives, fetched without one.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(BinanceHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(http, instrument, barType, UnixNanos.FromDateTimeOffset(start), UnixNanos.FromDateTimeOffset(end), null, UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given; one page of the
    /// venue's own size when it is not, which is what the venue gives for a request that names no count.
    /// <para>
    /// The direction the window is walked depends on whether a start was given, and it has to: with
    /// <c>startTime</c> set the venue answers from that time forwards, so the window is walked forwards from the
    /// start. Paging backwards from <c>endTime</c> in that case read the first page as the newest one, saw its first
    /// open at or before the start and stopped - losing everything after the first thousand bars of the window.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(
        BinanceHttp http,
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
        int page = BinanceVenue.KlinePage(http.AccountType);
        // No limit and a start means "the window", not "a page of it". Defaulting to one page here made the
        // start/end overload - which documents itself as the bars between two times - return the first thousand and
        // stop, so anything filling a catalog with it got a silently shorter history and a backtest on it just
        // covered less time and looked fine. KuCoin's helper had this right; these two did not.
        int count = BarWindow.Wanted(limit, start, page);
        long intervalMs = barType.Spec.IntervalNanos / 1_000_000L;

        // This venue filters by a candle's open time, so a window asked for from its own start comes back without the
        // bar that opens before it and closes inside it - the first moment of the period would be uncovered. One
        // interval earlier is asked for, and BarWindow decides what belongs.
        //
        // A candle row is the same twelve fields on all three families, and all three stamp element 0 with the
        // OPEN and element 6 with the CLOSE, in milliseconds, the close being one millisecond short of the next
        // open - measured on the coin-margined family on 2026-09-25 as 59,999 between them on a minute bar. The
        // close plus that millisecond is the bar's real end, which is what a bar is stamped with below. What
        // element 5 MEANS differs - base units on spot and USD-margined futures, a number of contracts on the
        // coin-margined one - and it needs no branch here, because it is the unit that family's instrument is
        // sized in either way.
        long? from = start is { } s ? Math.Max(0, s.ToMilliseconds() - intervalMs) : null;
        long to = (end ?? now).ToMilliseconds();

        if (from is { } forwardFrom)
        {
            long cursor = forwardFrom;
            while (bars.Count < count && cursor <= to)
            {
                int want = Math.Min(page, count - bars.Count);
                Dictionary<string, string> forward = new()
                {
                    ["symbol"] = BinanceVenue.ToRawSymbol(barType.InstrumentId),
                    ["interval"] = BinanceVenue.Interval(barType.Spec),
                    ["limit"] = want.ToString(CultureInfo.InvariantCulture),
                    ["startTime"] = cursor.ToString(CultureInfo.InvariantCulture),
                    ["endTime"] = to.ToString(CultureInfo.InvariantCulture),
                };

                using JsonDocument forwardDoc = await http.GetPublicAsync(http.Prefix + "/klines", forward, BinanceVenue.Weights.Klines, ct).ConfigureAwait(false);
                int read = 0;
                long lastOpen = cursor;
                foreach (JsonElement k in forwardDoc.RootElement.EnumerateArray())
                {
                    read++;
                    lastOpen = k[0].GetInt64();
                    UnixNanos closed = UnixNanos.FromMilliseconds(k[6].GetInt64() + 1);
                    bars.Add(new Bar(barType, instrument.MakePrice(k[1].DecValue()), instrument.MakePrice(k[2].DecValue()), instrument.MakePrice(k[3].DecValue()),
                        instrument.MakePrice(k[4].DecValue()), instrument.MakeQuantity(k[5].DecValue()), closed, closed));
                }

                if (read == 0 || read < want)
                {
                    // The venue had nothing more to give inside the window.
                    break;
                }

                cursor = lastOpen + 1;
            }

            return BarWindow.Capped(BarWindow.Closed(bars, barType, start, end, now), start, count);
        }

        while (bars.Count < count)
        {
            int asked = Math.Min(page, count - bars.Count);
            Dictionary<string, string> query = new()
            {
                ["symbol"] = BinanceVenue.ToRawSymbol(barType.InstrumentId),
                ["interval"] = BinanceVenue.Interval(barType.Spec),
                ["limit"] = asked.ToString(CultureInfo.InvariantCulture),
                ["endTime"] = to.ToString(CultureInfo.InvariantCulture),
            };

            using JsonDocument doc = await http.GetPublicAsync(http.Prefix + "/klines", query, BinanceVenue.Weights.Klines, ct).ConfigureAwait(false);
            List<Bar> pageBars = new();
            foreach (JsonElement k in doc.RootElement.EnumerateArray())
            {
                UnixNanos close = UnixNanos.FromMilliseconds(k[6].GetInt64() + 1);
                pageBars.Add(new Bar(barType, instrument.MakePrice(k[1].DecValue()), instrument.MakePrice(k[2].DecValue()), instrument.MakePrice(k[3].DecValue()), instrument.MakePrice(k[4].DecValue()),
                    instrument.MakeQuantity(k[5].DecValue()), close, close));
            }

            if (pageBars.Count == 0)
            {
                break;
            }

            bars.InsertRange(0, pageBars);
            long firstOpen = doc.RootElement[0][0].GetInt64();
            to = firstOpen - 1;
            if (pageBars.Count < asked)
            {
                // Fewer rows than were asked for: the venue has nothing older inside the window.
                break;
            }
        }

        return BarWindow.Capped(BarWindow.Closed(bars, barType, start, end, now), start, count);
    }
    /// <summary>
    /// Every funding rate charged between <paramref name="start"/> and <paramref name="end"/>, oldest first.
    /// <para>
    /// The venue's <c>fundingRate</c> read answers oldest first, a thousand rows to a page, so a period longer than
    /// that is walked forwards from where the last page ended. Only a futures host has the endpoint: spot pays no
    /// funding. Both futures hosts serve it under their own prefix, which is why the path is the client's rather
    /// than written out here.
    /// </para>
    /// <para>
    /// Measured on the coin-margined family on 2026-09-25: 1,000 rows came back for a limit of 1,000 from a start
    /// in 2021 and 1,001 was refused, so the page size is the same; asked with no limit at all it answers 500
    /// rather than the USD-margined family's 100, which is why the limit is always sent rather than left to the
    /// venue. A DATED contract of that family answers an empty array - BTCUSD_261225 returned <c>[]</c> - which is
    /// correct and not a gap: a contract that delivers converges by delivering and is charged no funding.
    /// </para>
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        BinanceHttp http,
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

    /// <inheritdoc cref="FetchFundingRatesAsync(BinanceHttp, InstrumentId, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>
    internal static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        BinanceHttp http,
        InstrumentId instrumentId,
        long? startMs,
        long? endMs,
        int? limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        List<FundingRateUpdate> rates = new();
        long? from = startMs;
        while (true)
        {
            Dictionary<string, string> query = new()
            {
                ["symbol"] = BinanceVenue.ToRawSymbol(instrumentId),
                ["limit"] = BinanceVenue.FundingPage.ToString(CultureInfo.InvariantCulture),
            };
            if (from is { } begins)
            {
                query["startTime"] = begins.ToString(CultureInfo.InvariantCulture);
            }

            if (endMs is { } ends)
            {
                query["endTime"] = ends.ToString(CultureInfo.InvariantCulture);
            }

            using JsonDocument doc = await http.GetPublicAsync(http.Prefix + "/fundingRate", query, BinanceVenue.Weights.Trades, ct).ConfigureAwait(false);
            int page = 0;
            long newest = long.MinValue;
            foreach (JsonElement r in doc.RootElement.EnumerateArray())
            {
                page++;
                long ms = r.Long("fundingTime");
                newest = Math.Max(newest, ms);
                UnixNanos ts = UnixNanos.FromMilliseconds(ms);
                rates.Add(new FundingRateUpdate(instrumentId, r.Dec("fundingRate"), null, ts, ts));
            }

            if (page < BinanceVenue.FundingPage || (limit is { } wanted && rates.Count >= wanted) || newest == long.MinValue)
            {
                break;
            }

            // One millisecond after the newest of this page, or the same page comes back for ever.
            from = newest + 1L;
        }

        return rates;
    }
}
