using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: strategies re-price and pull orders constantly. A modification must take effect against the current book
// (including crossing it), and a cancelled order must never trade afterwards.
public sealed class ModifyAndCancelTests
{
    [Fact]
    public void Modifying_the_price_of_a_resting_order_moves_it_to_the_new_price()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1600, s => s.Modify(order!, price: sim.Px(99.80m)))
            .Quote(2000, 99.60m, 99.70m) // through 99.80 but not through the original 99.00
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(99.80m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal("Initialized,Submitted,Accepted,Updated,Filled", SimHarness.EventNames(order!));
    }

    [Fact]
    public void Modifying_a_resting_order_to_a_crossing_price_fills_it_immediately_as_taker()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1600, s => s.Modify(order!, price: sim.Px(100.30m)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.10m), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(1600), fill.TsEvent);
    }

    [Fact]
    public void Modifying_the_quantity_changes_how_much_is_filled()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1600, s => s.Modify(order!, quantity: sim.Qty(2.5m)))
            .Quote(2000, 98.80m, 98.90m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Qty(2.5m), fill.LastQty);
        Assert.Equal(sim.Px(99.00m), fill.LastPx);
        Assert.Equal(sim.Qty(2.5m), order!.Quantity);
    }

    [Fact]
    public void Modifying_the_trigger_of_a_stop_order_moves_the_level_at_which_it_fires()
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(99.00m))))
            .At(1600, s => s.Modify(order!, triggerPrice: sim.Px(98.00m)))
            .Quote(2000, 98.50m, 98.60m) // would have fired the original 99.00 stop
            .At(2500, s => s.Note("status " + order!.Status))
            .Quote(3000, 97.90m, 98.00m)
            .Run();

        Assert.Contains("2500|status Accepted", sim.Strategy.Journal);
        Assert.Equal(sim.Px(98.00m), order!.TriggerPrice);
        Assert.Equal(sim.Px(97.90m), sim.SingleFill(order).LastPx);
    }

    [Fact]
    public void Modifying_the_trigger_of_a_trailing_stop_restarts_trailing_from_the_new_trigger()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m))) // trigger 99.00
            .At(1600, s => s.Modify(order!, triggerPrice: sim.Px(98.00m)))
            .Quote(2000, 98.50m, 98.60m) // below 99.00, above 98.00
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Equal(sim.Px(98.00m), order.TriggerPrice);
    }

    [Fact]
    public void Post_only_order_cannot_be_modified_into_a_crossing_price()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m), postOnly: true)))
            .At(1600, s => s.Modify(order!, price: sim.Px(100.10m)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderModifyRejected rejected = Assert.Single(order!.Events.OfType<OrderModifyRejected>());
        Assert.Equal("post-only modification would have crossed the book", rejected.Reason);
        Assert.Equal(sim.Px(99.00m), order.Price);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public void Venue_rejects_a_quantity_modification_that_does_not_exceed_the_filled_quantity()
    {
        // Risk checks are bypassed so that the venue's own validation is what answers.
        using SimHarness sim = SimHarness.Spot(new SimOptions { BypassRisk = true });
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1600, s => s.Modify(order!, quantity: sim.Qty(0m)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderModifyRejected rejected = Assert.Single(order!.Events.OfType<OrderModifyRejected>());
        Assert.Equal("new quantity does not exceed filled quantity", rejected.Reason);
        Assert.Equal(sim.Qty(1m), order.Quantity);
    }

    [Fact]
    public void Cancelled_order_leaves_the_book_and_is_not_filled_by_a_later_crossing_quote()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1600, s => s.Cancel(order!))
            .Quote(2000, 98.00m, 98.10m)
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Equal(Scripted.Ms(1600), order.TsClosed);
        Assert.Empty(sim.Fills(order));
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Cancel_all_for_one_side_leaves_the_other_side_working()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? bid1 = null;
        LimitOrder? bid2 = null;
        LimitOrder? offer = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                bid1 = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m)));
                bid2 = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(98.00m)));
                offer = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(101.00m)));
            })
            .At(1600, s => s.CancelAll(sim.Id, OrderSide.Buy))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Canceled, bid1!.Status);
        Assert.Equal(OrderStatus.Canceled, bid2!.Status);
        Assert.Equal(OrderStatus.Accepted, offer!.Status);
        Assert.Equal(1, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Cancel_all_without_a_side_cancels_every_working_order_of_the_instrument_including_stops()
    {
        using SimHarness sim = SimHarness.Spot();
        List<Order> orders = new();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                orders.Add(s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))));
                orders.Add(s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(101.00m))));
                orders.Add(s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(95.00m))));
                orders.Add(s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 2.00m)));
            })
            .At(1600, s => s.CancelAll(sim.Id))
            .Quote(2000, 90.00m, 90.10m)
            .Run();

        Assert.All(orders, o => Assert.Equal(OrderStatus.Canceled, o.Status));
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Batch_cancel_cancels_exactly_the_listed_orders()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? a = null;
        LimitOrder? b = null;
        LimitOrder? c = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                a = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m)));
                b = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(98.00m)));
                c = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(97.00m)));
            })
            .At(1600, s => s.CancelBatch([a!, c!]))
            .Quote(2000, 96.00m, 96.10m)
            .Run();

        Assert.Equal(OrderStatus.Canceled, a!.Status);
        Assert.Equal(OrderStatus.Filled, b!.Status);
        Assert.Equal(OrderStatus.Canceled, c!.Status);
    }

    [Fact]
    public void Mass_status_lists_working_orders_and_the_net_position_as_the_venue_sees_them()
    {
        using SimHarness sim = SimHarness.Perp();
        LimitOrder? resting = null;
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1500, s =>
            {
                s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(2m)));
                resting = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
            })
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Core.Model.Reports.ExecutionMassStatus status = sim.Exchange.GenerateMassStatus();

        Core.Model.Reports.OrderStatusReport orderReport = Assert.Single(status.OrderReports);
        Assert.Equal(resting!.ClientOrderId, orderReport.ClientOrderId);
        Assert.Equal(resting.VenueOrderId, orderReport.VenueOrderId);
        Assert.Equal(sim.Px(49_000.0m), orderReport.Price);
        Core.Model.Reports.PositionStatusReport positionReport = Assert.Single(status.PositionReports);
        Assert.Equal(PositionSide.Short, positionReport.PositionSide);
        Assert.Equal(sim.Qty(2m), positionReport.Quantity);
    }
}
