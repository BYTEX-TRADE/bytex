using System.Globalization;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests.Okx;

// Why: loading this venue's swap family took about fifty seconds, and none of it was the rate limit. The tier endpoint
// answers for at most five instrument families at a time - six are refused outright with code 50025 - so a family of
// 477 contracts costs about 96 requests however they are sent, and they were sent one after another, each waiting for
// the last. That is the round trip ninety-six times over. The venue's own published budget is twenty requests in two
// seconds, which would carry ten of them at once.
//
// Bitget's provider has fanned these out since it shipped, for a tier endpoint that answers for ONE contract at a time
// and is therefore worse off. This one simply waited. What is pinned here is the overlap itself rather than a
// stopwatch: a timing assertion measures the machine it runs on, while the number of requests in flight at once is the
// thing that was wrong.
public sealed class OkxTierConcurrencyTests
{
    private const string InstrumentsPath = "/api/v5/public/instruments";
    private const string TiersPath = "/api/v5/public/position-tiers";

    /// <summary>
    /// Enough families to need several pages: the recorded fixture holds three, which is one page and cannot show
    /// anything about how pages are sent. Each row is the venue's own recorded BTC-USDT swap with its family, id and
    /// underlying changed, so the shape stays the venue's and only the count is the test's.
    /// </summary>
    private static string ManyFamilies(int count)
    {
        string template = OkxPayloads.SwapInstruments;
        int open = template.IndexOf('[', StringComparison.Ordinal);
        int close = template.LastIndexOf(']');
        string first = template[(open + 1)..close].Split("},")[0].TrimStart() + "}";

        IEnumerable<string> rows = Enumerable.Range(0, count).Select(i =>
        {
            string family = string.Create(CultureInfo.InvariantCulture, $"A{i:D3}-USDT");
            return first
                .Replace("\"instFamily\":\"BTC-USDT\"", $"\"instFamily\":\"{family}\"", StringComparison.Ordinal)
                .Replace("\"instId\":\"BTC-USDT-SWAP\"", $"\"instId\":\"{family}-SWAP\"", StringComparison.Ordinal)
                .Replace("\"uly\":\"BTC-USDT\"", $"\"uly\":\"{family}\"", StringComparison.Ordinal)
                .Replace("\"ctValCcy\":\"BTC\"", $"\"ctValCcy\":\"A{i:D3}\"", StringComparison.Ordinal);
        });

        return OkxPayloads.Envelope("[" + string.Join(",", rows) + "]");
    }

    /// <summary>Tier one for whichever families the request names, so every page is answered in full.</summary>
    private static string TiersFor(string? families)
    {
        IEnumerable<string> rows = (families ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => $"{{\"imr\":\"0.01\",\"instFamily\":\"{f}\",\"instId\":\"\",\"maxLever\":\"100\",\"maxSz\":\"1000\",\"minSz\":\"0\",\"mmr\":\"0.004\",\"tier\":\"1\",\"uly\":\"{f}\"}}");

        return OkxPayloads.Envelope("[" + string.Join(",", rows) + "]");
    }

    /// <summary>
    /// Counts what is in flight, and makes each request WAIT for the thing the test is looking for rather than for a
    /// number of milliseconds: a handler holds its answer until a second request has arrived, with a deadline.
    ///
    /// <para>
    /// That is a stronger assertion than a sleep and a faster one. Sent several at a time, the second arrival releases
    /// both immediately. Sent one after another, the first waits out the deadline, gives up - so the run is one
    /// deadline rather than one per page - and the maximum in flight stays at one, which is the failure.
    /// </para>
    /// </summary>
    private sealed class Overlap
    {
        private readonly ManualResetEventSlim _overlapped = new(false);
        private int _inFlight;
        private bool _gaveUp;

        public int Max { get; private set; }

        public int Requests { get; private set; }

        public IDisposable Enter()
        {
            bool wait;
            lock (this)
            {
                _inFlight++;
                Requests++;
                Max = Math.Max(Max, _inFlight);
                if (_inFlight > 1)
                {
                    _overlapped.Set();
                }

                wait = !_overlapped.IsSet && !_gaveUp;
            }

            if (wait && !_overlapped.Wait(Wait.Timeout))
            {
                lock (this)
                {
                    _gaveUp = true;
                }
            }

            return new Exit(this);
        }

        private void Leave()
        {
            lock (this)
            {
                _inFlight--;
            }
        }

        private sealed class Exit(Overlap overlap) : IDisposable
        {
            public void Dispose() => overlap.Leave();
        }
    }

    /// <summary>
    /// The tier requests overlap, and by no more than the bound. Forty families is eight pages of five, and each
    /// answer is held until a second request has arrived - so an overlap cannot happen by accident, and a loop that
    /// waited for each answer in turn could never produce one.
    /// </summary>
    [Fact]
    public async Task Tier_requests_are_made_several_at_a_time_rather_than_one_after_another()
    {
        Overlap overlap = new();
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => StubResponse.Json(ManyFamilies(40)))
            .On("GET", TiersPath, request =>
            {
                using (overlap.Enter())
                {
                    return StubResponse.Json(TiersFor(request.Query("instFamily")));
                }
            })
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(8, overlap.Requests);
        Assert.True(overlap.Max > 1, "the tier requests were made one after another, which is the defect");
        Assert.True(
            overlap.Max <= OkxVenue.TierRequestConcurrency,
            $"{overlap.Max} requests were in flight at once, past the bound of {OkxVenue.TierRequestConcurrency}");
    }

    /// <summary>
    /// <b>The venue saying "slow down" is waited out, not thrown.</b> This venue limits per ENDPOINT and says so with
    /// code 50011 in a 200 body, which no transport reads as a rate limit - so the fan-out above, which stays inside
    /// this adapter's own declared budget, was refused by the tier endpoint on its first live run and lost the whole
    /// instrument list over it.
    ///
    /// <para>
    /// Repeating is safe for exactly this answer and no other: a rate refusal is a request that did not happen, so
    /// there is nothing to undo and nothing can be done twice. The stub refuses the first attempt on every page and
    /// answers the second, which is the shape the venue produced.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rate_refusal_is_waited_out_and_the_request_repeated()
    {
        Dictionary<string, int> attempts = new(StringComparer.Ordinal);
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => StubResponse.Json(ManyFamilies(10)))
            .On("GET", TiersPath, request =>
            {
                string page = request.Query("instFamily") ?? string.Empty;
                int seen;
                lock (attempts)
                {
                    attempts.TryGetValue(page, out seen);
                    attempts[page] = seen + 1;
                }

                return seen == 0
                    ? StubResponse.Json(OkxPayloads.Error(OkxVenue.ErrorTooManyRequests, "Too Many Requests"))
                    : StubResponse.Json(TiersFor(page));
            })
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        await provider.LoadAllAsync(CancellationToken.None);

        // Every page was asked twice, and every family still carries the margin its tier published - which is the
        // point: a refusal that was retried costs a wait, not an instrument list.
        Assert.All(attempts.Values, count => Assert.Equal(2, count));
        Assert.Equal(10, provider.GetAll().Count);
        Assert.All(provider.GetAll(), i => Assert.Equal(0.01m, i.MarginInit));
    }

    /// <summary>
    /// And every family still gets its margin: a fan-out that lost answers would be faster and wrong, which is the
    /// only way this change could do harm. Asserted on the instruments rather than on the requests.
    /// </summary>
    [Fact]
    public async Task Every_family_still_carries_the_margin_its_tier_published()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => StubResponse.Json(ManyFamilies(40)))
            .On("GET", TiersPath, request => StubResponse.Json(TiersFor(request.Query("instFamily"))))
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        await provider.LoadAllAsync(CancellationToken.None);

        IReadOnlyList<Instrument> loaded = provider.GetAll();

        Assert.Equal(40, loaded.Count);
        Assert.All(loaded, i => Assert.Equal(0.01m, i.MarginInit));
        Assert.All(loaded, i => Assert.Equal(0.004m, i.MarginMaint));
        Assert.All(loaded, i => Assert.Equal(100m, i.MaxLeverage));
        Assert.NotNull(provider.Find(MarketKey.Parse("bx-market:v2/OKX/A017-USDT-SWAP")));
    }

    /// <summary>
    /// One page the venue will not answer for costs its own page and not the rest. This was true of the loop and has
    /// to stay true of the fan-out, where a thrown exception would otherwise take the whole Task.WhenAll with it.
    /// </summary>
    [Fact]
    public async Task A_family_the_tier_endpoint_refuses_does_not_cost_the_others_their_margin()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", InstrumentsPath, _ => StubResponse.Json(ManyFamilies(40)))
            .On("GET", TiersPath, request => request.Query("instFamily")?.Contains("A000-USDT", StringComparison.Ordinal) == true
                ? StubResponse.Json(OkxPayloads.Error(OkxVenue.ErrorParameter, "Parameter instFamily error"))
                : StubResponse.Json(TiersFor(request.Query("instFamily"))))
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        await provider.LoadAllAsync(CancellationToken.None);

        // The refused page's five families are published without a margin requirement, which is visible; the other
        // thirty-five carry theirs.
        IReadOnlyList<Instrument> loaded = provider.GetAll();

        Assert.Equal(40, loaded.Count);
        Assert.Equal(5, loaded.Count(i => i.MarginInit == 0m));
        Assert.Equal(35, loaded.Count(i => i.MarginInit == 0.01m));
    }
}
