using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Core.Tests.Timing;

// Why: a component re-registers its timers when it starts, and SetTimer anchors an interval to "now" when no start is
// given. So a four-hourly rule on a node restarted at 03:59 next fires at 07:59 instead of 04:00 - and drifts again
// at the next restart, and the next. The strategy running afterwards is not the one that was written, and nothing on
// screen says so.
//
// Two decisions are pinned here because otherwise somebody has to guess them:
//
//   the PHASE survives a restart, so the schedule stays the schedule that was asked for;
//   the firings that fell while the node was down are NOT replayed, because running a four-hour rule hours late runs
//   it against a market that has already moved - the schedule lands on the first boundary still ahead and counts what
//   it passed over rather than swallowing it.
public sealed class TimerPhaseTests
{
    private const long Hour = 3_600L * 1_000_000_000L;

    private static UnixNanos At(long seconds) => UnixNanos.FromSeconds(seconds);

    private static TimerSchedule FourHourlyDueAt(long seconds) => new(At(seconds).Value, 4 * Hour);

    [Fact]
    public void A_schedule_still_ahead_is_kept_exactly()
    {
        // Resumed one minute before it was due: it is still due then, and nothing was missed.
        (UnixNanos next, long skipped) = TimerResumption.NextFrom(FourHourlyDueAt(14_400), At(14_340));

        Assert.Equal(At(14_400), next);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void A_schedule_that_fell_due_while_nothing_was_running_moves_on_and_says_how_far()
    {
        // Due at 04:00, resumed at 13:00: 04:00, 08:00 and 12:00 went by. The next run is 16:00, not three runs now.
        (UnixNanos next, long skipped) = TimerResumption.NextFrom(FourHourlyDueAt(14_400), At(46_800));

        Assert.Equal(At(57_600), next);
        Assert.Equal(3, skipped);
    }

    [Fact]
    public void The_phase_survives_however_long_the_gap()
    {
        // The property that matters, rather than one example of it: whatever the downtime, the schedule still falls
        // where the original one would have fallen.
        TimerSchedule schedule = FourHourlyDueAt(14_400);

        foreach (long down in new[] { 1L, 59L, 14_401L, 200_000L, 9_000_000L })
        {
            (UnixNanos next, _) = TimerResumption.NextFrom(schedule, At(14_400 + down));

            Assert.True(next.Value > At(14_400 + down).Value, "a resumed schedule never lands in the past");
            Assert.Equal(schedule.NextNanos % schedule.IntervalNanos, next.Value % schedule.IntervalNanos);
        }
    }

    [Fact]
    public void A_long_outage_is_counted_rather_than_walked()
    {
        // A month down against a one-second timer is two and a half million boundaries. The count has to be right and
        // it must not be reached by stepping, because this runs at the moment a node is trying to start.
        TimerSchedule everySecond = new(At(0).Value, 1_000_000_000L);
        (UnixNanos next, long skipped) = TimerResumption.NextFrom(everySecond, At(2_592_000));

        Assert.Equal(At(2_592_001), next);
        Assert.Equal(2_592_001, skipped);
    }

    [Fact]
    public void A_live_clock_resumes_a_schedule_it_is_handed()
    {
        // The wiring, asserted without pinning the wall clock: whatever the real time is, a resumed timer keeps the
        // saved phase and is still ahead of now.
        LiveClock clock = new(handler => handler.Handle());
        long interval = 4 * Hour;
        long saved = clock.Timestamp.Value - (3 * Hour);

        clock.Resume(new Dictionary<string, TimerSchedule>(StringComparer.Ordinal)
        {
            ["four-hourly"] = new TimerSchedule(saved, interval),
        });

        clock.SetTimer("four-hourly", TimeSpan.FromHours(4), callback: _ => { });

        UnixNanos? next = clock.NextTime("four-hourly");

        Assert.NotNull(next);
        Assert.True(next!.Value.Value > clock.Timestamp.Value, "a resumed timer never lands in the past");
        Assert.Equal(saved % interval, next.Value.Value % interval);
        Assert.Equal(1, clock.Missed);
    }

    [Fact]
    public void A_schedule_whose_interval_changed_is_a_different_schedule()
    {
        // A component that now runs hourly is asking for something else, and bending the old phase onto it would be
        // honouring an instruction nobody gave. It anchors to now, as any unresumed timer does.
        LiveClock clock = new(handler => handler.Handle());
        long saved = clock.Timestamp.Value - (3 * Hour);

        clock.Resume(new Dictionary<string, TimerSchedule>(StringComparer.Ordinal)
        {
            ["rule"] = new TimerSchedule(saved, 4 * Hour),
        });

        clock.SetTimer("rule", TimeSpan.FromHours(1), callback: _ => { });

        Assert.Equal(0, clock.Missed);
        Assert.True(clock.NextTime("rule")!.Value.Value >= clock.Timestamp.Value + Hour - 1_000_000_000L);
    }

    [Fact]
    public void A_timer_given_an_explicit_start_is_left_alone()
    {
        // Whoever names a start means it, and a saved phase must not quietly overrule them.
        LiveClock clock = new(handler => handler.Handle());
        UnixNanos start = new(clock.Timestamp.Value + Hour);

        clock.Resume(new Dictionary<string, TimerSchedule>(StringComparer.Ordinal)
        {
            ["rule"] = new TimerSchedule(clock.Timestamp.Value - (3 * Hour), 4 * Hour),
        });

        clock.SetTimer("rule", TimeSpan.FromHours(4), start: start, callback: _ => { });

        Assert.Equal(new UnixNanos(start.Value + (4 * Hour)), clock.NextTime("rule"));
        Assert.Equal(0, clock.Missed);
    }

    [Fact]
    public void What_a_clock_hands_over_is_what_the_next_one_resumes_from()
    {
        // The round trip, because a snapshot nothing can read back is not a snapshot. One-shot alerts are left out
        // deliberately: an alert is a moment somebody asked for, and a moment that passed while a node was down is
        // not one it can keep.
        LiveClock first = new(handler => handler.Handle());
        first.SetTimer("repeating", TimeSpan.FromMinutes(5), callback: _ => { });
        first.SetTimeAlert("one-shot", new UnixNanos(first.Timestamp.Value + Hour), callback: _ => { });

        IReadOnlyDictionary<string, TimerSchedule> schedules = first.Schedules();

        Assert.Equal(["repeating"], schedules.Keys);

        LiveClock second = new(handler => handler.Handle());
        second.Resume(schedules);
        second.SetTimer("repeating", TimeSpan.FromMinutes(5), callback: _ => { });

        Assert.Equal(first.NextTime("repeating"), second.NextTime("repeating"));
    }
}
