using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Bitget;

/// <summary>
/// Bitget history read the way a running node reads it, without a node. The data client answers <c>RequestBars</c> and
/// <c>RequestFundingRates</c> with these methods, and anything that stores history - a catalog download, a tool -
/// calls the same ones, so what is stored and what a node receives can never disagree about the venue's behaviour.
/// <para>
/// Paging, page sizes and ordering are venue behaviour and belong here rather than in whatever wants the data: a
/// second copy elsewhere is a copy that stops matching this one the day the venue changes a default.
/// </para>
/// </summary>
public static class BitgetHistory
{
    /// <summary>
    /// Where closed candles answer. There are two candle endpoints on each market and this is the one that holds the
    /// venue's whole history; see <see cref="BitgetVenue.HistoryCandlePage"/> for what the other one does instead.
    /// </summary>
    public const string SpotHistoryCandlesPath = "/api/v2/spot/market/history-candles";

    /// <summary>Where closed candles answer on the derivative markets.</summary>
    public const string FuturesHistoryCandlesPath = "/api/v2/mix/market/history-candles";

    /// <summary>Where the funding a perpetual has charged answers. The derivative markets only.</summary>
    public const string FundingHistoryPath = "/api/v2/mix/market/history-fund-rate";

    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: the candle history a node
    /// receives, fetched without one.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(BitgetHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(http, instrument, barType, UnixNanos.FromDateTimeOffset(start), UnixNanos.FromDateTimeOffset(end), null, UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given; one page of the
    /// venue's own size when there is neither a limit nor a start.
    /// <para>
    /// Three measured facts shape this, and each of them is the kind that produces plausible wrong answers rather than
    /// an error.
    /// </para>
    /// <para>
    /// The endpoint answers CLOSED candles only. Asked with an end inside the current minute it stopped at the
    /// previous one, so the candle still forming is never in the answer and nothing has to be trimmed off it. Its
    /// sibling <c>market/candles</c> does include it, which is one of the reasons the two are not interchangeable.
    /// </para>
    /// <para>
    /// It ignores <c>startTime</c> entirely. Asked for a window a year old with both ends given it returned two
    /// hundred rows ENDING at the end and beginning before the start, and asked with a start alone it returned the
    /// newest rows it had - and the spot endpoint refuses the request outright without an end. So only the end is
    /// sent, the window is walked backwards from it a page at a time, and which rows belong is decided by the shared
    /// rule rather than by the venue.
    /// </para>
    /// <para>
    /// The end bounds a candle's OPEN and excludes it: asked to end exactly at a minute boundary it stopped at the
    /// candle before. So the bound sent is one millisecond past the last open wanted.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(
        BitgetHttp http,
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

        long interval = barType.Spec.IntervalNanos;
        if (interval <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; Bitget history has time bars only.", nameof(barType));
        }

        // No limit and a start means "the window", not "a page of it": this endpoint answers backwards from its end,
        // so a page cap with a start given would return the LAST page of the window and silently drop the beginning.
        int wanted = BarWindow.Wanted(limit, start, BitgetVenue.HistoryCandlePage);
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        long? startMs = start is { } from ? from.ToMilliseconds() : null;
        long endExclusiveMs = (lastOpen / UnixNanos.NanosPerMillisecond) + 1;

        List<Bar> bars = [];
        while (bars.Count < wanted && endExclusiveMs > 0)
        {
            JsonElement data = await CandlesAsync(http, barType, endExclusiveMs, ct).ConfigureAwait(false);
            int count = 0;
            long oldest = long.MaxValue;
            foreach (JsonElement row in data.EnumerateArray())
            {
                count++;
                long openMs = row[0].LongValue();
                oldest = Math.Min(oldest, openMs);
                UnixNanos close = UnixNanos.FromMilliseconds(openMs).AddNanos(interval);
                bars.Add(new Bar(
                    barType,
                    instrument.MakePrice(row[1].DecValue()),
                    instrument.MakePrice(row[2].DecValue()),
                    instrument.MakePrice(row[3].DecValue()),
                    instrument.MakePrice(row[4].DecValue()),

                    // Index five is the volume in base currency on both markets. The spot row has eight columns and
                    // the derivative row seven - spot adds a USDT-converted turnover the derivatives leave out - but
                    // the first six are the same fields in the same order, so one reader serves both.
                    instrument.MakeQuantity(row[5].DecValue()),
                    close,
                    close));
            }

            // A page that did not fill has reached the end of what the venue holds, and a page that reached the start
            // of the window has nothing older worth asking for.
            if (count < BitgetVenue.HistoryCandlePage || oldest == long.MaxValue || (startMs is { } s && oldest <= s))
            {
                break;
            }

            // The end excludes the candle that opens on it, so the oldest open of this page is exactly the bound that
            // asks for what came before it.
            endExclusiveMs = oldest;
        }

        return BarWindow.Capped(BarWindow.Closed(bars, barType, start, end, now), start, wanted);
    }

    private static Task<JsonElement> CandlesAsync(BitgetHttp http, BarType barType, long endExclusiveMs, CancellationToken ct)
    {
        Dictionary<string, string> query = http.Query(
            ("symbol", http.ToRawSymbol(barType.InstrumentId)),
            ("granularity", BitgetVenue.RestGranularity(barType.Spec, http.ProductType)),
            ("endTime", endExclusiveMs.ToString(CultureInfo.InvariantCulture)),
            ("limit", BitgetVenue.HistoryCandlePage.ToString(CultureInfo.InvariantCulture)));

        string path = http.ProductType == BitgetProductType.Spot ? SpotHistoryCandlesPath : FuturesHistoryCandlesPath;
        return http.GetPublicAsync(path, query, ct);
    }

    /// <summary>
    /// Every funding settlement charged between <paramref name="start"/> and <paramref name="end"/>, oldest first.
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        BitgetHttp http,
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

    /// <summary>
    /// The funding settlements of a perpetual, oldest first.
    /// <para>
    /// This endpoint takes no window at all - no start, no end, no cursor. It pages by page NUMBER, newest first, and
    /// the walk stops when a page comes back short or reaches past the start asked for. Bounding it by
    /// <see cref="BitgetVenue.MaxFundingPages"/> is deliberate: paging by number has no natural end, so an answer that
    /// never shortened would loop for ever.
    /// </para>
    /// <para>
    /// The page size is the venue's own and not the caller's, because asking for more is SILENTLY reduced. Asked for
    /// 200 and for 500 the venue answered 100 rows and code 00000 both times, with no warning. A loop that compared
    /// what came back with what it asked for would read the first page as the end of history.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        BitgetHttp http,
        InstrumentId instrumentId,
        long? startMs,
        long? endMs,
        int? limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.ProductType == BitgetProductType.Spot)
        {
            throw new ArgumentException(
                "Bitget's spot market is charged no funding and has no funding history to fetch. Point the client at "
                + "one of the perpetual product types.",
                nameof(http));
        }

        List<FundingRateUpdate> rates = [];
        for (int page = BitgetVenue.FirstPage; page < BitgetVenue.FirstPage + BitgetVenue.MaxFundingPages; page++)
        {
            Dictionary<string, string> query = http.Query(
                ("symbol", http.ToRawSymbol(instrumentId)),
                ("pageSize", BitgetVenue.FundingPage.ToString(CultureInfo.InvariantCulture)),
                ("pageNo", page.ToString(CultureInfo.InvariantCulture)));

            JsonElement data = await http.GetPublicAsync(FundingHistoryPath, query, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            int count = 0;
            long oldest = long.MaxValue;
            foreach (JsonElement row in data.EnumerateArray())
            {
                count++;
                long ms = row.Long("fundingTime");
                oldest = Math.Min(oldest, ms);
                if ((startMs is { } from && ms < from) || (endMs is { } to && ms > to))
                {
                    continue;
                }

                UnixNanos at = UnixNanos.FromMilliseconds(ms);
                rates.Add(new FundingRateUpdate(instrumentId, row.Dec("fundingRate"), null, at, at));
            }

            if (count < BitgetVenue.FundingPage
                || oldest == long.MaxValue
                || (startMs is { } begins && oldest <= begins)
                || (limit is { } asked && rates.Count >= asked))
            {
                break;
            }
        }

        return [.. rates.DistinctBy(r => r.TsEvent).OrderBy(r => r.TsEvent.Value)];
    }
}
