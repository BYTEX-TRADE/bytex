using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Okx;

/// <summary>
/// OKX's candle and funding history read the way a running node sees it. The data client answers <c>RequestBars</c>
/// and <c>RequestFundingRates</c> with this, and anything that stores history - a catalog download - calls the same
/// methods, so stored bars and live bars never disagree about what an interval looked like.
/// <para>
/// Public and static, and needing nothing but an http client and an instrument, because a host that stores history
/// has no node to ask.
/// </para>
/// </summary>
public static class OkxHistory
{
    /// <summary>
    /// Where candles come from. There are two candle endpoints and only this one is used, which is a measured
    /// decision rather than a preference: <c>/api/v5/market/candles</c> keeps a recent window only, and asked for
    /// anything older it answers HTTP 200, code 0 and an EMPTY array - so a paging loop walking backwards through it
    /// reads "no more history" two days in and stops, successfully. This endpoint answered every window from two
    /// days to two hundred days back, and reaches forward to the candle still forming, so one endpoint covers the
    /// whole range and there is no seam between recent and old to get wrong.
    /// </summary>
    private const string CandlesPath = "/api/v5/market/history-candles";

    /// <summary>Where the funding a perpetual was actually charged comes from.</summary>
    private const string FundingPath = "/api/v5/public/funding-rate-history";

    /// <summary>
    /// What the venue writes in a candle's last field when the candle has closed. It states this itself, which no
    /// other venue here does, so whether a bar is finished is the venue's answer rather than a comparison against a
    /// clock: both candle endpoints include the interval still forming, and both mark it.
    /// </summary>
    private const string Confirmed = "1";

    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: every bar that closes
    /// at or after <paramref name="start"/> and opens at or before <paramref name="end"/>, never the candle that is
    /// still forming.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(OkxHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(http, instrument, barType, UnixNanos.FromDateTimeOffset(start), UnixNanos.FromDateTimeOffset(end), null, UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given. As in the other
    /// adapters, <paramref name="end"/> bounds a candle's open time and <paramref name="start"/> its close time.
    /// <para>
    /// The venue stamps a candle with its OPEN, in milliseconds, and returns rows newest first. Bars leave here
    /// close-stamped and oldest first, because that is what a bar is everywhere above an adapter.
    /// </para>
    /// <para>
    /// No gap filling, and that is measured rather than assumed. KuCoin and Binance both leave an interval with no
    /// trade out of their answer, so their adapters put a flat bar back; OKX writes the quiet interval itself. Three
    /// of the least traded pairs on the venue each returned sixty consecutive one-minute rows with zero volume and no
    /// gap at all, so filling here would be inventing bars beside real ones.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(OkxHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(instrument);

        long interval = barType.Spec.IntervalNanos;
        if (interval <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; OKX history has time bars only.", nameof(barType));
        }

        long intervalMs = interval / UnixNanos.NanosPerMillisecond;
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        int take = BarWindow.Wanted(limit, start, OkxVenue.CandlePage);

        // The venue's `after` is "rows whose open is strictly before this", so the cursor starts one millisecond past
        // the newest open that could belong to the window and walks back a page at a time.
        long cursor = (lastOpen / UnixNanos.NanosPerMillisecond) + 1;

        // And `before` is "rows whose open is strictly after this". A bar belongs to the window when it CLOSES
        // inside it, so the oldest open that can qualify is one interval before the start - and the bound is one
        // millisecond below that, since the venue's comparison excludes the value itself.
        long? beforeMs = start is { } s ? (s.Value / UnixNanos.NanosPerMillisecond) - intervalMs - 1 : null;

        List<Bar> bars = [];
        while (bars.Count < take && cursor > 0)
        {
            JsonElement data = await CandlesAsync(http, barType, cursor, beforeMs, ct).ConfigureAwait(false);
            int rows = 0;
            long oldest = long.MaxValue;
            foreach (JsonElement k in Items(data))
            {
                rows++;
                long openMs = k[0].LongValue();
                oldest = Math.Min(oldest, openMs);

                // The venue's own word for "this candle has closed". A row it has not confirmed is the interval in
                // progress, and a stored bar that says an interval closed when it has not is the one thing history
                // must never contain.
                if (k.GetArrayLength() <= ConfirmField || k[ConfirmField].ToString() != Confirmed)
                {
                    continue;
                }

                bars.Add(ToBar(k, barType, instrument, new UnixNanos(openMs * UnixNanos.NanosPerMillisecond + interval)));
            }

            // Nothing there, or a page that did not fill and so reached the end of what the venue holds in this
            // window. Counting the page rather than the bars kept is the point: a page can be full of unconfirmed
            // and out-of-window rows and still mean "there is more behind this".
            //
            // The last condition is the one that stops a node rather than a window: a full page whose oldest row is
            // not older than the cursor leaves the cursor where it was, so the same page comes back for ever. That
            // is what a venue ignoring the `after` bound looks like from here, and a loop without this guard hangs
            // on it instead of returning what it has.
            if (rows == 0 || rows < OkxVenue.CandlePage || oldest == long.MaxValue || oldest >= cursor)
            {
                break;
            }

            cursor = oldest;
        }

        IReadOnlyList<Bar> window = BarWindow.Closed(bars, barType, start, end, now);
        return BarWindow.Capped(window, start, take);
    }

    /// <summary>Which field of a candle row carries the venue's confirmation that the candle has closed.</summary>
    private const int ConfirmField = 8;

    private static Task<JsonElement> CandlesAsync(OkxHttp http, BarType barType, long afterMs, long? beforeMs, CancellationToken ct)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["instId"] = OkxVenue.ToRawSymbol(barType.InstrumentId),
            ["bar"] = OkxVenue.Interval(barType.Spec),
            ["limit"] = OkxVenue.CandlePage.ToString(CultureInfo.InvariantCulture),
            ["after"] = afterMs.ToString(CultureInfo.InvariantCulture),
        };

        if (beforeMs is { } before && before > 0)
        {
            // Both bounds together, which was measured working: a two-hour window came back as 120 one-minute rows.
            // Bounding the far end as well as the near one lets the venue stop rather than the loop, which is the
            // difference between one request and a walk to the start of the instrument's history.
            query["before"] = before.ToString(CultureInfo.InvariantCulture);
        }

        return http.GetPublicAsync(CandlesPath, query, ct);
    }

    /// <summary>
    /// A candle row as a bar: <c>[open time, open, high, low, close, vol, volCcy, volCcyQuote, confirm]</c>.
    /// <para>
    /// The volume is the field that differs between the venue's markets and the difference is silent. On spot
    /// <c>vol</c> is a quantity of the base currency; on a swap or a future it is a NUMBER OF CONTRACTS, and the base
    /// currency figure is in <c>volCcy</c> beside it. Measured on one minute of BTC-USDT-SWAP: 3498.64 contracts and
    /// 34.9864 BTC, which is the contract value of 0.01 exactly. A reader that took <c>vol</c> on both markets would
    /// report a hundred times the volume on every derivative bar, with nothing anywhere to say so.
    /// </para>
    /// </summary>
    private static Bar ToBar(JsonElement k, BarType barType, Instrument instrument, UnixNanos close) =>
        new(barType,
            instrument.MakePrice(k[1].DecValue()),
            instrument.MakePrice(k[2].DecValue()),
            instrument.MakePrice(k[3].DecValue()),
            instrument.MakePrice(k[4].DecValue()),
            instrument.MakeQuantity(BaseVolume(k, instrument)),
            close,
            close);

    /// <summary>The bar's volume in base currency, from whichever field of the row holds it on this market.</summary>
    private static decimal BaseVolume(JsonElement k, Instrument instrument) =>
        instrument.InstrumentClass == InstrumentClass.Spot || k.GetArrayLength() <= 6
            ? k[5].DecValue()
            : k[6].DecValue();

    /// <summary>
    /// The funding a perpetual was charged at each settlement in a window, oldest first - the order everything that
    /// stores history expects, and the order every other venue is read in. The venue answers newest first.
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        OkxHttp http,
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
    /// <inheritdoc cref="FetchFundingRatesAsync(OkxHttp, InstrumentId, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>
    /// <para>
    /// The rate taken is <c>realizedRate</c> where the venue publishes one, because that is what the account was
    /// actually charged; <c>fundingRate</c> beside it is what was predicted for the settlement. The two agree on
    /// every settlement measured, and a backtest priced on a prediction rather than a charge would be right until
    /// they disagreed.
    /// </para>
    /// <para>
    /// The venue keeps roughly three months of settlements: asked for 300 of BTC-USDT-SWAP's it answered 286, which
    /// is 95 days of eight-hourly funding. A window reaching further back is not an error and comes back short.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        OkxHttp http,
        InstrumentId instrumentId,
        long? startMs,
        long? endMs,
        int? limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        long from = startMs ?? 0;
        long cursor = (endMs ?? UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow).ToMilliseconds()) + 1;
        List<FundingRateUpdate> rates = [];

        while (cursor > from)
        {
            JsonElement data = await http.GetPublicAsync(
                FundingPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instId"] = OkxVenue.ToRawSymbol(instrumentId),
                    ["limit"] = OkxVenue.FundingPage.ToString(CultureInfo.InvariantCulture),

                    // Measured: `after` here means settlements OLDER than this timestamp, which is the opposite of
                    // what the word suggests and the same as it means on the candle endpoint.
                    ["after"] = cursor.ToString(CultureInfo.InvariantCulture),
                },
                ct).ConfigureAwait(false);

            int rows = 0;
            long oldest = long.MaxValue;
            foreach (JsonElement row in Items(data))
            {
                rows++;
                long at = row.Long("fundingTime");
                oldest = Math.Min(oldest, at);
                if (at < from)
                {
                    continue;
                }

                UnixNanos ts = UnixNanos.FromMilliseconds(at);
                decimal rate = row.Filled("realizedRate") ? row.Dec("realizedRate") : row.Dec("fundingRate");
                rates.Add(new FundingRateUpdate(instrumentId, rate, null, ts, ts));
            }

            // A limit bounds the WALK rather than trimming the answer, which is what the other venue that pages this
            // endpoint does: settlements are eight hours apart, so one page is two months of them and a caller
            // asking for a handful should not start a walk to the end of what the venue keeps.
            if (rows == 0
                || rows < OkxVenue.FundingPage
                || oldest == long.MaxValue
                || oldest <= from
                || oldest >= cursor
                || (limit is { } wanted && rates.Count >= wanted))
            {
                break;
            }

            cursor = oldest;
        }

        return [.. rates.DistinctBy(r => r.TsEvent).OrderBy(r => r.TsEvent.Value)];
    }

    private static IEnumerable<JsonElement> Items(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];
}
