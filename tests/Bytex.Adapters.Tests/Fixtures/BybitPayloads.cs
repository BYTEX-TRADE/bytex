namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// Payloads written by hand in the shapes published in the Bybit V5 API documentation.
/// </summary>
internal static class BybitPayloads
{
    public static string Envelope(string result) => "{\"retCode\":0,\"retMsg\":\"OK\",\"result\":" + result + ",\"retExtInfo\":{},\"time\":1700000000000}";

    public static string Error(int code, string message) => $"{{\"retCode\":{code},\"retMsg\":\"{message}\",\"result\":{{}},\"retExtInfo\":{{}},\"time\":1700000000000}}";

    /// <summary>GET /v5/market/instruments-info?category=spot.</summary>
    public static readonly string SpotInstruments = Envelope("""
        {
          "category": "spot",
          "list": [
            {
              "symbol": "BTCUSDT", "baseCoin": "BTC", "quoteCoin": "USDT", "innovation": "0", "status": "Trading", "marginTrading": "utaOnly",
              "lotSizeFilter": { "basePrecision": "0.000001", "quotePrecision": "0.00000001", "minOrderQty": "0.000048", "maxOrderQty": "71.73956243", "minOrderAmt": "1", "maxOrderAmt": "2000000" },
              "priceFilter": { "tickSize": "0.01" },
              "riskParameters": { "limitParameter": "0.03", "marketParameter": "0.03" }
            },
            {
              "symbol": "OLDUSDT", "baseCoin": "OLD", "quoteCoin": "USDT", "innovation": "0", "status": "Closed", "marginTrading": "none",
              "lotSizeFilter": { "basePrecision": "0.01", "quotePrecision": "0.0001", "minOrderQty": "1", "maxOrderQty": "1000", "minOrderAmt": "1", "maxOrderAmt": "1000" },
              "priceFilter": { "tickSize": "0.0001" }
            }
          ]
        }
        """);

    /// <summary>First page of GET /v5/market/instruments-info?category=linear: a perpetual, and a cursor to the next page.</summary>
    public static readonly string LinearInstrumentsPage1 = Envelope("""
        {
          "category": "linear",
          "list": [
            {
              "symbol": "BTCUSDT", "contractType": "LinearPerpetual", "status": "Trading", "baseCoin": "BTC", "quoteCoin": "USDT",
              "launchTime": "1585526400000", "deliveryTime": "0", "deliveryFeeRate": "", "priceScale": "2",
              "leverageFilter": { "minLeverage": "1", "maxLeverage": "100.00", "leverageStep": "0.01" },
              "priceFilter": { "minPrice": "0.10", "maxPrice": "199999.80", "tickSize": "0.10" },
              "lotSizeFilter": { "maxOrderQty": "100.000", "minOrderQty": "0.001", "qtyStep": "0.001", "postOnlyMaxOrderQty": "1000.000", "maxMktOrderQty": "100.000", "minNotionalValue": "5" },
              "unifiedMarginTrade": true, "fundingInterval": 480, "settleCoin": "USDT", "copyTrading": "both"
            }
          ],
          "nextPageCursor": "cursor-page-2"
        }
        """);

    /// <summary>Second page: a dated USDT futures contract; the empty cursor ends the listing.</summary>
    public static readonly string LinearInstrumentsPage2 = Envelope("""
        {
          "category": "linear",
          "list": [
            {
              "symbol": "BTCUSDT-26SEP25", "contractType": "LinearFutures", "status": "Trading", "baseCoin": "BTC", "quoteCoin": "USDT",
              "launchTime": "1742544000000", "deliveryTime": "1758873600000", "deliveryFeeRate": "0.0005", "priceScale": "2",
              "priceFilter": { "minPrice": "0.50", "maxPrice": "1999999.00", "tickSize": "0.50" },
              "lotSizeFilter": { "maxOrderQty": "50.000", "minOrderQty": "0.001", "qtyStep": "0.001", "minNotionalValue": "5" },
              "settleCoin": "USDT"
            }
          ],
          "nextPageCursor": ""
        }
        """);

    /// <summary>
    /// What this venue answered for BTCUSDT's linear risk limits on 2026-09-25, first two tiers. The lowest tier is
    /// the instrument's margin - 0.66 percent initial and 0.33 percent maintenance - and the second is here so the
    /// test proves the tier is chosen rather than the first row taken.
    /// </summary>
    public static readonly string LinearRiskLimits = Envelope("""
        {
          "category": "linear",
          "list": [
            { "id": 1, "symbol": "BTCUSDT", "riskLimitValue": "300000", "maintenanceMargin": "0.0033",
              "initialMargin": "0.0066", "isLowestRisk": 1, "maxLeverage": "150.00", "mmDeduction": "" },
            { "id": 2, "symbol": "BTCUSDT", "riskLimitValue": "2000000", "maintenanceMargin": "0.005",
              "initialMargin": "0.01", "isLowestRisk": 0, "maxLeverage": "100.00", "mmDeduction": "510" }
          ],
          "nextPageCursor": ""
        }
        """);

    public const string TopOfBookSnapshot = """
        {"topic":"orderbook.1.BTCUSDT","type":"snapshot","ts":1672304484978,"data":{"s":"BTCUSDT","b":[["16493.50","0.006"]],"a":[["16611.00","0.029"]],"u":18521288,"seq":7961638724},"cts":1672304484976}
        """;

    /// <summary>The best bid is removed (size 0) and replaced by a lower level; the ask is untouched.</summary>
    public const string TopOfBookDelta = """
        {"topic":"orderbook.1.BTCUSDT","type":"delta","ts":1672304485978,"data":{"s":"BTCUSDT","b":[["16493.50","0"],["16493.00","0.500"]],"a":[],"u":18521289,"seq":7961638725},"cts":1672304485976}
        """;

    public const string BookSnapshot = """
        {"topic":"orderbook.50.BTCUSDT","type":"snapshot","ts":1672304484978,"data":{"s":"BTCUSDT","b":[["16493.50","0.006"],["16493.00","0.100"]],"a":[["16611.00","0.029"]],"u":18521288,"seq":7961638724},"cts":1672304484976}
        """;

    public const string BookDelta = """
        {"topic":"orderbook.50.BTCUSDT","type":"delta","ts":1672304485978,"data":{"s":"BTCUSDT","b":[["16493.50","0"]],"a":[["16611.00","0.129"]],"u":18521289,"seq":7961638725},"cts":1672304485976}
        """;

    public const string PublicTrades = """
        {"topic":"publicTrade.BTCUSDT","type":"snapshot","ts":1672304486868,"data":[
          {"T":1672304486865,"s":"BTCUSDT","S":"Buy","v":"0.001","p":"16578.50","L":"PlusTick","i":"20f43950-d8dd-5b31-9112-a178eb6023af","BT":false},
          {"T":1672304486866,"s":"BTCUSDT","S":"Sell","v":"0.250","p":"16578.00","L":"MinusTick","i":"30f43950-d8dd-5b31-9112-a178eb6023b0","BT":false}]}
        """;

    public const string KlineForming = """
        {"topic":"kline.5.BTCUSDT","data":[{"start":1672324800000,"end":1672325099999,"interval":"5","open":"16649.5","close":"16677","high":"16677","low":"16608","volume":"2.081","turnover":"34666.4005","confirm":false,"timestamp":1672324988882}],"ts":1672324988882,"type":"snapshot"}
        """;

    public const string KlineConfirmed = """
        {"topic":"kline.5.BTCUSDT","data":[{"start":1672324800000,"end":1672325099999,"interval":"5","open":"16649.5","close":"16680.5","high":"16690","low":"16608","volume":"3.500","turnover":"58000.1","confirm":true,"timestamp":1672325100003}],"ts":1672325100003,"type":"snapshot"}
        """;

    public const string TickerSnapshot = """
        {"topic":"tickers.BTCUSDT","type":"snapshot","data":{"symbol":"BTCUSDT","tickDirection":"PlusTick","price24hPcnt":"0.017103","lastPrice":"17216.00","markPrice":"17217.33","indexPrice":"17227.36","openInterest":"68744.761","nextFundingTime":"1673280000000","fundingRate":"-0.000212","bid1Price":"17215.50","bid1Size":"84.489","ask1Price":"17216.00","ask1Size":"83.020"},"cs":24987956059,"ts":1673272861686}
        """;

    /// <summary>Deltas carry only the fields that changed.</summary>
    public const string TickerDelta = """
        {"topic":"tickers.BTCUSDT","type":"delta","data":{"symbol":"BTCUSDT","markPrice":"17218.10"},"cs":24987956060,"ts":1673272862686}
        """;

    public const string SubscribeAck = """{"success":true,"ret_msg":"subscribe","conn_id":"2324d924-aa4d-45b0-a858-7b8be29ab52b","req_id":"","op":"subscribe"}""";
}
