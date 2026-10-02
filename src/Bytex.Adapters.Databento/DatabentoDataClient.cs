using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Databento;

/// <summary>
/// How to reach this vendor's historical service.
/// <para>
/// Every name here - the path, the parameters, the encodings - was read from the vendor's own client and format
/// library rather than inferred, because a wrong parameter is answered with an error about something else and a
/// wrong column is answered with a number that is not the number asked for.
/// </para>
/// </summary>
public sealed record DatabentoDataClientConfig : DataClientConfig
{
    /// <summary>Left null so the key is read from the environment and never travels through a host's configuration.</summary>
    public string? ApiKey { get; init; }

    public string BaseUrl { get; init; } = "https://hist.databento.com";

    /// <summary>
    /// Which of this vendor's datasets to ask - <c>GLBX.MDP3</c>, <c>XNAS.ITCH</c> and so on. There is no default,
    /// because there is no dataset a caller can be assumed to mean and asking the wrong one returns an error about
    /// entitlements rather than about the mistake.
    /// </summary>
    public string? Dataset { get; init; }

    /// <summary>How the symbols given are to be read; the vendor's default is its raw symbology.</summary>
    public string SymbolType { get; init; } = "raw_symbol";
}

/// <summary>
/// Historical trades, quotes and bars from Databento.
///
/// <para>
/// <b>What this asks for, and what it deliberately does not.</b> The vendor serves three encodings and its own
/// client always asks for DBN, its binary format, compressed with zstd. This adapter asks for CSV instead. That is
/// not a preference about speed: a binary layout is a thing this repository would have to infer field by field, and
/// a decoder that is subtly wrong reads a plausible price out of the wrong bytes rather than failing. CSV is a
/// format whose columns the vendor names, and every column this reads is asserted against those names.
/// </para>
///
/// <para>
/// <b>Prices arrive as integers.</b> This vendor's fixed-point representation is 1e-9 per unit, so a price of one
/// dollar is 1,000,000,000. Reading those as decimals directly would be wrong by nine orders of magnitude, in the
/// direction that looks like a very cheap instrument rather than like an error.
/// </para>
/// </summary>
public sealed class DatabentoDataClient : DataClientBase
{
    public const string EnvApiKey = "DATABENTO_API_KEY";

    /// <summary>Every unit of this vendor's prices is a billionth, which nothing about the number itself says.</summary>
    public const decimal PriceScale = 1_000_000_000m;

    private readonly DatabentoDataClientConfig _config;
    private readonly string _apiKey;

    public DatabentoDataClient(ClientId clientId, DatabentoDataClientConfig config, TradingRuntimeServices services)
        : base(clientId, new Venue("DATABENTO"), services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _apiKey = Secrets.Require(config.ApiKey, EnvApiKey);
    }

    public override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public override Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Trades for one symbol over a period.
    /// <para>
    /// The vendor's <c>trades</c> schema, whose rows carry <c>ts_recv</c>, <c>ts_event</c>, <c>price</c>,
    /// <c>size</c>, <c>side</c> and <c>sequence</c> among others. A side this vendor could not determine is written
    /// as <c>N</c>, and is carried through as none rather than guessed into a buy.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<TradeTick>> LoadTradesAsync(MarketKey marketKey, string symbol, UnixNanos start, UnixNanos end, CancellationToken ct = default)
    {
        Instrument instrument = RequireInstrument(marketKey);
        List<TradeTick> trades = new();

        await foreach (IReadOnlyDictionary<string, string> row in RowsAsync("trades", symbol, start, end, ct).ConfigureAwait(false))
        {
            UnixNanos ts = Nanos(row, "ts_recv");
            trades.Add(new TradeTick(
                instrument.Id,
                instrument.MakePrice(Price(row, "price")),
                instrument.MakeQuantity(Dec(Field(row, "size"))),
                Side(row.TryGetValue("side", out string? side) ? side : null),
                new TradeId(row.TryGetValue("sequence", out string? sequence) && sequence.Length > 0 ? sequence : ts.Value.ToString(CultureInfo.InvariantCulture)),
                Nanos(row, "ts_event"),
                ts));
        }

        return trades;
    }

    /// <summary>
    /// Top-of-book quotes, from the vendor's <c>mbp-1</c> schema.
    /// <para>
    /// Its one level of depth is flattened into columns suffixed with the level's index - <c>bid_px_00</c>,
    /// <c>ask_sz_00</c> - which is the vendor's own convention and not a shape invented here.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<QuoteTick>> LoadQuotesAsync(MarketKey marketKey, string symbol, UnixNanos start, UnixNanos end, CancellationToken ct = default)
    {
        Instrument instrument = RequireInstrument(marketKey);
        List<QuoteTick> quotes = new();

        await foreach (IReadOnlyDictionary<string, string> row in RowsAsync("mbp-1", symbol, start, end, ct).ConfigureAwait(false))
        {
            quotes.Add(new QuoteTick(
                instrument.Id,
                instrument.MakePrice(Price(row, "bid_px_00")),
                instrument.MakePrice(Price(row, "ask_px_00")),
                instrument.MakeQuantity(Dec(Field(row, "bid_sz_00"))),
                instrument.MakeQuantity(Dec(Field(row, "ask_sz_00"))),
                Nanos(row, "ts_event"),
                Nanos(row, "ts_recv")));
        }

        return quotes;
    }

    /// <summary>
    /// Bars, from whichever <c>ohlcv-</c> schema the bar type asks for.
    /// <para>
    /// The vendor publishes one-second, one-minute, hourly and daily aggregations and nothing else, so a bar type it
    /// does not serve is refused by name rather than approximated from a finer one - an aggregation this adapter
    /// performed itself would be a bar the vendor never published, carrying its name.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Bar>> LoadBarsAsync(CandleSeries candleSeries, string symbol, UnixNanos start, UnixNanos end, CancellationToken ct = default)
    {
        Instrument instrument = RequireInstrument(candleSeries.MarketKey);
        string schema = SchemaFor(candleSeries);
        List<Bar> bars = new();

        await foreach (IReadOnlyDictionary<string, string> row in RowsAsync(schema, symbol, start, end, ct).ConfigureAwait(false))
        {
            bars.Add(BarFrom(row, instrument, candleSeries));
        }

        return bars;
    }

    /// <summary>One of this vendor's aggregation rows as a bar, in one place so both ways of asking agree.</summary>
    internal static Bar BarFrom(IReadOnlyDictionary<string, string> row, Instrument instrument, CandleSeries candleSeries)
    {
        UnixNanos ts = Nanos(row, "ts_event");
        return new Bar(
            candleSeries,
            instrument.MakePrice(Price(row, "open")),
            instrument.MakePrice(Price(row, "high")),
            instrument.MakePrice(Price(row, "low")),
            instrument.MakePrice(Price(row, "close")),
            instrument.MakeQuantity(Dec(Field(row, "volume"))),
            ts,
            ts);
    }

    /// <summary>
    /// The vendor's aggregation for a bar type, or a refusal naming what it publishes.
    /// <para>
    /// Only the four it actually serves. A five-minute bar is not a smaller ask than a one-minute one: this vendor
    /// does not publish it, and building it here would hand back a bar of this adapter's making under the vendor's
    /// name.
    /// </para>
    /// </summary>
    public static string SchemaFor(CandleSeries candleSeries)
    {
        return (candleSeries.Spec.Aggregation, candleSeries.Spec.Step) switch
        {
            (SamplingMethod.Second, 1) => "ohlcv-1s",
            (SamplingMethod.Minute, 1) => "ohlcv-1m",
            (SamplingMethod.Hour, 1) => "ohlcv-1h",
            (SamplingMethod.Day, 1) => "ohlcv-1d",
            _ => throw new InvalidOperationException(
                $"This vendor publishes one-second, one-minute, hourly and daily bars, and {candleSeries.Spec} is none of them. "
                + "Aggregating a finer one here would return a bar this vendor never published under its own name."),
        };
    }

    /// <summary>
    /// One request to the vendor, as rows keyed by its own column names.
    /// <para>
    /// A POST with form parameters, and the key sent as the user of HTTP basic authentication with no password,
    /// which is how this vendor's own client authenticates.
    /// </para>
    /// </summary>
    private IAsyncEnumerable<IReadOnlyDictionary<string, string>> RowsAsync(string schema, string symbol, UnixNanos start, UnixNanos end, CancellationToken ct) =>
        FetchRowsAsync(_config.BaseUrl, _apiKey, _config.Dataset, _config.SymbolType, schema, symbol, start, end, ct);

    /// <summary>
    /// One request to the vendor, as rows keyed by its own column names, with nothing of a node about it.
    /// <para>
    /// Static because the helper below has to reach the vendor without a tradingRuntime, and shared with the client so that
    /// the two cannot drift into reading the same vendor two different ways.
    /// </para>
    /// </summary>
    internal static async IAsyncEnumerable<IReadOnlyDictionary<string, string>> FetchRowsAsync(string baseUrl, string apiKey, string? dataset, string symbolType, string schema, string symbol, UnixNanos start, UnixNanos end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dataset))
        {
            throw new InvalidOperationException("This vendor serves many datasets and assumes none, so one has to be named: GLBX.MDP3, XNAS.ITCH and so on.");
        }

        using HttpClient client = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(apiKey + ":")));

        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dataset"] = dataset,
            ["symbols"] = symbol,
            ["schema"] = schema,
            ["start"] = start.Value.ToString(CultureInfo.InvariantCulture),
            ["end"] = end.Value.ToString(CultureInfo.InvariantCulture),
            ["stype_in"] = symbolType,
            ["encoding"] = "csv",
            ["compression"] = "none",
        });

        using HttpResponseMessage response = await client.PostAsync("/v0/timeseries.get_range", form, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The vendor's own message, because its errors say which entitlement or dataset is the problem and
            // nothing this adapter could write would be more use than that.
            throw new VenueHttpException(response.StatusCode, string.Empty,
                $"Databento answered {(int)response.StatusCode} for {schema} on {dataset}: {Trim(body)}");
        }

        string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            yield break;
        }

        string[] header = [.. lines[0].Trim().Split(',')];
        foreach (string line in lines.Skip(1))
        {
            string[] values = line.Trim().Split(',');
            Dictionary<string, string> row = new(StringComparer.Ordinal);
            for (int i = 0; i < header.Length && i < values.Length; i++)
            {
                row[header[i]] = values[i];
            }

            yield return row;
            ct.ThrowIfCancellationRequested();
        }
    }

    private Instrument RequireInstrument(MarketKey id) =>
        Services.Cache.Instrument(id) ?? throw new InvalidOperationException($"Instrument {id} must be in the cache before requesting Databento data.");

    /// <summary>
    /// A column this vendor names, or a refusal that says which one is missing.
    /// <para>
    /// Missing columns are refused rather than defaulted: a zero price or an empty size reads as data, and the whole
    /// reason for asking in a named format is that a column either is there or the request was not what was meant.
    /// </para>
    /// </summary>
    private static string Field(IReadOnlyDictionary<string, string> row, string column) =>
        row.TryGetValue(column, out string? value) && value.Length > 0
            ? value
            : throw new FormatException($"This vendor's answer has no '{column}' column; it carries: {string.Join(", ", row.Keys)}");

    /// <summary>A price, out of the fixed-point integer this vendor writes: every unit is a billionth.</summary>
    private static decimal Price(IReadOnlyDictionary<string, string> row, string column) =>
        Dec(Field(row, column)) / PriceScale;

    private static UnixNanos Nanos(IReadOnlyDictionary<string, string> row, string column) =>
        new(long.Parse(Field(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture));

    private static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// The aggressor, by this vendor's own letters. <c>N</c> is what it writes when it does not know, and that is
    /// carried through rather than resolved into a side nobody reported.
    /// </summary>
    private static AggressorSide Side(string? value) => value switch
    {
        "B" => AggressorSide.Buyer,
        "A" or "S" => AggressorSide.Seller,
        _ => AggressorSide.None,
    };

    private static string Trim(string body) => body.Length <= 400 ? body : body[..400] + "…";
}

public sealed class DatabentoDataClientFactory : IDataClientFactory
{
    public string Name => "DATABENTO";

    public Type ConfigType => typeof(DatabentoDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, TradingRuntimeServices services) =>
        new DatabentoDataClient(clientId, (DatabentoDataClientConfig)config, services);
}

public sealed class DatabentoPlugin : Core.Plugins.IPlugin
{
    public string Id => "bytex.databento";

    public string Version => typeof(DatabentoPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry) => registry.AddDataClientFactory(new DatabentoDataClientFactory());
}
