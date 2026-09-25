using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// This venue's history read the way a running node sees it. The data client answers <c>RequestBars</c> with this and
/// anything storing history calls the same methods, so stored bars and live bars never disagree about an interval.
/// <para>
/// Both methods are public and static and take an HTTP client and nothing else, because a host that downloads history
/// has no node to ask.
/// </para>
/// </summary>
public static class HyperliquidHistory
{
    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first, and never the candle
    /// that is still forming.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(HyperliquidHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        FetchBarsAsync(http, instrument, barType, UnixNanos.FromDateTimeOffset(start), UnixNanos.FromDateTimeOffset(end), null, UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Closed bars, oldest first, the newest <paramref name="limit"/> of them when a limit is given.
    /// <para>
    /// Three things about this venue's candles are measured rather than assumed, and each one produces a wrong answer
    /// quietly rather than an error.
    /// </para>
    /// <para>
    /// A row carries BOTH ends of the interval: <c>t</c> is the open and <c>T</c> is the close, and the close is the
    /// last millisecond of the interval rather than the first of the next - 1790048940000 and 1790048999999 on a
    /// one-minute bar, 59999 apart. The engine stamps a bar at its close, so the stamp is computed from the open plus
    /// the interval rather than taken from <c>T</c>: taking <c>T</c> would put every bar one millisecond early and
    /// every comparison with another venue's bars off by one.
    /// </para>
    /// <para>
    /// The last row is the candle STILL FORMING. Measured at 17:46:47 UTC the newest one-minute row opened at
    /// 17:46:00 and had a close stamp in the future. <see cref="BarWindow.Closed"/> is what drops it.
    /// </para>
    /// <para>
    /// And the venue serves only about the last <see cref="HyperliquidVenue.CandleRetention"/> bars of an interval,
    /// counted from NOW and not from the window asked for. This is not a page size and no loop gets past it: a
    /// window entirely older comes back as an empty array. So this makes ONE request for the window it can be
    /// served and does not page - a paging loop would issue requests the venue answers identically and then look
    /// like it had reached the start of history. What is lost is said out loud rather than returned quietly short.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(HyperliquidHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(instrument);

        // A tick, volume or value bar has no interval, and asking one for its length throws from the core rather
        // than answering zero. Translated here so the caller is told which ARGUMENT was wrong - this venue keeps
        // time bars and nothing else - instead of being handed an invalid-operation from inside a property.
        long interval;
        try
        {
            interval = barType.Spec.IntervalNanos;
        }
        catch (InvalidOperationException e)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; Hyperliquid history has time bars only.", nameof(barType), e);
        }

        int take = BarWindow.Wanted(limit, start, HyperliquidVenue.CandleRetention);
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        long toMs = lastOpen / UnixNanos.NanosPerMillisecond;
        long intervalMs = interval / UnixNanos.NanosPerMillisecond;

        // The oldest moment the venue will answer for, whatever was asked. Asking further back is not an error and
        // not slower - the venue simply starts its answer at the boundary - but there is no point in a request for a
        // window entirely behind it.
        long earliestMs = Math.Max(0, (now.Value / UnixNanos.NanosPerMillisecond) - (HyperliquidVenue.CandleRetention * intervalMs));
        long wantedFromMs = start is { } s
            ? s.Value / UnixNanos.NanosPerMillisecond - intervalMs
            : toMs - ((Math.Min(take, HyperliquidVenue.CandleRetention) + 1L) * intervalMs);

        long fromMs = Math.Max(0, Math.Max(wantedFromMs, earliestMs));
        if (fromMs > toMs)
        {
            return [];
        }

        JsonElement data = await CandlesAsync(http, barType, fromMs, toMs, ct).ConfigureAwait(false);
        List<Bar> bars = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return bars;
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            long openMs = row.Long(CandleOpen);
            if (openMs <= 0)
            {
                continue;
            }

            UnixNanos close = new((openMs * UnixNanos.NanosPerMillisecond) + interval);
            bars.Add(new Bar(
                barType,
                instrument.MakePrice(row.Dec(CandleOpenPrice)),
                instrument.MakePrice(row.Dec(CandleHigh)),
                instrument.MakePrice(row.Dec(CandleLow)),
                instrument.MakePrice(row.Dec(CandleClose)),
                instrument.MakeQuantity(row.Dec(CandleVolume)),
                close,
                close));
        }

        IReadOnlyList<Bar> window = BarWindow.Closed(bars, barType, start, end, now);
        return BarWindow.Capped(window, start, take);
    }

    /// <summary>
    /// The funding a perpetual was charged at each settlement in a window, oldest first.
    /// <para>
    /// This history is complete, unlike the candles: a window three years back answered with real settlements from
    /// 2023-09-26, so nothing here is retained for a few thousand rows only. It pages FORWARD instead - asked for
    /// 2000 hours it returned the OLDEST 500 of them - which is the opposite of what the candle read does with the
    /// same kind of window, so the two cannot share a loop.
    /// </para>
    /// <para>
    /// Settlement is hourly here and eight-hourly on the venues beside it, which is a fact about the NUMBER and not
    /// only about the schedule: a rate off this venue is a charge for one hour.
    /// </para>
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        HyperliquidHttp http,
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

    /// <inheritdoc cref="FetchFundingRatesAsync(HyperliquidHttp, InstrumentId, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>
    public static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        HyperliquidHttp http,
        InstrumentId instrumentId,
        long? startMs,
        long? endMs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        long to = endMs ?? UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow).ToMilliseconds();
        long cursor = Math.Max(0, startMs ?? 0);
        List<FundingRateUpdate> rates = new();

        while (cursor <= to)
        {
            JsonElement data = await http.InfoAsync(
                HyperliquidReads.FundingHistory,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [HyperliquidReads.Coin] = HyperliquidVenue.ToCoin(instrumentId),
                    [HyperliquidReads.StartTime] = cursor,
                    [HyperliquidReads.EndTime] = to,
                },
                ct).ConfigureAwait(false);

            if (data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            int count = 0;
            long newest = long.MinValue;
            foreach (JsonElement row in data.EnumerateArray())
            {
                count++;
                long at = row.Long(FundingTime);
                newest = Math.Max(newest, at);
                if (at < cursor || at > to)
                {
                    continue;
                }

                UnixNanos ts = UnixNanos.FromMilliseconds(at);
                rates.Add(new FundingRateUpdate(instrumentId, row.Dec(FundingRate), null, ts, ts));
            }

            // A page that did not fill has reached the end of the window. A settlement's stamp is not exactly on the
            // hour - the venue writes it a few tens of milliseconds late, measured at .015 to .068 past - so the
            // cursor moves to one past the newest row rather than to a computed boundary, which would either skip a
            // settlement or fetch the same page for ever.
            if (count < HyperliquidVenue.FundingPage || newest == long.MinValue)
            {
                break;
            }

            cursor = newest + 1;
        }

        return [.. rates.DistinctBy(r => r.TsEvent).OrderBy(r => r.TsEvent.Value)];
    }

    /// <summary>The open of the interval, in milliseconds. The venue sends the close as <c>T</c> beside it.</summary>
    private const string CandleOpen = "t";

    private const string CandleOpenPrice = "o";
    private const string CandleHigh = "h";
    private const string CandleLow = "l";
    private const string CandleClose = "c";

    /// <summary>The volume in BASE units, which is what everything above the adapter counts in already.</summary>
    private const string CandleVolume = "v";

    private const string FundingTime = "time";
    private const string FundingRate = "fundingRate";

    private static Task<JsonElement> CandlesAsync(HyperliquidHttp http, BarType barType, long fromMs, long toMs, CancellationToken ct) =>
        http.InfoAsync(
            HyperliquidReads.CandleSnapshot,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // The only read on this venue that nests its arguments, which is why InfoAsync takes a group at all.
                [HyperliquidReads.Request] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [HyperliquidReads.Coin] = HyperliquidVenue.ToCoin(barType.InstrumentId),
                    [HyperliquidReads.Interval] = HyperliquidVenue.Interval(barType.Spec),
                    [HyperliquidReads.StartTime] = fromMs,
                    [HyperliquidReads.EndTime] = toMs,
                },
            },
            ct);
}
