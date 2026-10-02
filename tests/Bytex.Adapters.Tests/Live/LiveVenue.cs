using Bytex.Core.Adapters;
using Xunit;

namespace Bytex.Adapters.Tests.Live;

/// <summary>
/// The gate on the tests that talk to a real venue (R12.10).
///
/// <para>
/// Every other adapter test runs against recorded payloads with no key and no network (R12.8), which is what makes the
/// suite fast and honest about determinism - and leaves one thing it cannot see. A recording is a photograph. If a
/// venue renames a field, drops one, or starts sending a number as a string, every offline test still passes against
/// the photograph while the adapter is broken against the venue. These tests are the periodic check that the
/// photograph is still the venue: they run the same providers the offline tests run, against the live endpoint, and
/// hold them to the same assertions.
/// </para>
///
/// <para>
/// <b>Opt-in, and skipped rather than passed.</b> CONTRIBUTING says tests that need a venue do not belong in the
/// default run, and a test that quietly passes when it did nothing is worse than one that is not there: it reports
/// coverage nobody has. So without <see cref="EnvEnabled"/> they report SKIP with the reason, which is visible in
/// every runner's output and in CI logs.
/// </para>
///
/// <para>
/// <b>Read-only, and no orders.</b> These fetch public listings and public candles. Nothing here places, amends or
/// cancels an order, and nothing here needs a key: an automated suite that sends orders to a venue is a different
/// decision from an automated suite that reads one, and it is not mine to make. What needs a key and an order is the
/// owner's own rehearsal against each venue, which is a checklist rather than a test.
/// </para>
/// </summary>
internal static class LiveVenue
{
    /// <summary>Set this to 1 to run them: <c>BYTEX_LIVE_VENUE_TESTS=1 dotnet test</c>.</summary>
    public const string EnvEnabled = "BYTEX_LIVE_VENUE_TESTS";

    /// <summary>
    /// How long one venue is given to answer. Minutes rather than seconds, and measured rather than guessed: Bitget
    /// publishes position tiers for one contract at a time, so listing its whole derivative family is a request per
    /// contract - hundreds of them, held to the venue's own rate budget. A timeout shorter than that reports a defect
    /// in the adapter where the truth is a slow endpoint, which is the wrong answer in the most misleading direction.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnvEnabled) is { Length: > 0 } value
        && !value.Equals("0", StringComparison.Ordinal)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A fact that talks to a real venue, skipped with a reason when nobody asked for it.</summary>
internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!LiveVenue.Enabled)
        {
            Skip = $"Talks to a real venue. Set {LiveVenue.EnvEnabled}=1 to run it; see docs/integrations/README.md.";
        }
    }
}

/// <summary>The same gate for a theory, which is how one test covers eight venues.</summary>
internal sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (!LiveVenue.Enabled)
        {
            Skip = $"Talks to a real venue. Set {LiveVenue.EnvEnabled}=1 to run it; see docs/integrations/README.md.";
        }
    }
}

/// <summary>
/// Every live test in one collection, so they run one after another.
///
/// <para>
/// Not a style choice: xUnit runs collections in parallel, and three tests each listing OKX's swap family is three
/// times ninety-six requests at once - the venue answered 50011, Too Many Requests, which is the venue being right.
/// A test suite that trips a rate limit is testing itself.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LiveVenueCollection : ICollectionFixture<LiveVenueListings>
{
    public const string Name = "live venue";
}

/// <summary>
/// One listing per family, loaded once and shared.
///
/// <para>
/// Three checks ask about the same listing - the fields, the margin, the ceiling - and loading it three times asks a
/// venue for its whole instrument list three times for one answer. On the venues that publish margin per contract that
/// is hundreds of requests each time, which is how this suite first made a venue refuse it.
/// </para>
/// </summary>
public sealed class LiveVenueListings
{
    private readonly Dictionary<string, Task<IInstrumentProvider>> _loaded = new(StringComparer.Ordinal);

    public Task<IInstrumentProvider> ListingAsync(string family, Func<Task<IInstrumentProvider>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        lock (_loaded)
        {
            if (!_loaded.TryGetValue(family, out Task<IInstrumentProvider>? task))
            {
                task = load();
                _loaded[family] = task;
            }

            return task;
        }
    }
}
