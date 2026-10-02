using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Backtest;

/// <summary>
/// A venue behaviour a run can add, that the simulator does not have to know about (R8.19).
/// <para>
/// The simulator models what venues do in general - it fills, it charges commission, it charges funding on a
/// perpetual, it liquidates an account that runs out of margin. What it cannot hold is the long tail of things
/// individual venues do that change a result: interest charged on a leveraged position held overnight, a scheduled
/// maintenance window, a settlement convention one exchange has and its competitors do not. Each is small, each is
/// real, and building them in would mean the simulator carrying every venue's idiosyncrasies forever.
/// </para>
/// <para>
/// So a behaviour is a module here instead. It sees the venue's clock advance and what the account holds, and the one
/// thing it can do is move money, with a reason. That is deliberately narrow: a module cannot fill an order, cancel
/// one, or change a position, because a behaviour that could do those is not an idiosyncrasy - it is the matching
/// engine, and it belongs in the matching engine where the whole suite tests it.
/// </para>
/// </summary>
public interface ISimulationModule
{
    /// <summary>
    /// What this module is, in the run's own report of what it did. It appears in
    /// <see cref="SimulatedExchange.Applied"/> only when the module actually moved money - the same rule the built-in
    /// behaviours follow, because a run configured for a behaviour that never happened must not claim it did.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The venue's clock has reached <paramref name="now"/>. Called as a run advances, never backwards, and possibly
    /// many times within one interval - a module that acts on a boundary has to decide for itself whether the
    /// boundary has been crossed since it last acted, because only it knows what its boundary is.
    /// </summary>
    void OnTime(UnixNanos now, ISimulatedVenueState venue);
}

/// <summary>
/// What a <see cref="ISimulationModule"/> may see and do. Narrow on purpose: enough to charge a position for being
/// held, and not enough to trade.
/// </summary>
public interface ISimulatedVenueState
{
    /// <summary>The venue this behaviour belongs to.</summary>
    Venue Venue { get; }

    /// <summary>Somewhere to say what happened, and why a charge was or was not made.</summary>
    ILogger Log { get; }

    /// <summary>Every instrument with a position open on it, and how much is held - negative when short.</summary>
    IReadOnlyList<(MarketKey MarketKey, decimal SignedQuantity)> OpenPositions { get; }

    /// <summary>The instrument, as the run loaded it, or null when this venue has never been given it.</summary>
    Instrument? Instrument(MarketKey marketKey);

    /// <summary>
    /// What one unit of the instrument is worth here now - the last traded price, or the touch when nothing has
    /// traded - or null when this venue has seen no price at all. A behaviour that charges against a notional cannot
    /// invent one, and charging zero would be a silently wrong answer rather than a missing one.
    /// </summary>
    decimal? Price(MarketKey marketKey);

    /// <summary>
    /// The notional of a position, the way this venue values it: inverse instruments are quoted the other way up, and
    /// a contract is a multiple of the instrument's own unit. Here rather than in each module because getting it
    /// wrong is silent - an inverse position valued as a linear one is wrong by the square of the price.
    /// </summary>
    decimal? Notional(MarketKey marketKey);

    /// <summary>
    /// Move money against the account, in the instrument's settlement currency, and record that this module did it. A
    /// negative amount is a cost to the account.
    /// </summary>
    void Charge(string module, Money amount, string reason, UnixNanos ts);
}

/// <summary>
/// Interest on a position held across a rollover time (R8.19), which is how a venue charges for the money behind a
/// leveraged position - separately from funding, and on instruments that have no funding at all.
/// <para>
/// Both sides normally pay, because both borrow: long borrows the quote currency and short borrows the base. That is
/// the opposite of funding, where one side pays the other, and it is why the two rates are given rather than derived
/// - a venue's convention is the caller's fact, and a simulator that guessed it would produce a confident number
/// nobody could check.
/// </para>
/// </summary>
/// <summary>
/// Makes a simulation module from a NAME and string parameters, so a run's description can ask for a venue behaviour
/// that lives in code.
///
/// <para>
/// The same shape as <c>IIndicatorFactory</c>, and for the same reason: a module is an object and a configuration file
/// is not, so something has to turn a name into one. Without it a behaviour could only be added by a caller building
/// the venue in C#, which is why the one behaviour this engine ships - rollover interest (R8.19) - was constructed
/// nowhere outside its own file and could not be switched on by anybody running from a configuration.
/// </para>
/// </summary>
public interface ISimulationModuleFactory
{
    /// <summary>The names this factory answers to, matched without case.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Builds the module, or throws saying which parameter it needed and did not get.</summary>
    ISimulationModule Create(string name, IReadOnlyDictionary<string, string> parameters);
}

/// <summary>
/// The behaviours this engine ships, by name. Today that is rollover interest, which needs the venue's rollover time
/// and a daily rate for each side - three numbers only the venue knows, which is why they are asked for rather than
/// assumed.
/// </summary>
public sealed class BuiltinSimulationModuleFactory : ISimulationModuleFactory
{
    /// <inheritdoc/>
    public IReadOnlyList<string> Names => [SimulationCapabilities.RolloverInterest];

    /// <inheritdoc/>
    public ISimulationModule Create(string name, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!string.Equals(name, SimulationCapabilities.RolloverInterest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{name}' is not a behaviour this engine ships. It ships {SimulationCapabilities.RolloverInterest}.");
        }

        return new RolloverInterestModule(
            TimeSpan.Parse(Required(parameters, "rolloverTimeOfDay", name), System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(Required(parameters, "longDailyRate", name), System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(Required(parameters, "shortDailyRate", name), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key, string name) =>
        parameters.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"The {name} behaviour needs '{key}'. Only the venue knows it, so it is asked for rather than assumed: "
                + "a rollover time of day in UTC, and a daily rate for each side as a fraction of notional.");
}

public sealed class RolloverInterestModule : ISimulationModule
{
    private readonly TimeSpan _rollover;
    private readonly decimal _longRate;
    private readonly decimal _shortRate;
    private DateOnly? _chargedThrough;

    /// <param name="rolloverTimeOfDay">The venue's rollover, as a time of day in UTC.</param>
    /// <param name="longDailyRate">The share of a long position's notional it pays per rollover. Positive costs money.</param>
    /// <param name="shortDailyRate">The same for a short position. Positive costs money; negative is a position paid to be held.</param>
    public RolloverInterestModule(TimeSpan rolloverTimeOfDay, decimal longDailyRate, decimal shortDailyRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rolloverTimeOfDay, TimeSpan.Zero, nameof(rolloverTimeOfDay));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rolloverTimeOfDay, TimeSpan.FromDays(1), nameof(rolloverTimeOfDay));
        _rollover = rolloverTimeOfDay;
        _longRate = longDailyRate;
        _shortRate = shortDailyRate;
    }

    /// <inheritdoc/>
    public string Name => SimulationCapabilities.RolloverInterest;

    /// <inheritdoc/>
    public void OnTime(UnixNanos now, ISimulatedVenueState venue)
    {
        ArgumentNullException.ThrowIfNull(venue);
        DateTimeOffset clock = DateTimeOffset.FromUnixTimeMilliseconds(now.Value / UnixNanos.NanosPerMillisecond);

        // The rollover most recently passed. A run that jumps a week - a gap in the data, or bars an hour apart -
        // must not be charged seven times for one crossing and must not skip six, so what is tracked is the last day
        // charged rather than the last time this was called.
        DateOnly due = clock.TimeOfDay >= _rollover
            ? DateOnly.FromDateTime(clock.UtcDateTime)
            : DateOnly.FromDateTime(clock.UtcDateTime).AddDays(-1);

        if (_chargedThrough is null)
        {
            // Nothing is charged on the first sight of the clock: a run starting after the rollover has not held
            // anything across it, and charging here would bill a position for a day before it was opened.
            _chargedThrough = due;
            return;
        }

        while (_chargedThrough < due)
        {
            _chargedThrough = _chargedThrough.Value.AddDays(1);
            ChargeOnce(now, venue, _chargedThrough.Value);
        }
    }

    private void ChargeOnce(UnixNanos now, ISimulatedVenueState venue, DateOnly day)
    {
        foreach ((MarketKey marketKey, decimal signed) in venue.OpenPositions)
        {
            if (signed == 0m)
            {
                continue;
            }

            if (venue.Instrument(marketKey) is not { } instrument)
            {
                continue;
            }

            if (venue.Notional(marketKey) is not { } notional)
            {
                venue.Log.LogWarning(
                    "Rollover on {Day} for {MarketKey} found no price to value the position at, so no interest was charged",
                    day,
                    marketKey);
                continue;
            }

            decimal rate = signed > 0m ? _longRate : _shortRate;
            if (rate == 0m)
            {
                continue;
            }

            venue.Charge(
                Name,
                new Money(-notional * rate, instrument.SettlementCurrency),
                string.Create(CultureInfo.InvariantCulture, $"rollover interest on {signed} {marketKey} at {rate} for {day:yyyy-MM-dd}"),
                now);
        }
    }
}

/// <summary>What an added behaviour did, so a run can report it rather than a reader having to trust the configuration.</summary>
/// <param name="Module">The behaviour's own name.</param>
/// <param name="Amount">What moved, negative when the account paid.</param>
/// <param name="Reason">What the behaviour said it was for.</param>
/// <param name="EventTime">When the venue's clock stood at the time it acted.</param>
public sealed record ModuleCharge(string Module, Money Amount, string Reason, UnixNanos EventTime);
