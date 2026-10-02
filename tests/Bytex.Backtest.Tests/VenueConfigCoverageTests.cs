using System.Reflection;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: a run's description is how anybody who is not writing C# configures a venue, and eight of the simulated venue's
// settings had no member there at all. That is the same defect as a setting nothing reads, turned around: the value
// could not be stated rather than being stated and ignored - and `liquidate` proved how quiet it is, because the
// engine's own tests wrote it, it was dropped, and they passed on the venue's default.
//
// So this holds the two types to each other. A setting that exists on a simulated venue is either expressible in a
// description or listed below with the reason it cannot be, and what a description says reaches the venue rather than
// being carried and dropped on the way.
public sealed class VenueConfigCoverageTests
{
    /// <summary>
    /// What a file genuinely cannot carry, each for a reason about the thing rather than about effort. The list is
    /// short on purpose: it is the escape hatch, and every entry is a claim somebody can check.
    /// </summary>
    private static readonly Dictionary<string, string> _notExpressible = new(StringComparer.Ordinal)
    {
        ["Venue"] = "the venue's own name, which a description carries as a string and converts",
        ["StartingBalances"] = "carried as strings a description parses into money",
        ["BaseCurrency"] = "carried as a currency code",
        ["Leverages"] = "per instrument, and no run has yet needed to state one; DefaultLeverage covers the case that has",
        ["FillModel"] = "built from the probabilities and the seed a description does carry",
        ["LatencyModel"] = "built from the latency a description does carry",
        ["Session"] = "built from SessionEndUtc, which a description carries",
        ["MarginModel"] = "built from MarginModelConfig, which a description carries",
        ["Modules"] = "built from SimulationModuleConfig by name, which a description carries",
        ["FeeModel"] = "built from FeeModelConfig, which a description carries",
        ["OmsType"] = "carried directly",
    };

    [Fact]
    public void Every_setting_of_a_simulated_venue_can_be_stated_in_a_runs_description()
    {
        string[] described = [.. typeof(BacktestVenueConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)];
        List<string> missing = [];

        foreach (PropertyInfo setting in typeof(SimulatedVenueConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (described.Contains(setting.Name, StringComparer.Ordinal) || _notExpressible.ContainsKey(setting.Name))
            {
                continue;
            }

            missing.Add(setting.Name);
        }

        Assert.True(
            missing.Count == 0,
            $"a simulated venue has {string.Join(", ", missing)} and a run's description cannot state it, so anybody "
            + "not writing C# cannot configure it and will not be told why. Add it to BacktestVenueConfig, or list it "
            + "in this test with the reason a file cannot carry it.");
    }

    /// <summary>
    /// And the other half, which is the one that actually bit: a member a description HAS must reach the venue.
    /// `liquidate` was added to neither, and a description saying false was read by nothing while the run liquidated
    /// anyway. Checked by giving every setting a value that is not its default and reading the venue back.
    /// </summary>
    [Fact]
    public void What_a_description_says_reaches_the_venue()
    {
        BacktestVenueConfig described = new()
        {
            Venue = "SIM",
            AccountType = AccountType.Margin,
            StartingBalances = ["100000 USDT"],
            DefaultLeverage = 7m,
            BarExecution = BarExecutionMode.HighFirst,
            RejectStopOrdersAtMarket = false,
            SessionEndUtc = TimeSpan.FromHours(21),
            MaxBarWalkSteps = 1234,
            Liquidate = false,
            FillSizing = FillSizing.WholeFills,
            BarVolumeShare = 0.25m,
            BookType = BookType.L2,
            RefusesOrderAmends = true,
            SupportContingentOrders = false,
            FeeModel = new FeeModelConfig { Kind = FeeModelConfig.Percent, Rate = 0.001m },
            Modules =
            [
                new SimulationModuleConfig
                {
                    Name = SimulationCapabilities.RolloverInterest,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rolloverTimeOfDay"] = "21:00:00",
                        ["longDailyRate"] = "0.0002",
                        ["shortDailyRate"] = "0.0001",
                    },
                },
            ],
        };

        SimulatedVenueConfig venue = described.ToVenueConfig();

        Assert.Equal(AccountType.Margin, venue.AccountType);
        Assert.Equal(7m, venue.DefaultLeverage);
        Assert.Equal(BarExecutionMode.HighFirst, venue.BarExecution);
        Assert.False(venue.RejectStopOrdersAtMarket);
        Assert.Equal(1234, venue.MaxBarWalkSteps);
        Assert.False(venue.Liquidate);
        Assert.Equal(FillSizing.WholeFills, venue.FillSizing);
        Assert.Equal(0.25m, venue.BarVolumeShare);
        Assert.Equal(BookType.L2, venue.BookType);
        Assert.True(venue.RefusesOrderAmends);
        Assert.False(venue.SupportContingentOrders);
        Assert.IsType<PercentFeeModel>(venue.FeeModel);

        // The behaviour it named, built and carrying its own name into what a run reports it applied.
        ISimulationModule module = Assert.Single(venue.Modules);
        Assert.Equal(SimulationCapabilities.RolloverInterest, module.Name);
        Assert.IsType<RolloverInterestModule>(module);
    }

    /// <summary>
    /// A behaviour nothing can build is refused rather than skipped. A run that quietly dropped one would charge less
    /// than it was told to and then report that it applied nothing, which reads as a strategy that simply did better.
    /// </summary>
    [Fact]
    public void A_behaviour_nothing_can_build_is_refused_by_name()
    {
        BacktestVenueConfig described = new()
        {
            Venue = "SIM",
            StartingBalances = ["100000 USDT"],
            Modules = [new SimulationModuleConfig { Name = "overnightHaircut" }],
        };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(described.ToVenueConfig);

        Assert.Contains("overnightHaircut", refused.Message, StringComparison.Ordinal);
        Assert.Contains(SimulationCapabilities.RolloverInterest, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a behaviour that IS built but is missing a figure says which one, because only the venue knows it and a
    /// default would be a rate somebody else chose for this account.
    /// </summary>
    [Fact]
    public void A_behaviour_missing_a_figure_says_which_one()
    {
        BacktestVenueConfig described = new()
        {
            Venue = "SIM",
            StartingBalances = ["100000 USDT"],
            Modules =
            [
                new SimulationModuleConfig
                {
                    Name = SimulationCapabilities.RolloverInterest,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["rolloverTimeOfDay"] = "21:00:00" },
                },
            ],
        };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(described.ToVenueConfig);

        Assert.Contains("longDailyRate", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Two ways to say what a fill costs, and saying it twice is refused.</b> This tree has both: <c>fees</c>, the
    /// document's own assumption, and <c>feeModel</c>, which names one of the engine's models with its figures. They
    /// arrived from different directions and both become the venue's fee model.
    ///
    /// <para>
    /// The sync that brought the second one in applied cleanly and left two assignments to the same member, where the
    /// later one wins without a word - commissions in a result that trace to neither setting, which is the shape of
    /// defect the release it came from was about. So both are kept, both at once is refused, and the refusal names
    /// them; neither is deprecated, because a builder sets the one and a hand-written run sets the other.
    /// </para>
    /// </summary>
    [Fact]
    public void Saying_what_a_fill_costs_twice_is_refused_rather_than_letting_one_win()
    {
        BacktestVenueConfig both = new()
        {
            Venue = "SIM",
            StartingBalances = ["100000 USDT"],
            Fees = new Bytex.Core.Model.FeeSettings { Mode = "fixed", PerFill = 1.5m },
            FeeModel = new FeeModelConfig { Kind = FeeModelConfig.Percent, Rate = 0.001m },
        };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(both.ToVenueConfig);

        Assert.Contains("fees", refused.Message, StringComparison.Ordinal);
        Assert.Contains("feeModel", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>And either one alone reaches the venue, which is what keeps the refusal from being a wall.</summary>
    [Fact]
    public void Either_way_of_saying_it_alone_reaches_the_venue()
    {
        BacktestVenueConfig document = new()
        {
            Venue = "SIM",
            StartingBalances = ["100000 USDT"],
            Fees = new Bytex.Core.Model.FeeSettings { Mode = "fixed", PerFill = 1.5m },
        };

        BacktestVenueConfig named = new()
        {
            Venue = "SIM",
            StartingBalances = ["100000 USDT"],
            FeeModel = new FeeModelConfig { Kind = FeeModelConfig.Percent, Rate = 0.001m },
        };

        Assert.NotNull(document.ToVenueConfig().FeeModel);
        Assert.IsType<PercentFeeModel>(named.ToVenueConfig().FeeModel);

        // And neither set is the venue's own default - the instrument's stored rates - rather than no charge at all.
        Assert.Null(new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["100000 USDT"] }.ToVenueConfig().FeeModel);
    }

    /// <summary>The four fee models a description can name all build, and an unknown name is refused.</summary>
    [Theory]
    [InlineData(FeeModelConfig.MakerTaker, typeof(MakerTakerFeeModel))]
    [InlineData(FeeModelConfig.Percent, typeof(PercentFeeModel))]
    public void A_named_fee_model_builds(string kind, Type expected)
    {
        Assert.IsType(expected, new FeeModelConfig { Kind = kind, Rate = 0.001m }.Build());
    }

    [Fact]
    public void A_fee_model_that_needs_an_amount_says_so_and_an_unknown_kind_is_refused()
    {
        Assert.IsType<FixedFeeModel>(new FeeModelConfig { Kind = FeeModelConfig.Fixed, Amount = "1.5 USDT" }.Build());
        Assert.IsType<PerContractFeeModel>(new FeeModelConfig { Kind = FeeModelConfig.PerContract, Amount = "0.5 USDT" }.Build());
        Assert.IsType<PerContractFeeModel>(new FeeModelConfig { Kind = FeeModelConfig.PerContract, Amount = "0.5 USDT", MakerAmount = "0.2 USDT" }.Build());

        Assert.Contains("amount", Assert.Throws<InvalidOperationException>(() => new FeeModelConfig { Kind = FeeModelConfig.Fixed }.Build()).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discount", Assert.Throws<InvalidOperationException>(() => new FeeModelConfig { Kind = "discount" }.Build()).Message, StringComparison.Ordinal);
    }
}
