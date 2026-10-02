using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Adapters.Tests;

// Why: an order sized in the QUOTE currency - "buy 100 USDT of BTC" - carries that 100 in the same field as a base
// quantity, and it is only safe where the venue has a field that means quote. Three venues have one for exactly one
// order shape each and sent the number anyway everywhere else: Binance's quoteOrderQty is spot MARKET only, Bybit's
// marketUnit is spot MARKET only, KuCoin's funds is on its market branch only. Every other combination put a quote
// amount in the base-size field and sent it.
//
// What that does: "buy 100 USDT of BTC" leaves as "buy 100 BTC", about twenty-five thousand times the intended size.
// A venue usually refuses that for want of balance, which is luck rather than safety - on an instrument priced below
// one it FILLS: "buy 100 USDT of DOGE" at 0.10 buys 100 DOGE, ten dollars, and nothing anywhere says the size was not
// the one asked for. Nothing upstream catches it either, because the risk engine reads the quantity as quote and
// approves the size that was meant.
//
// Five venues already refuse by name where they cannot express it - Gate spot and futures, Hyperliquid, Kraken,
// Bitget futures, KuCoin futures - so this is the shape being made consistent rather than invented.
public sealed class QuoteQuantityTests
{
    private static OrderRejected Rejected(BinanceExecRig rig) =>
        Assert.IsType<OrderRejected>(rig.Sink.AllOrderEvents.Last());

    private static void RefusedByName(string reason)
    {
        Assert.Contains("quote", reason, StringComparison.OrdinalIgnoreCase);

        // It has to say what WOULD work, or the reader is left with a refusal and no next step.
        Assert.True(
            reason.Contains("market", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("base", StringComparison.OrdinalIgnoreCase),
            $"a refusal has to say what the venue can take instead: {reason}");
    }

    // ----- Binance: quoteOrderQty is spot MARKET and nothing else -----

    [Fact]
    public async Task Binance_refuses_a_quote_quantity_on_a_futures_market_order()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        rig.Routes.On("POST", "/fapi/v1/order", """{"orderId":1,"status":"NEW"}""");
        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(100m), quoteQuantity: true);

        await rig.SubmitAsync(order);

        RefusedByName(Rejected(rig).Reason);
        Assert.Empty(rig.Server.RequestsTo("/fapi/v1/order"));
    }

    [Fact]
    public async Task Binance_refuses_a_quote_quantity_on_a_spot_limit_order()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");
        LimitOrder order = rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(100m), rig.Px(50_000m), quoteQuantity: true);

        await rig.SubmitAsync(order);

        RefusedByName(Rejected(rig).Reason);
        Assert.Empty(rig.Server.RequestsTo("/api/v3/order"));
    }

    /// <summary>The shape this venue really has: a spot market order still goes, under the field that means quote.</summary>
    [Fact]
    public async Task Binance_still_sends_a_quote_quantity_on_the_spot_market_order_it_fits()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");
        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(100m), quoteQuantity: true);

        await rig.SubmitAsync(order);

        RecordedRequest sent = rig.Server.RequestsTo("/api/v3/order").Last();
        // The value, not its spelling: this venue is sent the instrument's own precision.
        Assert.Equal(100m, decimal.Parse(sent.Query("quoteOrderQty")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(sent.Query("quantity"));
    }

    /// <summary>And a base quantity is untouched by any of this, on the order type that was never the problem.</summary>
    [Fact]
    public async Task Binance_sends_a_base_quantity_as_a_quantity()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        rig.Routes.On("POST", "/fapi/v1/order", """{"orderId":1,"status":"NEW"}""");
        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m));

        await rig.SubmitAsync(order);

        Assert.Equal(0.5m, decimal.Parse(rig.Server.RequestsTo("/fapi/v1/order").Last().Query("quantity")!, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ----- Bybit: marketUnit is spot MARKET and nothing else -----

    [Fact]
    public async Task Bybit_refuses_a_quote_quantity_on_a_linear_market_order()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes.On("POST", "/v5/order/create", """{"retCode":0,"retMsg":"OK","result":{"orderId":"1"}}""");
        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(100m), quoteQuantity: true);

        await rig.SubmitAsync(order);

        OrderRejected rejected = Assert.IsType<OrderRejected>(rig.Sink.AllOrderEvents.Last());
        RefusedByName(rejected.Reason);
        Assert.Empty(rig.Server.RequestsTo("/v5/order/create"));
    }

    [Fact]
    public async Task Bybit_refuses_a_quote_quantity_on_a_spot_limit_order()
    {
        await using BybitExecRig rig = new(BybitProductType.Spot);
        rig.Routes.On("POST", "/v5/order/create", """{"retCode":0,"retMsg":"OK","result":{"orderId":"1"}}""");
        LimitOrder order = rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(100m), rig.Px(50_000m), quoteQuantity: true);

        await rig.SubmitAsync(order);

        RefusedByName(Assert.IsType<OrderRejected>(rig.Sink.AllOrderEvents.Last()).Reason);
        Assert.Empty(rig.Server.RequestsTo("/v5/order/create"));
    }
}
