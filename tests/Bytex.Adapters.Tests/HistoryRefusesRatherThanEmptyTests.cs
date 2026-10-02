using System.Text.RegularExpressions;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Adapters.Tests;

// A historical request used to answer an unresolved instrument with an empty list - the same answer as "the venue
// holds no history for that window". A caller could not tell the two apart, and the one worth knowing about is the
// one that means the venue was never asked.
//
// It cost real time: a live chart drew nothing and a request returned nothing, and neither said the instrument was
// simply not loaded. Every request path already turns a thrown exception into a logged error AND an error response,
// so the machinery to say so was there and unused at twenty-three sites across eleven data clients.
//
// The first test is behaviour on a real client. The second is the guard that covers the other ten, because a fix
// applied by hand at twenty-three sites is a fix that comes back.
public sealed class HistoryRefusesRatherThanEmptyTests
{
    private static readonly CandleSeries _unknown = CandleSeries.Parse("bx-candle:v2/BINANCE/NOPEUSDT/minute/1/last/provider");
    private static readonly CandleSeries _known = CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider");

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Routes routes = new Routes()
                .On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo)
                .On("GET", "/api/v3/klines", """
                    [[1690000020000,"25000.00","25010.00","24990.00","25005.00","1.5",1690000079999,"37507.5",10,"0.7","17503.5","0"]]
                    """);
            Server = new LoopbackServer(routes.Handle);
            TradingRuntime = new TestTradingRuntime();
            Client = new BinanceDataClient(new ClientId("BINANCE"), new BinanceDataClientConfig { BaseUrlHttp = Server.HttpBase, BaseUrlWs = Server.WsBase }, TradingRuntime.Services);
            Client.AttachSink(Sink);
        }

        public LoopbackServer Server { get; }

        public TestTradingRuntime TradingRuntime { get; }

        public BinanceDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public async Task<DataResponse> RequestAsync(RequestCommand command)
        {
            await Client.Instruments.LoadAllAsync(CancellationToken.None);
            await Client.RequestAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);
            return await Sink.NextResponseAsync();
        }

        public async ValueTask DisposeAsync()
        {
            TradingRuntime.Dispose();
            await Server.DisposeAsync();
        }
    }

    /// <summary>
    /// <b>An instrument the client cannot resolve is refused, and the refusal names it.</b> Not an empty list: the
    /// venue was never asked, and a caller that cannot tell those apart draws an empty chart and calls it data.
    /// </summary>
    [Fact]
    public async Task A_request_for_an_unloaded_instrument_answers_with_an_error()
    {
        await using Rig rig = new();

        DataResponse response = await rig.RequestAsync(new RequestBars(_unknown, null, null, null, null, Guid.NewGuid(), default));

        Assert.True(response.IsError, "an unresolved instrument answered without an error");
        Assert.Contains("bx-market:v2/BINANCE/NOPEUSDT", response.Error!, StringComparison.Ordinal);
        Assert.Contains("not loaded", response.Error!, StringComparison.Ordinal);
        Assert.Empty(response.Data);
    }

    /// <summary>
    /// The control: an instrument the client does know still answers with data. Without this, refusing everything
    /// would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_request_for_a_loaded_instrument_still_answers_with_data()
    {
        await using Rig rig = new();

        DataResponse response = await rig.RequestAsync(new RequestBars(_known, null, null, null, null, Guid.NewGuid(), default));

        Assert.False(response.IsError, response.Error ?? string.Empty);
        Assert.NotEmpty(response.Data);
    }

    /// <summary>
    /// <b>And no data client anywhere goes back to answering empty.</b> Twenty-three sites across eleven clients
    /// had the same shape - a null-instrument check whose body was a bare <c>return []</c> - so this reads the
    /// source rather than trusting that each one stays fixed. A twelfth adapter written from an existing one as a
    /// template is the way this returns.
    /// </summary>
    [Fact]
    public void No_data_client_answers_an_unresolved_instrument_with_an_empty_list()
    {
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo.Root, "src"), "*DataClient.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("return [];", StringComparison.Ordinal))
                {
                    continue;
                }

                string window = string.Join('\n', lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(4, i + 1)));
                if (Regex.IsMatch(window, @"(is null|is not \{ \} instrument)", RegexOptions.None, TimeSpan.FromSeconds(1))
                    && window.Contains("nstrument", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these answer an unresolved instrument with an empty list instead of refusing: " + string.Join(", ", offenders));
    }
}
