using System.Globalization;
using System.IO.Compression;
using System.Text;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Tardis;

public sealed record TardisDataClientConfig : DataClientConfig
{
    public string? ApiKey { get; init; }

    public string BaseUrl { get; init; } = "https://datasets.tardis.dev/v1";

    /// <summary>
    /// What this vendor calls each of a venue's markets, keyed as <c>VENUE:CLASS</c> and, where a venue's
    /// coin-settled market is a dataset of its own, <c>VENUE:CLASS:INVERSE</c>.
    ///
    /// <para>
    /// Keyed per product family because that is how the vendor publishes: Binance is three datasets, not two -
    /// <c>binance</c>, <c>binance-futures</c> for the USD-margined market and <c>binance-delivery</c> for the
    /// coin-margined one - and Bybit's options are <c>bybit-options</c> while its spot is <c>bybit-spot</c> and the
    /// plain <c>bybit</c> is its derivatives. A key of venue-plus-is-it-a-perpetual sent options to the spot dataset
    /// and coin-margined contracts to the USD-margined one.
    /// </para>
    ///
    /// <para>
    /// Every id here was read from the vendor's own published exchange list, not inferred from the venue's name. Two
    /// of them could not have been guessed: OKX is <c>okex</c> and Gate is <c>gate-io</c>. A wrong id is answered
    /// with an empty result, which reads as the vendor holding no data for that period rather than as this adapter
    /// asking the wrong question - so guessing here is worse than refusing.
    /// </para>
    ///
    /// <para>
    /// Kraken's futures are absent because the vendor publishes no dataset for them, which is a fact about the
    /// vendor rather than a gap here. Anything this map does not name is refused by name.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> ExchangeMap { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["BINANCE:Spot"] = "binance",
        ["BINANCE:Swap"] = "binance-futures",
        ["BINANCE:Future"] = "binance-futures",
        ["BINANCE:Swap:INVERSE"] = "binance-delivery",
        ["BINANCE:Future:INVERSE"] = "binance-delivery",

        ["BYBIT:Spot"] = "bybit-spot",
        ["BYBIT:Swap"] = "bybit",
        ["BYBIT:Future"] = "bybit",
        ["BYBIT:Option"] = "bybit-options",

        ["OKX:Spot"] = "okex",
        ["OKX:Swap"] = "okex-swap",
        ["OKX:Future"] = "okex-futures",
        ["OKX:Option"] = "okex-options",

        ["KRAKEN:Spot"] = "kraken",

        ["KUCOIN:Spot"] = "kucoin",
        ["KUCOIN:Swap"] = "kucoin-futures",
        ["KUCOIN:Future"] = "kucoin-futures",

        ["GATE:Spot"] = "gate-io",
        ["GATE:Swap"] = "gate-io-futures",
        ["GATE:Future"] = "gate-io-futures",

        ["BITGET:Spot"] = "bitget",
        ["BITGET:Swap"] = "bitget-futures",

        ["HYPERLIQUID:Swap"] = "hyperliquid",
    };

    /// <summary>
    /// The dataset this vendor publishes the instrument's market as, or null where nobody has verified one.
    ///
    /// <para>
    /// Public, and on the config rather than on the client, because a HOST has the same question before it has a
    /// client: "can this vendor serve this market, and under what name". It was answered by hosts repeating the
    /// two-step lookup against <see cref="ExchangeMap"/> - two copies of one rule, and the copy is the one that goes
    /// stale when a venue is added.
    /// </para>
    ///
    /// <para>
    /// The order of the two lookups matters and is the reason this is not a single dictionary read: a coin-settled
    /// contract is asked for first, because a venue whose coin-margined market is a separate dataset has to be told
    /// apart from its USD-margined one, and <see cref="Instrument.IsInverse"/> is exactly that fact - settled in its
    /// own base currency. A venue that publishes one dataset for both has no inverse key and falls to the plain one.
    /// </para>
    /// </summary>
    /// <returns>The vendor's own name for the market, or null when this vendor is not set up for it.</returns>
    public string? DatasetFor(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        string venue = instrument.Venue.Value;
        string clazz = instrument.InstrumentClass.ToString();

        if (instrument.IsInverse && ExchangeMap.TryGetValue($"{venue}:{clazz}:INVERSE", out string? coin))
        {
            return coin;
        }

        return ExchangeMap.TryGetValue($"{venue}:{clazz}", out string? exchange) ? exchange : null;
    }

    /// <summary>Directory for downloaded files; null keeps them in memory only.</summary>
    public string? CacheDirectory { get; init; }
}

/// <summary>
/// Historical trades, quotes, and book deltas from the Tardis datasets API. Instruments must already be in the cache.
/// </summary>
public sealed class TardisDataClient : DataClientBase
{
    /// <summary>The dataset that carries twenty-five levels a side, and the only free one with real depth in it.</summary>
    public const string BookSnapshotDataType = "book_snapshot_25";

    /// <summary>Levels a side in <see cref="BookSnapshotDataType"/>, which is what the 25 in its name means.</summary>
    public const int BookLevels = 25;

    /// <summary>exchange, symbol, timestamp, local_timestamp - the four every row of every dataset starts with.</summary>
    private const int FixedColumns = 4;

    /// <summary>
    /// A level of a snapshot row is four columns - an ask price and amount, then a bid price and amount - so the
    /// levels of one side are four apart and not two. Measured from the vendor's header.
    /// </summary>
    private const int ColumnsPerLevel = 4;

    /// <summary>
    /// The first two bytes of a gzip member. MEASURED against the vendor rather than assumed: it serves these files
    /// as <c>text/csv</c> with no content encoding and a gzip body, so a check on the declared type would have
    /// refused every real download. What the bytes ARE does not depend on what the server calls them.
    /// </summary>
    private static readonly byte[] GzipMagic = [0x1f, 0x8b];

    public const string EnvApiKey = "TARDIS_API_KEY";

    /// <summary>Requests the provider allows in <see cref="RequestWindow"/>.</summary>
    public const int RequestsPerWindow = 30;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one download may take. A day of trades for a busy instrument is tens of megabytes, so the timeout is
    /// far longer than an ordinary request's.
    /// </summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private readonly TardisDataClientConfig _config;
    private readonly HttpClientWrapper _http;
    private readonly string? _apiKey;

    public TardisDataClient(ClientId clientId, TardisDataClientConfig config, TradingRuntimeServices services)
        : base(clientId, null, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // OPTIONAL, not required. This vendor publishes the first day of every month with no authentication at all,
        // and requiring a key here made those datasets unreachable through a client that needs nothing to read them -
        // which is the only real order-book depth obtainable without a subscription, since the free archives of the
        // venues themselves carry top-of-book or a band metric rather than levels. A key is still used when there is
        // one, and a paid dataset without one fails at the request with the vendor's own answer rather than here with
        // a message about configuration.
        _apiKey = Secrets.Optional(config.ApiKey, EnvApiKey);
        _http = new HttpClientWrapper(new Uri(config.BaseUrl.TrimEnd('/') + "/"), new RateLimiter(RequestsPerWindow, RequestWindow), new RetryPolicy(), Log, DownloadTimeout,
            _apiKey is null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + _apiKey });
    }

    protected override void OnDispose() => _http.Dispose();

    public override Task SubscribeAsync(SubscribeCommand command, CancellationToken ct)
    {
        Sink.OnSubscriptionFailed(ClientId, command, "Tardis provides historical data only");
        return Task.CompletedTask;
    }

    public override async Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        try
        {
            switch (command)
            {
                case RequestTradeTicks rt:
                    SendResponse(rt, typeof(TradeTick), await LoadTradesAsync(rt.MarketKey, rt.Start, rt.End, rt.Limit, ct).ConfigureAwait(false));
                    break;
                case RequestQuoteTicks rq:
                    SendResponse(rq, typeof(QuoteTick), await LoadQuotesAsync(rq.MarketKey, rq.Start, rq.End, rq.Limit, ct).ConfigureAwait(false));
                    break;
                default:
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Tardis client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Tardis request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    public async Task<IReadOnlyList<IData>> LoadTradesAsync(MarketKey marketKey, UnixNanos? start, UnixNanos? end, int? limit, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(marketKey);
        List<IData> result = new();
        await foreach (string[] row in RowsAsync(instrument, "trades", start, end, ct).ConfigureAwait(false))
        {
            // exchange,symbol,timestamp,local_timestamp,id,side,price,amount
            UnixNanos ts = UnixNanos.FromMicroseconds(long.Parse(row[2], CultureInfo.InvariantCulture));
            if (start is { } s && ts < s)
            {
                continue;
            }

            if (end is { } e && ts > e)
            {
                break;
            }

            UnixNanos local = UnixNanos.FromMicroseconds(long.Parse(row[3], CultureInfo.InvariantCulture));
            result.Add(new TradeTick(instrument.Id, instrument.MakePrice(Dec(row[6])), instrument.MakeQuantity(Dec(row[7])),
                row[5] == "buy" ? AggressorSide.Buyer : row[5] == "sell" ? AggressorSide.Seller : AggressorSide.None, new TradeId(row[4]), ts, local));
            if (limit is { } l && result.Count >= l)
            {
                break;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<IData>> LoadQuotesAsync(MarketKey marketKey, UnixNanos? start, UnixNanos? end, int? limit, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(marketKey);
        List<IData> result = new();
        await foreach (string[] row in RowsAsync(instrument, "quotes", start, end, ct).ConfigureAwait(false))
        {
            // exchange,symbol,timestamp,local_timestamp,ask_amount,ask_price,bid_price,bid_amount
            UnixNanos ts = UnixNanos.FromMicroseconds(long.Parse(row[2], CultureInfo.InvariantCulture));
            if (start is { } s && ts < s)
            {
                continue;
            }

            if (end is { } e && ts > e)
            {
                break;
            }

            UnixNanos local = UnixNanos.FromMicroseconds(long.Parse(row[3], CultureInfo.InvariantCulture));
            result.Add(new QuoteTick(instrument.Id, instrument.MakePrice(Dec(row[6])), instrument.MakePrice(Dec(row[5])), instrument.MakeQuantity(Dec(row[7])), instrument.MakeQuantity(Dec(row[4])), ts, local));
            if (limit is { } l && result.Count >= l)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Order-book depth from this vendor's <c>book_snapshot_25</c> dataset: one <see cref="OrderBookDepth"/> per
    /// row, twenty-five levels a side.
    ///
    /// <para>
    /// <b>Why snapshots rather than the deltas beside them.</b> <see cref="LoadBookDeltasAsync"/> reads
    /// <c>incremental_book_L2</c>, which is around seven hundred megabytes gzipped for one instrument-day - more
    /// than a run can hold at any setting. A day of snapshots is about ninety, so this is the cheapest form that
    /// proves what needs proving: that a fill binds to the levels BELOW the best bid and ask, rather than only to
    /// the size available at the top of the book.
    /// </para>
    ///
    /// <para>
    /// <b>The limit is not optional here.</b> A day is roughly 1.5 million rows of fifty levels, so a caller that
    /// cannot say how many it wants cannot use this at all - which is exactly why the deltas loader beside it, which
    /// takes none, is unusable. A row past the limit stops the read rather than being collected and dropped.
    /// </para>
    ///
    /// <para>
    /// A level whose price or amount is empty is the end of that side's ladder, not a zero: these files pad every
    /// row to the same width, so a book with eleven bids has fourteen empty ones after them. Reading them as zeros
    /// would put a bid at zero in the ladder and a fill would find it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<IData>> LoadBookSnapshotsAsync(MarketKey marketKey, UnixNanos? start, UnixNanos? end, int? limit, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(marketKey);
        List<IData> result = new();
        await foreach (string[] row in RowsAsync(instrument, BookSnapshotDataType, start, end, ct).ConfigureAwait(false))
        {
            // exchange,symbol,timestamp,local_timestamp, then FOUR columns per level, interleaved:
            // asks[L].price,asks[L].amount,bids[L].price,bids[L].amount - 104 columns for twenty-five levels.
            if (row.Length < FixedColumns + 4)
            {
                continue;
            }

            UnixNanos ts = UnixNanos.FromMicroseconds(long.Parse(row[2], CultureInfo.InvariantCulture));
            if (start is { } s && ts < s)
            {
                continue;
            }

            if (end is { } e && ts > e)
            {
                break;
            }

            UnixNanos local = UnixNanos.FromMicroseconds(long.Parse(row[3], CultureInfo.InvariantCulture));
            List<BookLevel> asks = Ladder(instrument, row, asks: true);
            List<BookLevel> bids = Ladder(instrument, row, asks: false);
            if (asks.Count == 0 && bids.Count == 0)
            {
                continue;
            }

            // A snapshot IS the book, not a change to it, so it carries the snapshot flag: anything applying it
            // replaces what it held rather than merging into it. The sequence is the row's position in the day,
            // because the file gives no sequence of its own and a consumer needs them to increase.
            result.Add(new OrderBookDepth(instrument.Id, bids, asks, RecordFlags.Snapshot, (ulong)result.Count + 1, ts, local));
            if (limit is { } l && result.Count >= l)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// One side of a snapshot row, stopping at the first level the file left empty.
    ///
    /// <para>
    /// <b>The columns are INTERLEAVED, four to a level</b> - <c>asks[L].price, asks[L].amount, bids[L].price,
    /// bids[L].amount</c> - so a side steps by four from its own start rather than running as a contiguous block.
    /// Measured against the vendor's own header, which is the only thing that settles it.
    /// </para>
    ///
    /// <para>
    /// This was written as a block - twenty-five asks and then twenty-five bids - and both ladders came out wrong
    /// while nothing threw, because price and amount stay adjacent under either reading. The ask ladder alternated
    /// asks with the bids beneath them, and the bid ladder began at the thirteenth bid and alternated with asks
    /// priced above it: two crossed, non-monotonic ladders of real prices, from the right instrument, at the right
    /// timestamp. A resting buy sized against that would have filled on the other side's size, which is worse than
    /// having no depth at all because it is silently plausible.
    /// </para>
    ///
    /// <para>
    /// The test fixture had the same assumption in it, so it round-tripped and proved only its own consistency.
    /// What caught it was the vendor's file, and that is what the fixture is now.
    /// </para>
    /// </summary>
    private static List<BookLevel> Ladder(Instrument instrument, string[] row, bool asks)
    {
        int side = FixedColumns + (asks ? 0 : 2);
        List<BookLevel> levels = new(BookLevels);
        for (int level = 0; level < BookLevels; level++)
        {
            int price = side + (level * ColumnsPerLevel);
            int amount = price + 1;
            if (amount >= row.Length || row[price].Length == 0 || row[amount].Length == 0)
            {
                break;
            }

            decimal size = Dec(row[amount]);
            if (size <= 0m)
            {
                break;
            }

            levels.Add(new BookLevel(instrument.MakePrice(Dec(row[price])), instrument.MakeQuantity(size)));
        }

        return levels;
    }

    public async Task<IReadOnlyList<IData>> LoadBookDeltasAsync(MarketKey marketKey, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(marketKey);
        List<IData> result = new();
        ulong sequence = 0;
        ulong orderId = 0;
        await foreach (string[] row in RowsAsync(instrument, "incremental_book_L2", start, end, ct).ConfigureAwait(false))
        {
            // exchange,symbol,timestamp,local_timestamp,is_snapshot,side,price,amount
            UnixNanos ts = UnixNanos.FromMicroseconds(long.Parse(row[2], CultureInfo.InvariantCulture));
            if (start is { } s && ts < s)
            {
                continue;
            }

            if (end is { } e && ts > e)
            {
                break;
            }

            UnixNanos local = UnixNanos.FromMicroseconds(long.Parse(row[3], CultureInfo.InvariantCulture));
            bool snapshot = row[4] == "true";
            decimal amount = Dec(row[7]);
            BookAction action = amount == 0m ? BookAction.Delete : snapshot ? BookAction.Add : BookAction.Update;
            result.Add(new OrderBookDelta(instrument.Id, action, new BookOrder(row[5] == "bid" ? OrderSide.Buy : OrderSide.Sell, instrument.MakePrice(Dec(row[6])), instrument.MakeQuantity(amount), ++orderId),
                snapshot ? RecordFlags.Snapshot : RecordFlags.None, ++sequence, ts, local));
        }

        return result;
    }

    private Instrument RequireInstrument(MarketKey id) =>
        Services.Cache.Instrument(id) ?? throw new InvalidOperationException($"Instrument {id} must be in the cache before requesting Tardis data.");

    /// <summary>
    /// What this vendor calls the market an instrument trades in, or a refusal naming what is served. The answer
    /// itself comes from <see cref="TardisDataClientConfig.DatasetFor"/>; this adds the refusal.
    /// </summary>
    private string ExchangeFor(Instrument instrument) =>
        _config.DatasetFor(instrument)

        // No guess. A lower-cased venue name is right for four of the eight venues this engine ships and wrong for
        // OKX and Gate, and being wrong here is answered with an empty result that reads as the vendor having no
        // data - so the mistake would look like the vendor's rather than ours.
        ?? throw new InvalidOperationException(
            $"This vendor is not set up for {instrument.Venue.Value}'s {instrument.InstrumentClass} market here. It serves: "
            + string.Join(", ", _config.ExchangeMap.Keys.Order(StringComparer.Ordinal))
            + ". A market absent from that list is one nobody has verified the vendor's own name for, and asking "
            + "under a guessed name is answered with silence rather than an error.");

    private async IAsyncEnumerable<string[]> RowsAsync(Instrument instrument, string dataType, UnixNanos? start, UnixNanos? end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string exchange = ExchangeFor(instrument);
        string symbol = instrument.RawSymbol.Value;
        DateOnly first = DateOnly.FromDateTime((start ?? Clock.Timestamp.Subtract(TimeSpan.FromDays(1))).ToDateTimeUtc());
        DateOnly last = DateOnly.FromDateTime((end ?? Clock.Timestamp).ToDateTimeUtc());
        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            string path = $"{exchange}/{dataType}/{day.Year}/{day.Month:00}/{day.Day:00}/{symbol}.csv.gz";
            byte[]? payload = await DownloadAsync(path, ct).ConfigureAwait(false);
            if (payload is null)
            {
                continue;
            }

            await using MemoryStream memory = new(payload);
            await using GZipStream gzip = new(memory, CompressionMode.Decompress);
            using StreamReader reader = new(gzip);
            string? header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (header is null)
            {
                continue;
            }

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                yield return line.Split(',');
            }
        }
    }

    private async Task<byte[]?> DownloadAsync(string path, CancellationToken ct)
    {
        string? cacheFile = _config.CacheDirectory is null ? null : Path.Combine(_config.CacheDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        if (cacheFile is not null && File.Exists(cacheFile))
        {
            return await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false);
        }

        try
        {
            byte[] bytes = await DownloadBytesAsync(path, ct).ConfigureAwait(false);
            if (cacheFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllBytesAsync(cacheFile, bytes, ct).ConfigureAwait(false);
            }

            return bytes;
        }
        catch (VenueHttpException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.LogDebug("Tardis dataset {Path} not found", path);
            return null;
        }
    }

    /// <summary>
    /// One gzipped dataset, through the same rate limiter and retry policy as everything else this client sends.
    ///
    /// <para>
    /// It used to build an HttpClient of its own per call, so the vendor's request budget was enforced for every
    /// other request and not for this one - the one that fetches ninety-megabyte files - and each call opened a
    /// socket of its own.
    /// </para>
    ///
    /// <para>
    /// And the body is checked, because a success status is not a promise about it: a request this vendor dislikes
    /// can answer with an HTML error page under HTTP 200, and that body reached the decompressor and died as an
    /// InvalidDataException naming neither the request nor the reason. It is checked by its first two bytes rather
    /// than by its declared type - measured, the vendor serves gzip as <c>text/csv</c>, so a type check would have
    /// refused every real download.
    /// </para>
    /// </summary>
    private async Task<byte[]> DownloadBytesAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await _http.GetBytesAsync(path, ct: ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e) when (_apiKey is null
            && e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // The helpful message that used to come from the constructor, moved to where the credential is actually
            // missing. Requiring a key up front made the free datasets unreachable; saying nothing when a paid one is
            // refused would leave somebody reading a bare 401. The dataset asked for decides which of those it is,
            // and only the vendor knows that.
            throw new InvalidOperationException(
                $"Tardis refused {path} with {(int)e.StatusCode} and this client has no API key. The first day of each "
                + $"month is free and needs none; every other day needs a subscription. Set the configuration value or "
                + $"the {EnvApiKey} environment variable.",
                e);
        }

        if (bytes.Length >= GzipMagic.Length && bytes[0] == GzipMagic[0] && bytes[1] == GzipMagic[1])
        {
            return bytes;
        }

        // The body of a wrong body is usually the explanation, so it goes in the message.
        string preview = LogText.Truncate(Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 300)), 300);
        throw new VenueHttpException(
            System.Net.HttpStatusCode.OK,
            preview,
            $"GET {path} answered 200 with a body that is not gzip - content type '{_http.ContentTypeOf ?? "none"}', "
            + $"{bytes.Length} bytes. This vendor answers a request it will not serve this way, a ranged one among them. It said: {preview}");
    }

    private static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed class TardisDataClientFactory : IDataClientFactory
{
    public string Name => "TARDIS";

    public Type ConfigType => typeof(TardisDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, TradingRuntimeServices services) =>
        new TardisDataClient(clientId, (TardisDataClientConfig)config, services);
}

public sealed class TardisPlugin : Core.Plugins.IPlugin
{
    public string Id => "bytex.tardis";

    public string Version => typeof(TardisPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry) => registry.AddDataClientFactory(new TardisDataClientFactory());
}
