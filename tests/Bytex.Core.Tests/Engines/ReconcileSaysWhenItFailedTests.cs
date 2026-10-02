using Bytex.Core.Engines;
using Bytex.Core.Tests.Support;
using Xunit;

namespace Bytex.Core.Tests.Engines;

// A node whose every reconcile threw looked exactly like one reconciling cleanly with nothing to correct: checks
// nothing, differences nothing, status running. That is how a node which could not place an order for an hour -
// because every signed request was refused - showed as healthy on a page while the reason sat in a log nobody
// reads.
//
// An attempt that never produced a report is not a reconciliation that found nothing, and the two must not count
// as the same thing.
public sealed class ReconcileSaysWhenItFailedTests
{
    private static OrderCoordinator Engine() => new ExecHarness().Engine;

    [Fact]
    public void A_fresh_engine_has_failed_nothing()
    {
        OrderCoordinator engine = Engine();

        Assert.Equal(0, engine.ReconciliationFailures);
        Assert.Null(engine.LastReconciliationError);
        Assert.Null(engine.LastReconciliationFailure);
    }

    /// <summary><b>A failed attempt is counted and says why.</b></summary>
    [Fact]
    public void A_failed_attempt_is_counted_and_carries_its_reason()
    {
        OrderCoordinator engine = Engine();

        engine.RecordReconciliationFailure("Bybit error 10002: invalid request, please check your server timestamp");

        Assert.Equal(1, engine.ReconciliationFailures);
        Assert.Contains("10002", engine.LastReconciliationError!, StringComparison.Ordinal);
        Assert.NotNull(engine.LastReconciliationFailure);
    }

    /// <summary>They accumulate, because a node drifting away from its venue does it once a minute, not once.</summary>
    [Fact]
    public void Failures_accumulate()
    {
        OrderCoordinator engine = Engine();

        engine.RecordReconciliationFailure("first");
        engine.RecordReconciliationFailure("second");
        engine.RecordReconciliationFailure("third");

        Assert.Equal(3, engine.ReconciliationFailures);
        Assert.Equal("third", engine.LastReconciliationError);
    }

    /// <summary>
    /// A failure does not pretend a check happened. This is the distinction the whole thing exists for: without
    /// it, a page showing "checks 0, differences 0" cannot tell a node that reconciled cleanly from one that never
    /// managed to ask.
    /// </summary>
    [Fact]
    public void A_failure_is_not_counted_as_a_check()
    {
        OrderCoordinator engine = Engine();
        long checksBefore = engine.ReconciliationCount;

        engine.RecordReconciliationFailure("the venue refused");

        Assert.Equal(checksBefore, engine.ReconciliationCount);
        Assert.Equal(0, engine.ReconciledDifferences);
        Assert.Equal(1, engine.ReconciliationFailures);
    }

    /// <summary>An attempt that failed without a message still says something rather than nothing.</summary>
    [Fact]
    public void A_failure_without_a_message_still_says_something()
    {
        OrderCoordinator engine = Engine();

        engine.RecordReconciliationFailure("   ");

        Assert.False(string.IsNullOrWhiteSpace(engine.LastReconciliationError));
    }
}
