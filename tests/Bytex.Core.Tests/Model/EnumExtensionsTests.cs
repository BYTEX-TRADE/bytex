using Bytex.Core.Model;

namespace Bytex.Core.Tests.Model;

// The enum helpers decide which side closes a position and which order types wait for a trigger.
// A wrong mapping here silently sends orders in the wrong direction, so each mapping is pinned.
public class EnumExtensionsTests
{
    [Fact]
    public void Opposite_swaps_buy_and_sell()
    {
        Assert.Equal(OrderSide.Sell, OrderSide.Buy.Opposite());
        Assert.Equal(OrderSide.Buy, OrderSide.Sell.Opposite());
    }

    [Fact]
    public void Buying_opens_a_long_and_selling_opens_a_short()
    {
        Assert.Equal(PositionSide.Long, OrderSide.Buy.ToPositionSide());
        Assert.Equal(PositionSide.Short, OrderSide.Sell.ToPositionSide());
    }

    [Fact]
    public void A_long_is_entered_by_buying_and_closed_by_selling()
    {
        Assert.Equal(OrderSide.Buy, PositionSide.Long.ToOrderSide());
        Assert.Equal(OrderSide.Sell, PositionSide.Long.ClosingSide());
    }

    [Fact]
    public void A_short_is_entered_by_selling_and_closed_by_buying()
    {
        Assert.Equal(OrderSide.Sell, PositionSide.Short.ToOrderSide());
        Assert.Equal(OrderSide.Buy, PositionSide.Short.ClosingSide());
    }

    [Fact]
    public void A_flat_position_has_no_entry_or_closing_side()
    {
        Assert.Throws<ArgumentException>(() => PositionSide.Flat.ToOrderSide());
        Assert.Throws<ArgumentException>(() => PositionSide.Flat.ClosingSide());
    }

    [Fact]
    public void The_aggressor_of_a_buy_is_the_buyer()
    {
        Assert.Equal(AggressorSide.Buyer, OrderSide.Buy.ToAggressorSide());
        Assert.Equal(AggressorSide.Seller, OrderSide.Sell.ToAggressorSide());
    }

    [Theory]
    [InlineData(BarAggregation.Tick, false)]
    [InlineData(BarAggregation.TickImbalance, false)]
    [InlineData(BarAggregation.Volume, false)]
    [InlineData(BarAggregation.VolumeImbalance, false)]
    [InlineData(BarAggregation.Value, false)]
    [InlineData(BarAggregation.ValueImbalance, false)]
    [InlineData(BarAggregation.Millisecond, true)]
    [InlineData(BarAggregation.Second, true)]
    [InlineData(BarAggregation.Minute, true)]
    [InlineData(BarAggregation.Hour, true)]
    [InlineData(BarAggregation.Day, true)]
    [InlineData(BarAggregation.Week, true)]
    [InlineData(BarAggregation.Month, true)]
    public void IsTimeBased_is_true_only_for_clock_driven_aggregations(BarAggregation aggregation, bool expected)
    {
        Assert.Equal(expected, aggregation.IsTimeBased());
    }

    [Theory]
    [InlineData(OrderType.Market, false, false)]
    [InlineData(OrderType.Limit, false, true)]
    [InlineData(OrderType.StopMarket, true, false)]
    [InlineData(OrderType.StopLimit, true, true)]
    [InlineData(OrderType.MarketIfTouched, true, false)]
    [InlineData(OrderType.LimitIfTouched, true, true)]
    [InlineData(OrderType.TrailingStopMarket, true, false)]
    [InlineData(OrderType.TrailingStopLimit, true, true)]
    [InlineData(OrderType.MarketToLimit, false, false)]
    public void Order_types_are_classified_by_trigger_and_limit_price(OrderType type, bool isConditional, bool hasLimitPrice)
    {
        Assert.Equal(isConditional, type.IsConditional());
        Assert.Equal(hasLimitPrice, type.HasLimitPrice());
    }
}
