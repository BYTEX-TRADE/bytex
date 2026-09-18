using System.Collections.Concurrent;

namespace Bytex.Live.Tests;

// Why: the whole live engine relies on one guarantee, that everything entering from I/O threads and timers
// runs on a single kernel thread, in order, with back-pressure instead of unbounded growth.
public sealed class LiveKernelLoopTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Actions_posted_from_many_threads_all_run_on_the_single_loop_thread()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        ConcurrentBag<int> executedOn = new();
        ConcurrentBag<int> postedFrom = new();

        Task[] producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            postedFrom.Add(Environment.CurrentManagedThreadId);
            for (int i = 0; i < 200; i++)
            {
                loop.Post(() => executedOn.Add(Environment.CurrentManagedThreadId));
            }
        })).ToArray();
        await Task.WhenAll(producers).WaitAsync(_timeout);
        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal(1600, executedOn.Count);
        Assert.Equal([loop.ManagedThreadId], executedOn.Distinct().ToArray());
        Assert.DoesNotContain(loop.ManagedThreadId, postedFrom);
        await loop.StopAsync();
    }

    [Fact]
    public async Task Actions_posted_by_one_thread_run_in_posting_order()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        List<int> seen = new();

        for (int i = 0; i < 5000; i++)
        {
            int captured = i;
            loop.Post(() => seen.Add(captured));
        }

        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal(Enumerable.Range(0, 5000), seen);
        await loop.StopAsync();
    }

    [Fact]
    public async Task Each_producer_keeps_its_own_order_when_several_threads_post_concurrently()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        List<(int Producer, int Sequence)> seen = new();

        Task[] producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                int sequence = i;
                loop.Post(() => seen.Add((producer, sequence)));
            }
        })).ToArray();
        await Task.WhenAll(producers).WaitAsync(_timeout);
        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal(4000, seen.Count);
        foreach (IGrouping<int, (int Producer, int Sequence)> group in seen.GroupBy(s => s.Producer))
        {
            Assert.Equal(Enumerable.Range(0, 1000), group.Select(s => s.Sequence));
        }

        await loop.StopAsync();
    }

    [Fact]
    public async Task An_exception_in_one_action_does_not_stop_later_actions_from_running()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        bool ranAfterFailure = false;

        loop.Post(() => throw new InvalidOperationException("handler bug"));
        await loop.InvokeAsync(() => ranAfterFailure = true).WaitAsync(_timeout);

        Assert.True(ranAfterFailure);
        long processed = await loop.InvokeAsync(() => loop.Processed).WaitAsync(_timeout);
        Assert.Equal(2, processed); // the failing action and the one after it; the running one is not counted yet
        await loop.StopAsync();
    }

    [Fact]
    public async Task InvokeAsync_surfaces_the_action_exception_to_the_caller_instead_of_swallowing_it()
    {
        using LiveKernelLoop loop = new();
        loop.Start();

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.InvokeAsync(() => throw new InvalidOperationException("boom")).WaitAsync(_timeout));
        ArgumentException thrownFromFunc = await Assert.ThrowsAsync<ArgumentException>(
            () => loop.InvokeAsync<int>(() => throw new ArgumentException("bad")).WaitAsync(_timeout));

        Assert.Equal("boom", thrown.Message);
        Assert.Equal("bad", thrownFromFunc.Message);
        await loop.StopAsync();
    }

    [Fact]
    public async Task InvokeAsync_returns_the_value_computed_on_the_loop_thread()
    {
        using LiveKernelLoop loop = new();
        loop.Start();

        (int ThreadId, bool OnLoop) result = await loop.InvokeAsync(() => (Environment.CurrentManagedThreadId, loop.IsOnLoopThread)).WaitAsync(_timeout);

        Assert.Equal(loop.ManagedThreadId, result.ThreadId);
        Assert.True(result.OnLoop);
        Assert.False(loop.IsOnLoopThread);
        await loop.StopAsync();
    }

    [Fact]
    public async Task Posting_from_the_loop_thread_runs_inline_so_kernel_code_never_waits_on_its_own_queue()
    {
        using LiveKernelLoop loop = new(capacity: 1);
        loop.Start();
        List<string> order = new();

        await loop.InvokeAsync(() =>
        {
            order.Add("outer-begin");
            loop.Post(() => order.Add("inner"));
            loop.InvokeAsync(() => order.Add("inner-invoke")).GetAwaiter().GetResult();
            order.Add("outer-end");
        }).WaitAsync(_timeout);

        Assert.Equal(["outer-begin", "inner", "inner-invoke", "outer-end"], order);
        await loop.StopAsync();
    }

    [Fact]
    public async Task A_full_queue_blocks_the_poster_until_the_loop_makes_room_and_loses_nothing()
    {
        using LiveKernelLoop loop = new(capacity: 2);
        loop.Start();
        using ManualResetEventSlim loopBusy = new(false);
        using ManualResetEventSlim release = new(false);
        List<int> seen = new();

        loop.Post(() =>
        {
            loopBusy.Set();
            release.Wait(_timeout);
        });
        Assert.True(loopBusy.Wait(_timeout));
        loop.Post(() => seen.Add(1));
        loop.Post(() => seen.Add(2));
        Assert.Equal(2, loop.Pending);

        Task blockedPost = Task.Run(() => loop.Post(() => seen.Add(3)));
        bool completedWhileFull = await Task.WhenAny(blockedPost, Task.Delay(TimeSpan.FromMilliseconds(300))) == blockedPost;
        int pendingWhileFull = loop.Pending;
        release.Set();
        await blockedPost.WaitAsync(_timeout);
        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.False(completedWhileFull);
        Assert.Equal(2, pendingWhileFull);
        Assert.Equal([1, 2, 3], seen);
        await loop.StopAsync();
    }

    [Fact]
    public async Task StopAsync_drains_everything_already_queued_before_the_thread_exits()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        using ManualResetEventSlim loopBusy = new(false);
        using ManualResetEventSlim release = new(false);
        int ran = 0;

        loop.Post(() =>
        {
            loopBusy.Set();
            release.Wait(_timeout);
        });
        Assert.True(loopBusy.Wait(_timeout));
        for (int i = 0; i < 500; i++)
        {
            loop.Post(() => ran++);
        }

        Task stop = loop.StopAsync(_timeout);
        release.Set();
        await stop.WaitAsync(_timeout);

        Assert.Equal(500, ran);
        Assert.Equal(501, loop.Processed);
        Assert.Equal(0, loop.Pending);
    }

    [Fact]
    public async Task StopAsync_gives_up_after_its_timeout_and_discards_work_that_is_still_queued()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        using ManualResetEventSlim loopBusy = new(false);
        using ManualResetEventSlim release = new(false);
        bool queuedWorkRan = false;

        loop.Post(() =>
        {
            loopBusy.Set();
            release.Wait(_timeout);
        });
        Assert.True(loopBusy.Wait(_timeout));
        loop.Post(() => queuedWorkRan = true);

        Task stop = loop.StopAsync(TimeSpan.FromMilliseconds(50));
        bool stoppedWhileActionStillRunning = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(1))) == stop;
        release.Set();
        await stop.WaitAsync(_timeout);

        Assert.False(stoppedWhileActionStillRunning); // the running action is never aborted
        Assert.False(queuedWorkRan);
        Assert.Equal(1, loop.Processed);
    }

    [Fact]
    public async Task Start_is_idempotent_and_does_not_spawn_a_second_consumer()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        loop.Start();
        ConcurrentBag<int> threads = new();

        for (int i = 0; i < 100; i++)
        {
            loop.Post(() => threads.Add(Environment.CurrentManagedThreadId));
        }

        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal([loop.ManagedThreadId], threads.Distinct().ToArray());
        await loop.StopAsync();
    }
}
