using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Trading;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Trading;

// Why: a document strategy took its environment from its own payload and defaulted to Backtest. Every test here runs
// a backtest, so the wrong default was the right answer in every test that existed, and a document under a live node
// followed backtest rules while the node knew perfectly well what it was. A default that matches the only
// environment the tests use is a default no test can question.
//
// So every rule that differs by environment is exercised in ALL of them, from a kernel configured that way rather
// than from an object constructed with it.
public sealed class EnvironmentRulesTests
{
    [Theory]
    [InlineData(TradingEnvironment.Backtest)]
    [InlineData(TradingEnvironment.Sandbox)]
    [InlineData(TradingEnvironment.Live)]
    public void Whatever_the_kernel_is_configured_as_is_what_its_strategies_are_told(TradingEnvironment environment)
    {
        using KernelHarness h = new(environment: environment);

        ProbeStrategy strategy = h.StartWithStrategy();

        Assert.Equal(environment, strategy.RunningIn);
    }

    [Theory]
    [InlineData(TradingEnvironment.Backtest)]
    [InlineData(TradingEnvironment.Sandbox)]
    [InlineData(TradingEnvironment.Live)]
    public void An_actor_is_told_as_well_as_a_strategy(TradingEnvironment environment)
    {
        // Actors read market data and can place orders through the same engines, so a rule that differs by
        // environment differs for them too.
        using KernelHarness h = new(environment: environment);
        ProbeActor actor = new(new ActorConfig { ActorId = new ActorId("A-1") });

        h.Kernel.Trader.AddActor(actor);

        Assert.Equal(environment, actor.RunningIn);
    }

    [Fact]
    public void A_strategy_nobody_registered_assumes_the_safe_answer()
    {
        // Constructed and never given to a trader: it has not been told, and assuming live would be the dangerous
        // way round to be wrong.
        ProbeStrategy loose = new(new StrategyConfig { StrategyId = new StrategyId("S-LOOSE") });

        Assert.Equal(TradingEnvironment.Backtest, loose.RunningIn);
    }
}
