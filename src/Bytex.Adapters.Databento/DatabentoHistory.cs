using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;

namespace Bytex.Adapters.Databento;

/// <summary>
/// Bars from this vendor for a caller that has no node: an instrument, a period, and a key.
///
/// <para>
/// Every venue adapter here offers this shape, because a host downloads history from a service rather than from a
/// running node. A data vendor that needed a node standing up to answer would be the one source in the catalogue
/// that did.
/// </para>
///
/// <para>
/// The two window rules are the shared ones rather than this adapter's own. They were written per adapter once and
/// drifted immediately: one copy answered "the last six months" with the last thousand bars, successfully, and two
/// others took the FIRST n of what they had collected - correct only because they collected exactly n. A vendor is
/// no more exempt from that than a venue.
/// </para>
/// </summary>
public static class DatabentoHistory
{
    /// <summary>
    /// Bars for one symbol over a period, at whichever aggregation this vendor publishes for that bar type.
    ///
    /// <para>
    /// The key is read from the environment when it is not given, so a host never has to hold one to ask for
    /// history. What comes back is bounded to bars that have CLOSED: a candle still forming is the one thing a
    /// stored bar must never be, because nothing downstream can tell it from a finished one.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Bar>> FetchBarsAsync(
        Instrument instrument,
        CandleSeries candleSeries,
        string symbol,
        string dataset,
        UnixNanos? start = null,
        UnixNanos? end = null,
        int? limit = null,
        string? apiKey = null,
        string baseUrl = "https://hist.databento.com",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);

        string schema = DatabentoDataClient.SchemaFor(candleSeries);
        string key = Secrets.Require(apiKey, DatabentoDataClient.EnvApiKey);
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        // This vendor answers a range rather than a page, so what it returns is the window asked for. The shared
        // rule is still asked what to take, because a caller giving a limit with no start means the NEWEST that many
        // and a caller giving a start means the ones that follow it - and getting that backwards is a request that
        // succeeds while answering a different question.
        int take = BarWindow.Wanted(limit, start, int.MaxValue);

        List<Bar> bars = new();
        await foreach (IReadOnlyDictionary<string, string> row in DatabentoDataClient
            .FetchRowsAsync(baseUrl, key, dataset, "raw_symbol", schema, symbol, start ?? new UnixNanos(0), end ?? now, ct)
            .ConfigureAwait(false))
        {
            bars.Add(DatabentoDataClient.BarFrom(row, instrument, candleSeries));
        }

        IReadOnlyList<Bar> closed = BarWindow.Closed(bars, candleSeries, start, end, now);
        return BarWindow.Capped(closed, start, take);
    }
}
