using System.Diagnostics;
using Bytex.Live.Network;

namespace Bytex.Live.Tests.Network;

// Why: back-off and rate limiting are what keep a node from being banned by a venue; the arithmetic must hold.
public sealed class RetryAndRateLimitTests
{
    [Theory]
    [InlineData(1, 250)]
    [InlineData(2, 500)]
    [InlineData(3, 1000)]
    [InlineData(4, 2000)]
    [InlineData(5, 4000)]
    [InlineData(6, 5000)] // 8000 ms capped at the 5 s default maximum
    [InlineData(12, 5000)]
    public void Default_backoff_doubles_from_250ms_and_is_capped_at_5s_plus_under_100ms_of_jitter(int attempt, int expectedBaseMs)
    {
        RetryPolicy policy = new();

        for (int i = 0; i < 50; i++)
        {
            double delay = policy.DelayFor(attempt).TotalMilliseconds;
            Assert.InRange(delay, expectedBaseMs, expectedBaseMs + 99);
        }
    }

    [Fact]
    public void Custom_backoff_uses_its_own_initial_delay_factor_and_cap()
    {
        RetryPolicy policy = new(MaxAttempts: 10, InitialDelay: TimeSpan.FromMilliseconds(100), BackoffFactor: 3.0, MaxDelay: TimeSpan.FromMilliseconds(1000));

        Assert.InRange(policy.DelayFor(1).TotalMilliseconds, 100, 199);
        Assert.InRange(policy.DelayFor(2).TotalMilliseconds, 300, 399);
        Assert.InRange(policy.DelayFor(3).TotalMilliseconds, 900, 999);
        Assert.InRange(policy.DelayFor(4).TotalMilliseconds, 1000, 1099);
    }

    [Fact]
    public void The_None_policy_makes_a_single_attempt()
    {
        Assert.Equal(1, RetryPolicy.None.MaxAttempts);
    }

    [Fact]
    public async Task Requests_within_capacity_pass_immediately_and_the_next_one_waits_for_the_window_to_roll()
    {
        TimeSpan window = TimeSpan.FromMilliseconds(400);
        RateLimiter limiter = new(3, window);
        Stopwatch watch = Stopwatch.StartNew();

        await limiter.WaitAsync();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        TimeSpan afterBurst = watch.Elapsed;
        await limiter.WaitAsync();
        TimeSpan afterThrottled = watch.Elapsed;

        Assert.True(afterBurst < window, $"burst of 3 took {afterBurst.TotalMilliseconds} ms");
        Assert.True(afterThrottled >= window - TimeSpan.FromMilliseconds(50), $"4th request passed after only {afterThrottled.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task A_weighted_request_consumes_that_many_slots()
    {
        TimeSpan window = TimeSpan.FromMilliseconds(400);
        RateLimiter limiter = new(10, window);
        Stopwatch watch = Stopwatch.StartNew();

        await limiter.WaitAsync(weight: 8);
        await limiter.WaitAsync(weight: 2);
        TimeSpan withinBudget = watch.Elapsed;
        await limiter.WaitAsync(weight: 1);
        TimeSpan overBudget = watch.Elapsed;

        Assert.True(withinBudget < window, $"weights 8 + 2 took {withinBudget.TotalMilliseconds} ms");
        Assert.True(overBudget >= window - TimeSpan.FromMilliseconds(50), $"11th unit passed after only {overBudget.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task Cancelling_a_throttled_wait_throws_and_leaves_the_limiter_usable()
    {
        RateLimiter limiter = new(1, TimeSpan.FromMilliseconds(300));
        await limiter.WaitAsync();
        using CancellationTokenSource cts = new();

        Task throttled = limiter.WaitAsync(ct: cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttled.WaitAsync(TimeSpan.FromSeconds(15)));

        await limiter.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15)); // would hang forever if the cancelled waiter kept the gate
    }

    [Fact]
    public void A_limiter_needs_a_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimiter(0, TimeSpan.FromSeconds(1)));
    }
}
