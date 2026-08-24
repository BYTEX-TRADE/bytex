using System.Globalization;
using System.IO.Compression;
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

    /// <summary>Maps engine venues to Tardis exchange identifiers; defaults cover Binance and Bybit.</summary>
    public IReadOnlyDictionary<string, string> ExchangeMap { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["BINANCE"] = "binance",
        ["BINANCE-PERP"] = "binance-futures",
        ["BYBIT"] = "bybit-spot",
        ["BYBIT-PERP"] = "bybit",
    };

    /// <summary>Directory for downloaded files; null keeps them in memory only.</summary>
    public string? CacheDirectory { get; init; }
}

/// <summary>
/// Historical trades, quotes, and book deltas from the Tardis datasets API. Instruments must already be in the cache.
/// </summary>
public sealed class TardisDataClient : DataClientBase
{
    public const string EnvApiKey = "TARDIS_API_KEY";

    private readonly TardisDataClientConfig _config;
    private readonly HttpClientWrapper _http;
    private readonly string _apiKey;

    public TardisDataClient(ClientId clientId, TardisDataClientConfig config, KernelServices services)
        : base(clientId, null, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _apiKey = Secrets.Require(config.ApiKey, EnvApiKey);
        _http = new HttpClientWrapper(new Uri(config.BaseUrl.TrimEnd('/') + "/"), new RateLimiter(30, TimeSpan.FromSeconds(10)), new RetryPolicy(3), Log, TimeSpan.FromMinutes(5),
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + _apiKey });
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
                    SendResponse(rt, typeof(TradeTick), await LoadTradesAsync(rt.InstrumentId, rt.Start, rt.End, rt.Limit, ct).ConfigureAwait(false));
                    break;
                case RequestQuoteTicks rq:
                    SendResponse(rq, typeof(QuoteTick), await LoadQuotesAsync(rq.InstrumentId, rq.Start, rq.End, rq.Limit, ct).ConfigureAwait(false));
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

    public async Task<IReadOnlyList<IData>> LoadTradesAsync(InstrumentId instrumentId, UnixNanos? start, UnixNanos? end, int? limit, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(instrumentId);
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

    public async Task<IReadOnlyList<IData>> LoadQuotesAsync(InstrumentId instrumentId, UnixNanos? start, UnixNanos? end, int? limit, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(instrumentId);
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

    public async Task<IReadOnlyList<IData>> LoadBookDeltasAsync(InstrumentId instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        Instrument instrument = RequireInstrument(instrumentId);
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

    private Instrument RequireInstrument(InstrumentId id) =>
        Services.Cache.Instrument(id) ?? throw new InvalidOperationException($"Instrument {id} must be in the cache before requesting Tardis data.");

    private string ExchangeFor(Instrument instrument)
    {
        string venue = instrument.Venue.Value;
        bool perp = instrument.InstrumentClass is InstrumentClass.Swap or InstrumentClass.Future;
        string key = perp ? venue + "-PERP" : venue;
        if (_config.ExchangeMap.TryGetValue(key, out string? exchange) || _config.ExchangeMap.TryGetValue(venue, out exchange))
        {
            return exchange;
        }

        return venue.ToLowerInvariant();
    }

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

    private async Task<byte[]> DownloadBytesAsync(string path, CancellationToken ct)
    {
        using HttpClient client = new() { BaseAddress = _http.BaseUrl, Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        using HttpResponseMessage response = await client.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VenueHttpException(response.StatusCode, string.Empty, $"GET {path} returned {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed class TardisDataClientFactory : IDataClientFactory
{
    public string Name => "TARDIS";

    public Type ConfigType => typeof(TardisDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) =>
        new TardisDataClient(clientId, (TardisDataClientConfig)config, services);
}

public sealed class TardisPlugin : Core.Plugins.IPlugin
{
    public string Id => "bytex.tardis";

    public string Version => typeof(TardisPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry) => registry.AddDataClientFactory(new TardisDataClientFactory());
}
