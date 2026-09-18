using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: the simulated venue claims native support for contingent lists. A bracket must keep its exits dormant until
// the entry fills, and the first exit to fill must take the other one out of the book.
public sealed class ContingentOrderTests
{
    [Fact]
    public void Bracket_with_a_market_entry_fills_the_entry_and_puts_both_exits_to_work()
    {
        using SimHarness sim = SimHarness.Spot();
        OrderList? bracket = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => bracket = s.SubmitList(s.Orders.BracketOrder(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m), sim.Px(110.00m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Order entry = bracket!.Orders[0];
        Order stopLoss = bracket.Orders[1];
        Order takeProfit = bracket.Orders[2];
        Assert.Equal(sim.Px(100.10m), sim.SingleFill(entry).LastPx);
        Assert.Equal(OrderStatus.Accepted, stopLoss.Status);
        Assert.Equal(OrderStatus.Accepted, takeProfit.Status);
        Assert.Equal(2, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Take_profit_fill_cancels_the_stop_loss_of_the_same_bracket()
    {
        using SimHarness sim = SimHarness.Spot();
        OrderList? bracket = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => bracket = s.SubmitList(s.Orders.BracketOrder(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m), sim.Px(110.00m))))
            .Quote(2000, 110.50m, 110.60m)
            .Quote(3000, 94.00m, 94.10m) // would have fired the stop had it still been working
            .Run();

        Order stopLoss = bracket!.Orders[1];
        Order takeProfit = bracket.Orders[2];
        OrderFilled fill = sim.SingleFill(takeProfit);
        Assert.Equal(sim.Px(110.00m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(OrderStatus.Canceled, stopLoss.Status);
        Assert.Equal(Scripted.Ms(2000), stopLoss.TsClosed);
        Assert.Equal(0m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void Stop_loss_fill_cancels_the_take_profit_of_the_same_bracket()
    {
        using SimHarness sim = SimHarness.Spot();
        OrderList? bracket = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => bracket = s.SubmitList(s.Orders.BracketOrder(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m), sim.Px(110.00m))))
            .Quote(2000, 94.80m, 94.90m)
            .Quote(3000, 111.00m, 111.10m)
            .Run();

        Order stopLoss = bracket!.Orders[1];
        Order takeProfit = bracket.Orders[2];
        Assert.Equal(sim.Px(94.80m), sim.SingleFill(stopLoss).LastPx);
        Assert.Equal(OrderStatus.Canceled, takeProfit.Status);
        Assert.Empty(sim.Fills(takeProfit));
    }

    [Fact]
    public void Exits_of_a_bracket_with_a_resting_limit_entry_stay_dormant_until_the_entry_fills()
    {
        // Entry: buy limit 99.00. Stop-loss 95.00, take-profit 101.00. Before the entry fills the market visits 101.50;
        // a live take-profit (sell limit 101.00) would have sold there.
        using SimHarness sim = SimHarness.Spot();
        OrderList? bracket = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => bracket = s.SubmitList(s.Orders.BracketOrder(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m), sim.Px(101.00m), entryPrice: sim.Px(99.00m), entryType: OrderType.Limit)))
            .Quote(2000, 101.50m, 101.60m)
            .At(2500, s => s.Note($"fills {bracket!.Orders.Sum(o => o.Events.OfType<OrderFilled>().Count())}"))
            .Quote(3000, 98.80m, 98.90m) // entry fills at 99.00
            .Quote(4000, 101.20m, 101.30m) // take-profit is now live and fills at 101.00
            .Run();

        Assert.Contains("2500|fills 0", sim.Strategy.Journal);
        Assert.Equal(sim.Px(99.00m), sim.SingleFill(bracket!.Orders[0]).LastPx);
        OrderFilled exit = sim.SingleFill(bracket.Orders[2]);
        Assert.Equal(sim.Px(101.00m), exit.LastPx);
        Assert.Equal(Scripted.Ms(4000), exit.TsEvent);
        Assert.Equal(OrderStatus.Canceled, bracket.Orders[1].Status);
    }

    [Fact]
    public void Cancelling_the_unfilled_entry_cancels_its_dormant_exits()
    {
        using SimHarness sim = SimHarness.Spot();
        OrderList? bracket = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => bracket = s.SubmitList(s.Orders.BracketOrder(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m), sim.Px(101.00m), entryPrice: sim.Px(99.00m), entryType: OrderType.Limit)))
            .At(1600, s => s.Cancel(bracket!.Orders[0]))
            .Quote(2000, 94.00m, 94.10m)
            .Run();

        Assert.All(bracket!.Orders, o => Assert.Equal(OrderStatus.Canceled, o.Status));
        Assert.All(bracket.Orders, o => Assert.Empty(sim.Fills(o)));
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Fact(Skip = "BUG: a dormant OTO child is accepted when the list arrives and submitted and accepted again, with a second venue order id, when the parent fills")]
    public void Dormant_exit_is_announced_to_the_strategy_as_accepted_exactly_once()
    {
        using SimHarness sim = SimHarness.Spot();
        OrderList? bracket = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => bracket = s.SubmitList(s.Orders.BracketOrder(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m), sim.Px(101.00m), entryPrice: sim.Px(99.00m), entryType: OrderType.Limit)))
            .Quote(3000, 98.80m, 98.90m)
            .Run();

        Order stopLoss = bracket!.Orders[1];
        List<OrderAccepted> accepted = sim.Events.OfType<OrderAccepted>().Where(e => e.ClientOrderId == stopLoss.ClientOrderId).ToList();
        Assert.Single(accepted);
    }

    [Fact]
    public void One_cancels_other_pair_cancels_the_survivor_when_either_order_fills()
    {
        using SimHarness sim = SimHarness.Spot();
        OrderList? pair = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => pair = s.SubmitList(Contingent.SellExitPair(s, sim.Id, sim.Qty(1m), sim.Px(101.00m), sim.Px(99.00m))))
            .Quote(2000, 98.90m, 99.00m) // the stop fires at the 98.90 bid
            .Quote(3000, 101.50m, 101.60m)
            .Run();

        Assert.Equal(OrderStatus.Canceled, pair!.Orders[0].Status);
        Assert.Equal(sim.Px(98.90m), sim.SingleFill(pair.Orders[1]).LastPx);
    }

    [Fact]
    public void Venue_without_contingent_order_support_leaves_the_linked_order_working()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { SupportContingentOrders = false });
        OrderList? pair = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => pair = s.SubmitList(Contingent.SellExitPair(s, sim.Id, sim.Qty(1m), sim.Px(101.00m), sim.Px(99.00m))))
            .Quote(2000, 98.90m, 99.00m)
            .Quote(3000, 101.50m, 101.60m)
            .Run();

        Assert.Equal(OrderStatus.Filled, pair!.Orders[1].Status);
        Assert.Equal(sim.Px(101.00m), sim.SingleFill(pair.Orders[0]).LastPx); // nobody cancelled it, so it traded too
    }
}
