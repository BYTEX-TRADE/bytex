using Bytex.Core.Model;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests.Support;

/// <summary>
/// A tiny scripted run whose every number can be derived by hand. One perpetual contract per trade, a flat fee of
/// 1 USDT per fill, 1 000 000 USDT starting balance, zero spread except on the last quote.
/// <code>
/// trade 1 (day A): buy 100 -> sell 110 after 60 s    gross +10  net  +8
/// trade 2 (day B): buy 110 -> sell 104 after 120 s   gross  -6  net  -8
/// trade 3 (day C): sell 104 -> buy 100 after 180 s   gross  +4  net  +2
/// trade 4 (day D): buy 100, still open; last quote 103.0 / 103.4   unrealised +3, realised -1 (entry fee)
/// </code>
/// </summary>
public static class StatisticsScenario
{
    public const long Day = 86_400_000L;

    /// <summary>Runs the scenario with the four trades on the given day offsets (0 = 2025-01-01).</summary>
    public static SimHarness Run(int dayA = 0, int dayB = 1, int dayC = 2, int dayD = 3)
    {
        SimHarness sim = SimHarness.Perp(new SimOptions
        {
            FeeModel = new FixedFeeModel(new Money(1m, Currencies.USDT)),
            StartingBalances = [new Money(1_000_000m, Currencies.USDT)],
            RunId = "statistics",
        });

        RoundTrip(sim, dayA * Day, OrderSide.Buy, 100.0m, 110.0m, holdMs: 60_000);
        RoundTrip(sim, dayB * Day, OrderSide.Buy, 110.0m, 104.0m, holdMs: 120_000);
        RoundTrip(sim, dayC * Day, OrderSide.Sell, 104.0m, 100.0m, holdMs: 180_000);

        long d = dayD * Day;
        sim.Quote(d + 1000, 100.0m, 100.0m)
            .At(d + 2000, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(d + 3000, 103.0m, 103.4m);

        return sim.Run();
    }

    private static void RoundTrip(SimHarness sim, long dayStart, OrderSide entry, decimal entryPrice, decimal exitPrice, long holdMs)
    {
        OrderSide exit = entry == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        sim.Quote(dayStart + 1000, entryPrice, entryPrice)
            .At(dayStart + 2000, s => s.Submit(s.Orders.Market(sim.Id, entry, sim.Qty(1m))))
            .Quote(dayStart + 2000 + holdMs - 1000, exitPrice, exitPrice)
            .At(dayStart + 2000 + holdMs, s => s.Submit(s.Orders.Market(sim.Id, exit, sim.Qty(1m))));
    }
}
