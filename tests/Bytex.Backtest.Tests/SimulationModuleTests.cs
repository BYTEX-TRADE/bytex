using System.Reflection;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Backtest.Tests;

// Why (R8.19): the simulator models what venues do in general - it fills, it charges commission, it charges funding
// on a perpetual, it liquidates an account out of margin. What it cannot hold is the long tail of things individual
// venues do that change a result: interest on a leveraged position held overnight, a maintenance window, a
// settlement convention one exchange has and its competitors do not. Building them in means the simulator carrying
// every venue's idiosyncrasies forever, and leaving them out means a backtest that is quietly wrong about the venue
// it claims to model.
//
// So a behaviour is added to a run instead. Two things are pinned here.
//
// WHAT A BEHAVIOUR MAY DO, which is deliberately almost nothing: see the clock, read positions and prices, and move
// money with a reason. It cannot fill, cancel or change a position - a behaviour that needed those is the matching
// engine, and the matching engine is where the whole suite tests it. That boundary is worth a test of its own,
// because the cost of losing it is a venue quirk quietly rewriting fills where nothing is watching.
//
// AND THAT A RUN REPORTS WHAT ACTUALLY HAPPENED. A run configured for a behaviour whose boundary its data never
// crossed did not apply it, and a result that claimed otherwise would be the same lie the Applied list exists to
// prevent.
public sealed class SimulationModuleTests
{
    /// <summary>22:00 UTC, the rollover most venues inherit from the New York close.</summary>
    private static readonly TimeSpan _rollover = TimeSpan.FromHours(22);

    private const long OneSecond = 1_000;
    private const long JustAfterRollover = (22 * 60 * 60 * 1_000) + 1_000;
    private const long ThreeDaysLater = (3 * 24 * 60 * 60 * 1_000) + 1_000;

    /// <summary>A venue that charges nothing to trade, so what is measured is the behaviour's own charge.</summary>
    private static SimOptions WithRollover(decimal longRate, decimal shortRate) => new()
    {
        FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        Modules = [new RolloverInterestModule(_rollover, longRate, shortRate)],
    };

    // ----- what the behaviour does -----

    [Fact]
    public void A_position_held_across_the_rollover_is_charged_interest_on_it()
    {
        // One contract at 50 000 at two basis points: 10 USDT out of the account.
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        ModuleCharge charge = Assert.Single(sim.Exchange.ModuleCharges);
        Assert.Equal(SimulationCapabilities.RolloverInterest, charge.Module);
        Assert.Equal(new Money(-10m, Currencies.USDT), charge.Amount);
        Assert.Contains("rollover interest", charge.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_is_charged_its_own_rate_rather_than_the_long_one_negated()
    {
        // The difference from funding, and the reason both rates are given rather than derived. Funding is paid by
        // one side to the other, so one rate describes it. Interest is the cost of the money behind the position and
        // both sides borrow - long borrows the quote currency, short borrows the base - so both normally pay, and a
        // simulator that negated one rate to get the other would produce a confident number that is simply wrong.
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0005m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(new Money(-25m, Currencies.USDT), Assert.Single(sim.Exchange.ModuleCharges).Amount);
    }

    [Fact]
    public void A_negative_rate_pays_the_position_for_being_held()
    {
        using SimHarness sim = SimHarness.Perp(WithRollover(0m, -0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(new Money(10m, Currencies.USDT), Assert.Single(sim.Exchange.ModuleCharges).Amount);
    }

    [Fact]
    public void Interest_is_charged_at_the_price_that_was_in_force_at_the_rollover()
    {
        // Subtle and worth pinning. The rollover is a moment between two ticks, and the crossing is only discovered
        // when the next tick arrives - here a quote at 60 000. The price in force AT the rollover was still 50 000:
        // 60 000 is a price that did not exist yet. So a behaviour is shown the venue as it stood before the tick
        // that revealed the crossing, and charges on 50 000.
        //
        // Charging on 60 000 instead would make every charge depend on the first print after the boundary, which on
        // thin data can be hours later and a long way away - a cost that moves with the data's gaps rather than with
        // what was held.
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 60_000.0m, 60_000.0m)
            .Run();

        Assert.Equal(new Money(-10m, Currencies.USDT), Assert.Single(sim.Exchange.ModuleCharges).Amount);
    }

    [Fact]
    public void A_position_closed_before_the_rollover_is_not_charged()
    {
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(OneSecond + 700, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Empty(sim.Exchange.ModuleCharges);
    }

    [Fact]
    public void Nothing_is_charged_for_the_rollover_a_run_started_after()
    {
        // The first sight of the clock charges nothing. A run whose data begins after the rollover has held nothing
        // across it, and charging there would bill a position for a day before it was opened - which is invisible in
        // a result, because it looks exactly like a slightly worse strategy.
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .At(JustAfterRollover + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover + 1_000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Empty(sim.Exchange.ModuleCharges);
    }

    [Fact]
    public void A_gap_in_the_data_is_not_a_free_ride_across_the_rollovers_it_skipped()
    {
        // The case that makes tracking the last day charged, rather than the last time the behaviour was called, the
        // difference between right and wrong. Bars an hour apart, a venue outage, a weekend with no prints: the
        // position was held across every rollover in the gap whether or not any data arrived to say so. Charging
        // once for a three-day jump would make a strategy that holds through thin data look cheaper than one that
        // holds through liquid data, which is a bias nobody would ever see in a result.
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(ThreeDaysLater, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(3, sim.Exchange.ModuleCharges.Count);
        Assert.All(sim.Exchange.ModuleCharges, c => Assert.Equal(new Money(-10m, Currencies.USDT), c.Amount));
    }

    [Fact]
    public void A_behaviour_charging_nothing_is_not_recorded_as_having_charged()
    {
        // A zero-rate behaviour is configured and never does anything. Recording the charge anyway would make the
        // run report the behaviour as applied, which is the claim the Applied list exists to stop.
        using SimHarness sim = SimHarness.Perp(WithRollover(0m, 0m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Empty(sim.Exchange.ModuleCharges);
        Assert.DoesNotContain(SimulationCapabilities.RolloverInterest, sim.Exchange.Applied);
    }

    // ----- and what a run says about it -----

    [Fact]
    public void A_configured_behaviour_is_what_the_venue_can_do_and_a_charge_is_what_it_did()
    {
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Contains(SimulationCapabilities.RolloverInterest, SimulationCapabilities.Of(sim.Exchange.Config));
        Assert.Contains(SimulationCapabilities.RolloverInterest, sim.Exchange.Applied);
    }

    [Fact]
    public void A_behaviour_that_never_crossed_its_boundary_is_offered_and_not_claimed()
    {
        // The pair that matters to whoever reads a result: the venue could have charged interest, and in this run it
        // did not, because the data never reached a rollover.
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(OneSecond + 2_000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Contains(SimulationCapabilities.RolloverInterest, SimulationCapabilities.Of(sim.Exchange.Config));
        Assert.DoesNotContain(SimulationCapabilities.RolloverInterest, sim.Exchange.Applied);
    }

    [Fact]
    public void Every_charge_reaches_the_report()
    {
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        BacktestResult result = BacktestResult.From(sim.Engine);
        ModuleChargeReportRow row = Assert.Single(result.ModuleCharges);

        Assert.Equal(SimulationCapabilities.RolloverInterest, row.Module);
        Assert.Equal(-10m, row.Amount);
        Assert.Equal(Currencies.USDT, row.Currency);
        Assert.Contains(SimulationCapabilities.RolloverInterest, result.Applied);
    }

    [Fact]
    public void A_charge_moves_the_balance_and_not_only_the_report()
    {
        using SimHarness sim = SimHarness.Perp(WithRollover(0.0002m, 0.0002m));
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(100_000m - 10m, sim.Balance(Currencies.USDT));
    }

    // ----- the boundary a behaviour may not cross -----

    [Fact]
    public void A_behaviour_is_given_no_way_to_trade()
    {
        // The guard on the extension point rather than on any behaviour. Everything a module can reach is on this
        // one interface, so widening it is the only way a venue quirk could start filling orders or moving positions
        // - and that would put trading decisions somewhere the matching engine's tests never look. A new member here
        // fails this test, which is the point: adding one should be a decision, not a convenience.
        string[] reads =
        [
            nameof(ISimulatedVenueState.Venue),
            nameof(ISimulatedVenueState.Log),
            nameof(ISimulatedVenueState.OpenPositions),
            nameof(ISimulatedVenueState.Instrument),
            nameof(ISimulatedVenueState.Price),
            nameof(ISimulatedVenueState.Notional),
        ];

        // The only thing a behaviour may DO. Anything else added here can act on a run, and this is the line.
        string[] writes = [nameof(ISimulatedVenueState.Charge)];

        string[] actual =
        [
            .. typeof(ISimulatedVenueState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
            .. typeof(ISimulatedVenueState).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name),
        ];

        Assert.Equal(
            reads.Concat(writes).Order(StringComparer.Ordinal),
            actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_behaviour_sees_the_clock_move_forwards_only()
    {
        // A run interleaving instruments, or replaying a book and a quote at the same instant, can offer a timestamp
        // no later than one already seen. A behaviour charging for elapsed time would bill that twice, and it cannot
        // defend itself: it has no way to know the run went back.
        Spy spy = new();
        using SimHarness sim = SimHarness.Perp(new SimOptions
        {
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            Modules = [spy],
        });

        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .Trade(OneSecond, 50_000.0m)
            .Quote(OneSecond + 1, 50_000.1m, 50_000.1m)
            .Bar(60_000, 50_000m, 50_100m, 49_900m, 50_000m)
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        Assert.NotEmpty(spy.Seen);
        Assert.Equal(spy.Seen.Order().ToArray(), spy.Seen.ToArray());
        Assert.Equal(spy.Seen.Distinct().Count(), spy.Seen.Count);
    }

    /// <summary>Records every time it is shown, and charges nothing.</summary>
    private sealed class Spy : ISimulationModule
    {
        public List<long> Seen { get; } = [];

        public string Name => "spy";

        public void OnTime(UnixNanos now, ISimulatedVenueState venue) => Seen.Add(now.Value);
    }

    [Fact]
    public void A_behaviour_is_told_when_there_is_no_price_to_value_a_position_at_rather_than_being_given_a_zero()
    {
        // A notional of zero would charge nothing and look exactly like a rate of zero. Null says the venue has seen
        // no price, which is a different thing and the only one a behaviour can react to.
        Probe probe = new();
        using SimHarness sim = SimHarness.Perp(new SimOptions
        {
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            Modules = [probe],
        });

        // A bar arrives, so the clock moves and the behaviour is shown a venue that has never seen a price for the
        // instrument it is asked about.
        sim.Bar(60_000, 50_000m, 50_100m, 49_900m, 50_000m).Run();

        Assert.Null(probe.NotionalOfSomethingUnknown);
        Assert.Null(probe.PriceOfSomethingUnknown);
        Assert.Null(probe.UnknownInstrument);
    }

    /// <summary>Asks about an instrument this venue has never been given.</summary>
    private sealed class Probe : ISimulationModule
    {
        private static readonly InstrumentId _absent = InstrumentId.Parse("NOTHING-USDT.SIM");

        public decimal? NotionalOfSomethingUnknown { get; private set; }

        public decimal? PriceOfSomethingUnknown { get; private set; }

        public Instrument? UnknownInstrument { get; private set; }

        public string Name => "probe";

        public void OnTime(UnixNanos now, ISimulatedVenueState venue)
        {
            ArgumentNullException.ThrowIfNull(venue);
            NotionalOfSomethingUnknown = venue.Notional(_absent);
            PriceOfSomethingUnknown = venue.Price(_absent);
            UnknownInstrument = venue.Instrument(_absent);
            venue.Log.LogDebug("probe ran at {Now}", now.Value);
        }
    }

    // ----- the notional a charge is taken on -----

    [Fact]
    public void An_inverse_position_is_valued_the_way_the_venue_values_it()
    {
        // 10 000 USD of contracts at 50 000 is 0.2 BTC, and interest is charged in BTC because that is what the
        // contract settles in. Valuing it as a linear contract would give 10 000 x 50 000 - wrong by the square of
        // the price, in the wrong currency, and with nothing in a result to suggest it.
        using SimHarness sim = SimHarness.For(TestInstruments.InversePerp(), new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(1m, Currencies.BTC)],
            DefaultLeverage = 20m,
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.BTC)),
            Liquidate = false,
            Modules = [new RolloverInterestModule(_rollover, 0.0002m, 0.0002m)],
        });

        // Enough on offer to fill 10 000 contracts: a fill bounded by the size at the touch would be measuring the
        // bound rather than the notional.
        sim.Quote(OneSecond, 50_000.0m, 50_000.0m, size: 100_000m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(10_000m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m, size: 100_000m)
            .Run();

        // 0.2 BTC at two basis points.
        Assert.Equal(new Money(-0.00004m, Currencies.BTC), Assert.Single(sim.Exchange.ModuleCharges).Amount);
    }

    [Fact]
    public void A_contract_worth_more_than_one_unit_is_valued_by_its_multiplier()
    {
        // A contract is a multiple of the instrument's own unit on plenty of venues, and a notional that ignored it
        // would charge a tenth of what it should here while looking entirely plausible.
        CryptoPerpetual tenPerContract = new(new InstrumentSpec
        {
            Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), TestInstruments.Sim),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.001m, 3),
            Multiplier = new Quantity(10m, 0),
            MarginInit = 0.05m,
            MarginMaint = 0.025m,
            MakerFee = 0m,
            TakerFee = 0m,
        });

        using SimHarness sim = SimHarness.For(tenPerContract, new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT)],
            DefaultLeverage = 20m,
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            Liquidate = false,
            Modules = [new RolloverInterestModule(_rollover, 0.0002m, 0.0002m)],
        });

        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .At(OneSecond + 500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(JustAfterRollover, 50_000.0m, 50_000.0m)
            .Run();

        // One contract of ten units at 50 000 is 500 000 of notional, at two basis points.
        Assert.Equal(new Money(-100m, Currencies.USDT), Assert.Single(sim.Exchange.ModuleCharges).Amount);
    }

    // ----- a behaviour that moves nothing -----

    [Fact]
    public void A_behaviour_that_charges_zero_on_purpose_is_not_recorded_as_having_charged()
    {
        // Reached through the charge itself rather than through a rate of zero, which the rollover module declines
        // earlier. A behaviour is entitled to compute a charge that comes to nothing - a rate that rounds away, a
        // position too small to bill - and a run must not then report the behaviour as applied.
        using SimHarness sim = SimHarness.Perp(new SimOptions
        {
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            Modules = [new ChargesNothing()],
        });

        sim.Quote(OneSecond, 50_000.0m, 50_000.0m)
            .Quote(OneSecond + 1_000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Empty(sim.Exchange.ModuleCharges);
        Assert.DoesNotContain("chargesNothing", sim.Exchange.Applied);
        Assert.Equal(100_000m, sim.Balance(Currencies.USDT));
    }

    /// <summary>Charges every time it is shown the clock, and always for nothing.</summary>
    private sealed class ChargesNothing : ISimulationModule
    {
        public string Name => "chargesNothing";

        public void OnTime(UnixNanos now, ISimulatedVenueState venue)
        {
            ArgumentNullException.ThrowIfNull(venue);
            venue.Charge(Name, Money.Zero(Currencies.USDT), "nothing to charge", now);
        }
    }

    // ----- the first rollover a run sees -----

    [Fact]
    public void The_rollover_a_run_starts_after_is_not_charged_even_with_a_position_already_open()
    {
        // The guard that no backtest can reach: a fresh run has no position at its first data point, so nothing
        // could be billed there whether the guard is present or not. A resumed run does - it starts holding what it
        // held - and so does anything else that drives the simulator with positions in place. The module is asked
        // directly here, because that is the only way this branch is reachable, and without it a resumed run would
        // be charged for a rollover it had already paid.
        FakeVenue venue = new();
        RolloverInterestModule module = new(_rollover, 0.0002m, 0.0002m);

        // First sight: an hour after the rollover, holding a position.
        module.OnTime(At(1, 23, 0), venue);
        Assert.Empty(venue.Charges);

        // Later the same day: still nothing, the rollover has not come round again.
        module.OnTime(At(1, 23, 30), venue);
        Assert.Empty(venue.Charges);

        // And the next one is charged, once.
        module.OnTime(At(2, 22, 1), venue);
        Assert.Single(venue.Charges);
    }

    private static UnixNanos At(int day, int hour, int minute) =>
        UnixNanos.FromDateTimeOffset(new DateTimeOffset(2025, 1, day, hour, minute, 0, TimeSpan.Zero));

    /// <summary>A venue holding one position at a known price, from the first moment it is asked.</summary>
    private sealed class FakeVenue : ISimulatedVenueState
    {
        private static readonly Instrument _instrument = TestInstruments.Perp();

        public List<Money> Charges { get; } = [];

        public Venue Venue => TestInstruments.Sim;

        public ILogger Log => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IReadOnlyList<(InstrumentId InstrumentId, decimal SignedQuantity)> OpenPositions =>
            [(_instrument.Id, 1m)];

        public Instrument? Instrument(InstrumentId instrumentId) => _instrument;

        public decimal? Price(InstrumentId instrumentId) => 50_000m;

        public decimal? Notional(InstrumentId instrumentId) => 50_000m;

        public void Charge(string module, Money amount, string reason, UnixNanos ts) => Charges.Add(amount);
    }
}
