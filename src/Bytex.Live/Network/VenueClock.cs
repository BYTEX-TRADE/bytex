using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Bytex.Live.Network;

/// <summary>
/// The time a venue believes it is, kept as an offset from this machine's clock.
///
/// <para>
/// A signed request carries a timestamp and the venue checks it against its own clock. Signing with the raw host
/// clock therefore makes correctness depend on the host being right, and a host is routinely not: a second of drift
/// is ordinary on a desktop, where the operating system resynchronises on a long interval. A node with a drifted
/// clock connects, streams public data, reports itself running - and every signed request fails, so it cannot
/// place, cancel or reconcile a single order. Public data keeps flowing throughout, which is what makes it look
/// healthy.
/// </para>
///
/// <para>
/// <b>Raising the venue's tolerance window does not fix it</b>, which is the intuitive move and a wasted afternoon.
/// The rule these venues apply is asymmetric: a timestamp may lag by the tolerance window, but may only run ahead
/// by a small fixed amount, whatever the window is set to. A host one second fast is refused however generous the
/// window.
/// </para>
///
/// <para>
/// So the offset is measured against the venue itself and added to every signed timestamp. It is re-measured when
/// it grows old, because a clock that was right at connect can drift during a session that runs for days, and on
/// demand when a venue rejects a timestamp outright.
/// </para>
/// </summary>
public sealed class VenueClock
{
    /// <summary>
    /// How long a measured offset is trusted before the next signed request re-measures it.
    ///
    /// <para>
    /// Chosen against what it guards: a host whose clock is disciplined by the operating system drifts by
    /// milliseconds over an hour, and the venues' ceiling for running ahead is of the order of a second. Half an
    /// hour therefore re-measures long before ordinary drift could reach the ceiling, while costing one unsigned
    /// request in that time. It is not a timer - the check happens on a signed request, so an idle node makes no
    /// traffic at all.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger? _log;
    private readonly string _venue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _offsetMs;
    private DateTimeOffset _measuredAt = DateTimeOffset.MinValue;
    private volatile bool _stale;

    public VenueClock(string venue, ILogger? logger = null)
    {
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _log = logger;
    }

    /// <summary>Milliseconds to add to this machine's clock to get the venue's. Zero until first measured.</summary>
    public long OffsetMs => Interlocked.Read(ref _offsetMs);

    /// <summary>Whether an offset has ever been measured. A clock that has not is not wrong, only unadjusted.</summary>
    public bool IsMeasured => _measuredAt != DateTimeOffset.MinValue;

    /// <summary>The venue's time now, in milliseconds since the epoch.</summary>
    public long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + OffsetMs;

    /// <summary>The venue's time now, in whole seconds - what the venues that sign in seconds want.</summary>
    public long NowSeconds() => NowMs() / 1000L;

    /// <summary>
    /// Forces the next <see cref="EnsureFreshAsync"/> to measure again. Called when a venue has refused a timestamp:
    /// whatever the offset was, it is now known to be wrong.
    /// </summary>
    public void MarkStale() => _stale = true;

    /// <summary>
    /// Whether a measurement is due.
    ///
    /// <para>
    /// A clock that has NEVER been measured is deliberately not due. Connecting is what takes the first
    /// measurement, because that is where a client already does its setup - and putting it in the request path
    /// instead would make placing an order depend on a second endpoint being up, and would pay a round trip on the
    /// first order after every quiet half hour. A client that is driven without connecting therefore signs on this
    /// machine's clock, which is what the engine did before this existed.
    /// </para>
    /// </summary>
    private bool NeedsMeasuring() => _stale || (IsMeasured && DateTimeOffset.UtcNow - _measuredAt >= SyncInterval);

    /// <summary>
    /// Measures now, whatever the last measurement said. This is what connecting calls.
    /// </summary>
    public Task MeasureAsync(Func<CancellationToken, Task<long>> readVenueMs, CancellationToken ct)
    {
        _stale = true;
        return EnsureFreshAsync(readVenueMs, ct);
    }

    /// <summary>
    /// Measures the offset if one is due - see <see cref="NeedsMeasuring"/>.
    ///
    /// <para>
    /// <paramref name="readVenueMs"/> reads the venue's own time endpoint, which is public on every venue this
    /// engine speaks to - so this works before a key is configured and cannot itself fail for want of one. A
    /// failure to read it is logged and left alone rather than thrown: an unadjusted clock is what the engine had
    /// before, and refusing to trade because a time endpoint is down would be worse than the drift it guards.
    /// </para>
    /// </summary>
    public async Task EnsureFreshAsync(Func<CancellationToken, Task<long>> readVenueMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readVenueMs);
        if (!NeedsMeasuring())
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have measured it while this one waited.
            if (!NeedsMeasuring())
            {
                return;
            }

            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long venueMs = await readVenueMs(ct).ConfigureAwait(false);
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // The midpoint of the round trip, so the network delay is split between the two legs instead of being
            // charged to the offset as if the venue's answer had arrived instantly.
            long hostMs = before + ((after - before) / 2);
            long offset = venueMs - hostMs;
            Interlocked.Exchange(ref _offsetMs, offset);
            _measuredAt = DateTimeOffset.UtcNow;
            _stale = false;

            if (Math.Abs(offset) > 0)
            {
                _log?.LogInformation(
                    "{Venue} time offset {Offset} ms (this machine is {Direction} by {Amount} ms, round trip {Rtt} ms); signed requests carry the venue's time",
                    _venue,
                    offset.ToString(CultureInfo.InvariantCulture),
                    offset < 0 ? "ahead" : "behind",
                    Math.Abs(offset).ToString(CultureInfo.InvariantCulture),
                    (after - before).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log?.LogWarning(e, "Could not read {Venue}'s time; signing with this machine's clock unadjusted", _venue);
        }
        finally
        {
            _gate.Release();
        }
    }
}
