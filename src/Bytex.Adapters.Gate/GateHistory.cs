using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Gate;

/// <summary>
/// Gate's candle and funding history, read the way a running node sees it. The data clients answer
/// <c>RequestBars</c> and <c>RequestFundingRates</c> with this, and anything that stores history - a catalog
/// download, which has no node at all - calls the same methods, so stored bars and live bars never disagree about
/// what an interval looked like.
/// <para>
/// Everything here is public and static on purpose: a host that stores history has no node and no client to borrow a
/// private method from, and a private one on the data client is how paging comes to be written twice.
/// </para>
/// </summary>
public static class GateHistory
{
    /// <summary>
    /// Closed bars between <paramref name="start"/> and <paramref name="end"/>, oldest first: every bar that closes
    /// at or after <paramref name="start"/> and opens at or before <paramref name="end"/>, never the candle that is
    /// still forming.
    /// </summary>
    public static Task<IReadOnlyList<Bar>> FetchBarsAsync(GateHttp http, Instrument instrument, BarType barType, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
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
    /// Three things about this venue's candles are handled here and are invisible above it, and all three are silent
    /// when got wrong rather than loud.
    /// </para>
    /// <para>
    /// The timestamps are in SECONDS - the row's own stamp, and the <c>from</c> and <c>to</c> the request carries.
    /// Sending milliseconds is refused outright with a range error, which is the one mercy here; READING a stamp as
    /// milliseconds is not refused by anything and places a 2026 bar in 1970.
    /// </para>
    /// <para>
    /// A stamp is the candle's OPEN, and a bar in this engine is stamped at its CLOSE, so one interval is added to
    /// every row. The last row the venue returns is the candle still forming, which <see cref="BarWindow.Closed"/>
    /// removes.
    /// </para>
    /// <para>
    /// The row's SHAPE differs between the venue's own markets. Spot answers an array of strings ordered
    /// [open time, quote volume, CLOSE, HIGH, LOW, OPEN, base volume, window closed]; the two derivative markets
    /// answer objects with named fields, sizes counted in contracts, and no window-closed flag at all. Reading one
    /// with the other's layout would give four real prices in the wrong slots with no error anywhere.
    /// </para>
    /// <para>
    /// The venue does write a candle for an interval nothing traded in - a flat one at the previous close with zero
    /// volume - which is measured rather than assumed: BVOL_USDT answered 61 consecutive minutes with every row at
    /// zero volume and no gap between any two. So nothing is filled in here, unlike on KuCoin.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(GateHttp http, Instrument instrument, BarType barType, UnixNanos? start, UnixNanos? end, int? limit, UnixNanos now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(instrument);

        long interval = barType.Spec.IntervalNanos;
        if (interval <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated; Gate history has time bars only.", nameof(barType));
        }

        bool spot = http.ProductType == GateProductType.Spot;
        int page = spot ? GateVenue.SpotCandlePage : GateFuturesVenue.CandlePage;
        int span = spot ? GateVenue.SpotCandleSpan : GateFuturesVenue.CandleSpan;
        int take = BarWindow.Wanted(limit, start, page);

        long intervalSeconds = interval / UnixNanos.NanosPerSecond;
        long lastOpen = Math.Min((end ?? now).Value, now.Value);
        long lastOpenSeconds = lastOpen / UnixNanos.NanosPerSecond;

        // With a start the window is given. Without one the caller wants the newest `take`, so the walk begins that
        // many intervals before the end; a limit large enough to overflow the multiplication means "as much as there
        // is", and walking back to the epoch answers that without arithmetic nobody can check.
        long back = take > int.MaxValue / 2 ? lastOpenSeconds : take * intervalSeconds;
        long cursor = start is { } from
            ? Math.Max(0, (from.Value / UnixNanos.NanosPerSecond) - intervalSeconds)
            : Math.Max(0, lastOpenSeconds - back);

        List<Bar> bars = new();
        while (cursor <= lastOpenSeconds)
        {
            long windowEnd = Math.Min(lastOpenSeconds, cursor + (span * intervalSeconds));
            JsonElement data = await CandlesAsync(http, barType, cursor, windowEnd, ct).ConfigureAwait(false);
            int count = 0;
            foreach (JsonElement row in data.EnumerateArray())
            {
                count++;
                long openSeconds = spot ? row[0].LongValue() : row.Long("t");
                UnixNanos close = new((openSeconds * UnixNanos.NanosPerSecond) + interval);
                bars.Add(spot ? SpotBar(row, barType, instrument, close) : DerivativeBar(row, barType, instrument, close));
            }

            // Nothing in this window means the venue holds nothing before it either: it writes a flat candle for
            // every interval a listed contract has existed through, so an empty answer is the start of its history.
            if (count == 0)
            {
                break;
            }

            cursor = windowEnd + intervalSeconds;
        }

        IReadOnlyList<Bar> window = BarWindow.Closed(bars, barType, start, end, now);
        return BarWindow.Capped(window, start, take);
    }

    /// <summary>
    /// One window of candles. The window is bounded here rather than at the venue because the two derivative
    /// markets disagree about what a too-wide one deserves: delivery refuses it and perpetual futures answers with
    /// the newest rows of it and says nothing about the ones it dropped.
    /// </summary>
    private static Task<JsonElement> CandlesAsync(GateHttp http, BarType barType, long fromSeconds, long toSeconds, CancellationToken ct)
    {
        string raw = GateVenue.ToRawSymbol(barType.InstrumentId);
        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            [http.ProductType == GateProductType.Spot ? "currency_pair" : "contract"] = raw,
            ["interval"] = GateVenue.Interval(barType.Spec),
            ["from"] = fromSeconds.ToString(CultureInfo.InvariantCulture),
            ["to"] = toSeconds.ToString(CultureInfo.InvariantCulture),
        };

        string path = http.ProductType == GateProductType.Spot
            ? "/spot/candlesticks"
            : GateFuturesVenue.Prefix(http.ProductType) + "/candlesticks";

        return http.GetPublicAsync(path, query, ct);
    }

    /// <summary>
    /// A spot row as a bar. The order is [open time, quote volume, close, high, low, open, base volume, window
    /// closed] - close before high, and the OPEN last, which is the reverse of where every other venue in this
    /// repository puts it. The volume taken is the base one, so a bar off this venue measures the same thing as a bar
    /// off any other.
    /// </summary>
    private static Bar SpotBar(JsonElement row, BarType barType, Instrument instrument, UnixNanos close) =>
        new(barType,
            instrument.MakePrice(row[5].DecValue()),
            instrument.MakePrice(row[3].DecValue()),
            instrument.MakePrice(row[4].DecValue()),
            instrument.MakePrice(row[2].DecValue()),
            instrument.MakeQuantity(row[6].DecValue()),
            close,
            close);

    /// <summary>
    /// A derivative row as a bar. The fields are named, so nothing depends on their order; the volume the venue
    /// reports is a number of CONTRACTS and becomes base currency here.
    /// </summary>
    private static Bar DerivativeBar(JsonElement row, BarType barType, Instrument instrument, UnixNanos close) =>
        new(barType,
            instrument.MakePrice(row.Dec("o")),
            instrument.MakePrice(row.Dec("h")),
            instrument.MakePrice(row.Dec("l")),
            instrument.MakePrice(row.Dec("c")),
            GateFuturesVenue.ToQuantity(instrument, row.Dec("v")),
            close,
            close);

    /// <summary>
    /// The funding a perpetual was charged at each settlement in a window, oldest first - which is the order
    /// everything that stores history expects and the order every other venue is read in, while the venue answers
    /// newest first.
    /// </summary>
    public static Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        GateHttp http,
        InstrumentId instrumentId,
        DateTimeOffset start,
        DateTimeOffset? end = null,
        CancellationToken ct = default) =>
        FetchFundingRatesAsync(
            http,
            instrumentId,
            UnixNanos.FromDateTimeOffset(start),
            end is { } e ? UnixNanos.FromDateTimeOffset(e) : null,
            UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ct);

    /// <summary>
    /// The funding settlements between <paramref name="start"/> and <paramref name="end"/>, oldest first.
    /// <para>
    /// The paging here is not the paging the endpoint looks like it has. Its <c>limit</c> accepts up to 1000 and is
    /// not what bounds an answer: one request returns the thirty days that FOLLOW <c>from</c>, whatever the limit
    /// says. Measured on three contracts with three different funding intervals - eight-hourly BTC_USDT answered 90
    /// rows, four-hourly ACT_USDT 180 and hourly CXMT_USDT 720 - which is thirty days in every case and nowhere near
    /// the thousand asked for. A loop that stopped when it received fewer rows than it asked for would read one
    /// month of a year and report it as the whole history, so the window is walked forward instead.
    /// </para>
    /// <para>
    /// The venue keeps 180 days. A <c>from</c> older than that is refused with "from time exceeds 180-day limit", so
    /// a request for a longer period is refused here, by the same rule and with a sentence that names it, rather
    /// than sent to be refused there.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<FundingRateUpdate>> FetchFundingRatesAsync(
        GateHttp http,
        InstrumentId instrumentId,
        UnixNanos? start,
        UnixNanos? end,
        UnixNanos now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.ProductType != GateProductType.Futures)
        {
            throw new ArgumentException(
                "Only Gate's perpetual contracts are charged funding. Its dated contracts settle at expiry instead, "
                + "and its spot pairs are not margined at all - neither market publishes a funding history.",
                nameof(http));
        }

        long windowSeconds = (long)GateVenue.FundingWindow.TotalSeconds;
        long earliest = (now.Value / UnixNanos.NanosPerSecond) - (long)GateVenue.FundingHistoryReach.TotalSeconds;
        long last = Math.Min((end ?? now).Value, now.Value) / UnixNanos.NanosPerSecond;
        long first = start is { } from ? from.Value / UnixNanos.NanosPerSecond : Math.Max(earliest, last - windowSeconds);

        if (first < earliest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                start,
                $"Gate keeps {GateVenue.FundingHistoryReach.TotalDays:0} days of funding history for a contract and "
                + "refuses a request that reaches further back. Ask for a window inside that, or read the rest from "
                + "a stored catalog.");
        }

        List<FundingRateUpdate> rates = new();
        long cursor = first;
        while (cursor <= last)
        {
            Dictionary<string, string> query = new(StringComparer.Ordinal)
            {
                ["contract"] = GateFuturesVenue.ToRawSymbol(instrumentId),
                ["from"] = cursor.ToString(CultureInfo.InvariantCulture),
                ["limit"] = GateVenue.FundingPage.ToString(CultureInfo.InvariantCulture),
            };

            JsonElement data = await http
                .GetPublicAsync(GateFuturesVenue.FuturesPrefix + "/funding_rate", query, ct)
                .ConfigureAwait(false);

            if (data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (JsonElement row in data.EnumerateArray())
            {
                // Seconds, as everywhere on this venue, and the venue stamps some settlements one second past the
                // hour - so the stamp is carried as it is rather than snapped to an interval it might not sit on.
                UnixNanos at = row.Seconds("t");
                if (at.Value > last * UnixNanos.NanosPerSecond)
                {
                    continue;
                }

                rates.Add(new FundingRateUpdate(instrumentId, row.Dec("r"), null, at, at));
            }

            cursor += windowSeconds;
        }

        return [.. rates.DistinctBy(r => r.TsEvent).OrderBy(r => r.TsEvent.Value)];
    }
}
