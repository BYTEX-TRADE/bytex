using System.Text.RegularExpressions;
using Bytex.Adapters.Tests.Support;
using Bytex.Live.Network;

namespace Bytex.Adapters.Tests;

// A signed request carries a timestamp and the venue checks it against ITS clock. Signing with the host's made
// correctness depend on this machine being right, and a machine routinely is not - a second of drift is ordinary on
// a desktop, where the operating system resynchronises on a long interval.
//
// What that looked like on a real node: public data connected and streaming, 880 events, the node reporting itself
// running with a strategy loaded - and every signed request refused, so it could not place, cancel or reconcile a
// single order. The host was 1,067 ms ahead of the venue.
//
//     Bybit error 10002: invalid request, please check your server timestamp or recv_window param:
//     req_timestamp[1790793252971], server_timestamp[1790793251904], recv_window[5000]
//
// Raising recv_window does not fix it, which is the intuitive move: the venue's rule is asymmetric, so the window
// buys tolerance for a clock that LAGS while one that runs ahead is capped at a fixed second whatever the window
// says.
public sealed class SignedRequestsUseVenueTimeTests
{
    /// <summary>The offset is the venue's time minus this machine's, so a host that is behind gets a positive one.</summary>
    [Theory]
    [InlineData(1_067, -1_067)]   // the measured case: the host was ahead, so the correction is negative
    [InlineData(-450, 450)]
    [InlineData(0, 0)]
    public async Task The_offset_is_measured_against_the_venue(long hostAheadByMs, long expectedOffsetSign)
    {
        VenueClock clock = new("TEST");
        long venueNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - hostAheadByMs;

        await clock.MeasureAsync(_ => Task.FromResult(venueNow), CancellationToken.None);

        Assert.True(clock.IsMeasured);

        // Within a few milliseconds: the test itself takes time between reading the clock and measuring.
        Assert.True(Math.Abs(clock.OffsetMs - expectedOffsetSign) < 200, $"offset was {clock.OffsetMs}, expected about {expectedOffsetSign}");
    }

    /// <summary><b>And the timestamp a request would carry is the venue's, not this machine's.</b></summary>
    [Fact]
    public async Task The_timestamp_carries_the_venue_time_not_the_host()
    {
        VenueClock clock = new("TEST");
        long hostNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long venueNow = hostNow - 1_067;

        await clock.MeasureAsync(_ => Task.FromResult(venueNow), CancellationToken.None);

        // The host is a second ahead, so a stamp taken now must be BEHIND the host's own clock by about that much.
        long stamped = clock.NowMs();
        Assert.True(stamped < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 900, $"stamp {stamped} was not corrected back toward the venue");
        Assert.Equal(clock.NowMs() / 1000L, clock.NowSeconds());
    }

    /// <summary>Measured once, then left alone until it goes stale - so a signed request is not a second request.</summary>
    [Fact]
    public async Task The_offset_is_not_re_read_on_every_request()
    {
        VenueClock clock = new("TEST");
        int reads = 0;
        Task<long> Read(CancellationToken _)
        {
            reads++;
            return Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        await clock.MeasureAsync(Read, CancellationToken.None);
        for (int i = 0; i < 20; i++)
        {
            await clock.EnsureFreshAsync(Read, CancellationToken.None);
        }

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// And a venue that refuses a timestamp gets the offset measured again on the next signed request, because
    /// whatever it was, it is now known to be wrong.
    /// </summary>
    [Fact]
    public async Task A_refused_timestamp_forces_the_next_request_to_measure_again()
    {
        VenueClock clock = new("TEST");
        int reads = 0;
        Task<long> Read(CancellationToken _)
        {
            reads++;
            return Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        await clock.MeasureAsync(Read, CancellationToken.None);
        await clock.EnsureFreshAsync(Read, CancellationToken.None);
        Assert.Equal(1, reads);

        clock.MarkStale();
        await clock.EnsureFreshAsync(Read, CancellationToken.None);

        Assert.Equal(2, reads);
    }

    /// <summary>
    /// A venue whose time cannot be read is not a venue that cannot trade. The offset stays as it was and the
    /// request goes out on the host's clock, which is what the engine did before - refusing to trade because a
    /// time endpoint is down would be worse than the drift it guards against.
    /// </summary>
    [Fact]
    public async Task A_time_endpoint_that_fails_leaves_the_clock_usable()
    {
        VenueClock clock = new("TEST");

        await clock.MeasureAsync(_ => throw new HttpRequestException("the venue's time endpoint is down"), CancellationToken.None);

        Assert.False(clock.IsMeasured);
        Assert.Equal(0, clock.OffsetMs);
        Assert.True(Math.Abs(clock.NowMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 200);
    }

    /// <summary>
    /// <b>A request never takes the first measurement - connecting does.</b>
    ///
    /// <para>
    /// This is the shape the first attempt got wrong. Measuring lazily on the first signed request put a second
    /// endpoint in the path of placing an order, and paid a round trip on the first order after every quiet spell.
    /// The test suite said so immediately: fifty-four tests that assert what a client sends saw a time request
    /// arrive in front of the order. So a clock that has never been measured stays unmeasured until something
    /// connects, and signs on this machine's clock meanwhile - which is what the engine did before any of this.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_clock_that_has_never_been_measured_is_not_measured_by_a_request()
    {
        VenueClock clock = new("TEST");
        int reads = 0;

        await clock.EnsureFreshAsync(
            _ =>
            {
                reads++;
                return Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            },
            CancellationToken.None);

        Assert.Equal(0, reads);
        Assert.False(clock.IsMeasured);
    }

    /// <summary>
    /// <b>And no venue signs with the host clock again.</b> Six adapters had the same shape and Bybit was simply
    /// the strictest, so it is where it surfaced; the rest would fail the same way at a larger offset, silently
    /// and only on signed calls, while market data kept flowing and the node looked alive.
    ///
    /// <para>
    /// The two nonce venues are exempt and named: Kraken and Hyperliquid sign with a monotonic counter seeded from
    /// the clock, which is deliberately NOT the venue's time - a nonce must never repeat nor go backwards, and both
    /// take the maximum of the counter and now, so a clock correction cannot move it backwards either.
    /// </para>
    /// </summary>
    [Fact]
    public void No_adapter_signs_a_timestamp_with_the_host_clock()
    {
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains("Bytex.Adapters.", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"UtcNow\.ToUnixTime(Milli)?[Ss]econds\(\)", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    continue;
                }

                // A nonce is a counter, not a timestamp the venue compares against its own clock.
                string context = string.Join('\n', lines.Skip(Math.Max(0, i - 12)).Take(16));
                if (context.Contains("once", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these sign with this machine's clock instead of the venue's: " + string.Join(", ", offenders));
    }
}
