namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// Payloads written by hand in the shapes published in the Binance spot and USDⓈ-M futures API documentation.
/// </summary>
internal static class BinancePayloads
{
    /// <summary>GET /api/v3/exchangeInfo: two tradable pairs and one halted pair.</summary>
    public const string SpotExchangeInfo = """
        {
          "timezone": "UTC",
          "serverTime": 1700000000000,
          "rateLimits": [],
          "exchangeFilters": [],
          "symbols": [
            {
              "symbol": "BTCUSDT", "status": "TRADING",
              "baseAsset": "BTC", "baseAssetPrecision": 8, "quoteAsset": "USDT", "quotePrecision": 8, "quoteAssetPrecision": 8,
              "orderTypes": ["LIMIT", "LIMIT_MAKER", "MARKET", "STOP_LOSS_LIMIT", "TAKE_PROFIT_LIMIT"],
              "icebergAllowed": true, "ocoAllowed": true, "isSpotTradingAllowed": true, "isMarginTradingAllowed": true,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.01000000", "maxPrice": "1000000.00000000", "tickSize": "0.01000000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00001000", "maxQty": "9000.00000000", "stepSize": "0.00001000" },
                { "filterType": "ICEBERG_PARTS", "limit": 10 },
                { "filterType": "MARKET_LOT_SIZE", "minQty": "0.00000000", "maxQty": "120.00000000", "stepSize": "0.00000000" },
                { "filterType": "NOTIONAL", "minNotional": "5.00000000", "applyMinToMarket": true, "maxNotional": "9000000.00000000", "applyMaxToMarket": false, "avgPriceMins": 5 },
                { "filterType": "MAX_NUM_ORDERS", "maxNumOrders": 200 }
              ],
              "permissions": [], "defaultSelfTradePreventionMode": "EXPIRE_MAKER"
            },
            {
              "symbol": "ETHBTC", "status": "TRADING",
              "baseAsset": "ETH", "baseAssetPrecision": 8, "quoteAsset": "BTC", "quotePrecision": 8, "quoteAssetPrecision": 8,
              "orderTypes": ["LIMIT", "MARKET"],
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.00001000", "maxPrice": "922327.00000000", "tickSize": "0.00001000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00010000", "maxQty": "100000.00000000", "stepSize": "0.00010000" },
                { "filterType": "MIN_NOTIONAL", "minNotional": "0.00010000", "applyToMarket": true, "avgPriceMins": 5 }
              ],
              "permissions": []
            },
            {
              "symbol": "LUNAUSDT", "status": "BREAK",
              "baseAsset": "LUNA", "baseAssetPrecision": 8, "quoteAsset": "USDT", "quotePrecision": 8, "quoteAssetPrecision": 8,
              "orderTypes": ["LIMIT"],
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.00010000", "maxPrice": "1000.00000000", "tickSize": "0.00010000" },
                { "filterType": "LOT_SIZE", "minQty": "0.01000000", "maxQty": "9000000.00000000", "stepSize": "0.01000000" }
              ],
              "permissions": []
            }
          ]
        }
        """;

    /// <summary>GET /fapi/v1/exchangeInfo: one perpetual, one quarterly delivery contract, one contract being settled.</summary>
    public const string FuturesExchangeInfo = """
        {
          "timezone": "UTC",
          "serverTime": 1700000000000,
          "futuresType": "U_MARGINED",
          "rateLimits": [],
          "exchangeFilters": [],
          "assets": [],
          "symbols": [
            {
              "symbol": "BTCUSDT", "pair": "BTCUSDT", "contractType": "PERPETUAL",
              "deliveryDate": 4133404800000, "onboardDate": 1569398400000, "status": "TRADING",
              "baseAsset": "BTC", "quoteAsset": "USDT", "marginAsset": "USDT",
              "pricePrecision": 2, "quantityPrecision": 3, "baseAssetPrecision": 8, "quotePrecision": 8,
              "underlyingType": "COIN", "settlePlan": 0, "triggerProtect": "0.0500",
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "556.80", "maxPrice": "4529764", "tickSize": "0.10" },
                { "filterType": "LOT_SIZE", "minQty": "0.001", "maxQty": "1000", "stepSize": "0.001" },
                { "filterType": "MARKET_LOT_SIZE", "minQty": "0.001", "maxQty": "120", "stepSize": "0.001" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "MIN_NOTIONAL", "notional": "100" },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ],
              "orderTypes": ["LIMIT", "MARKET", "STOP", "STOP_MARKET", "TAKE_PROFIT", "TAKE_PROFIT_MARKET", "TRAILING_STOP_MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX", "GTD"]
            },
            {
              "symbol": "BTCUSDT_250926", "pair": "BTCUSDT", "contractType": "CURRENT_QUARTER",
              "deliveryDate": 1758873600000, "onboardDate": 1742544000000, "status": "TRADING",
              "baseAsset": "BTC", "quoteAsset": "USDT", "marginAsset": "USDT",
              "pricePrecision": 1, "quantityPrecision": 3, "baseAssetPrecision": 8, "quotePrecision": 8,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "576.3", "maxPrice": "1000000", "tickSize": "0.1" },
                { "filterType": "LOT_SIZE", "minQty": "0.001", "maxQty": "500", "stepSize": "0.001" },
                { "filterType": "MIN_NOTIONAL", "notional": "5" }
              ],
              "orderTypes": ["LIMIT", "MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX"]
            },
            {
              "symbol": "ETHUSDT_230630", "pair": "ETHUSDT", "contractType": "CURRENT_QUARTER",
              "deliveryDate": 1688112000000, "onboardDate": 1679644800000, "status": "SETTLING",
              "baseAsset": "ETH", "quoteAsset": "USDT", "marginAsset": "USDT",
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "39.86", "maxPrice": "306177", "tickSize": "0.01" },
                { "filterType": "LOT_SIZE", "minQty": "0.001", "maxQty": "10000", "stepSize": "0.001" }
              ]
            }
          ]
        }
        """;

    /// <summary>Spot exchangeInfo narrowed to one symbol, as returned for ?symbol=BTCUSDT.</summary>
    public static string SpotExchangeInfoFor(string symbol)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(SpotExchangeInfo);
        string match = doc.RootElement.GetProperty("symbols").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == symbol).GetRawText();
        return "{\"timezone\":\"UTC\",\"serverTime\":1700000000000,\"rateLimits\":[],\"exchangeFilters\":[],\"symbols\":[" + match + "]}";
    }

    public const string SpotBookTicker = """
        {"stream":"btcusdt@bookTicker","data":{"u":400900217,"s":"BTCUSDT","b":"25000.35","B":"31.21000000","a":"25000.36","A":"40.66000000"}}
        """;

    public const string FuturesBookTicker = """
        {"stream":"btcusdt@bookTicker","data":{"e":"bookTicker","u":400900217,"E":1568014460893,"T":1568014460891,"s":"BTCUSDT","b":"25000.30","B":"31.210","a":"25000.40","A":"40.660"}}
        """;

    public const string SpotTradeBuyerIsMaker = """
        {"stream":"btcusdt@trade","data":{"e":"trade","E":1672515782136,"s":"BTCUSDT","t":12345,"p":"25000.10","q":"0.10000000","T":1672515782134,"m":true,"M":true}}
        """;

    public const string SpotTradeBuyerIsTaker = """
        {"stream":"btcusdt@trade","data":{"e":"trade","E":1672515782236,"s":"BTCUSDT","t":12346,"p":"25000.20","q":"0.25000000","T":1672515782234,"m":false,"M":true}}
        """;

    public const string KlineOpen = """
        {"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1672515782136,"s":"BTCUSDT","k":{"t":1672515780000,"T":1672515839999,"s":"BTCUSDT","i":"1m","f":100,"L":200,"o":"25000.00","c":"25010.00","h":"25020.00","l":"24990.00","v":"12.50000","n":100,"x":false,"q":"312500.0","V":"6.0","Q":"150000.0","B":"0"}}}
        """;

    public const string KlineClosed = """
        {"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1672515840003,"s":"BTCUSDT","k":{"t":1672515780000,"T":1672515839999,"s":"BTCUSDT","i":"1m","f":100,"L":250,"o":"25000.00","c":"25015.50","h":"25030.00","l":"24990.00","v":"18.75000","n":150,"x":true,"q":"468750.0","V":"9.0","Q":"225000.0","B":"0"}}}
        """;

    public const string DepthUpdate = """
        {"stream":"btcusdt@depth@100ms","data":{"e":"depthUpdate","E":1672515782136,"s":"BTCUSDT","U":157,"u":160,"b":[["25000.00","1.50000"],["24999.99","0.00000000"]],"a":[["25000.01","2.00000"]]}}
        """;

    public const string DepthSnapshot = """
        {"lastUpdateId":1027024,"bids":[["25000.00","4.00000000"],["24999.00","1.00000000"]],"asks":[["25000.02","12.00000000"]]}
        """;

    public const string MarkPriceUpdate = """
        {"stream":"btcusdt@markPrice@1s","data":{"e":"markPriceUpdate","E":1562305380000,"s":"BTCUSDT","p":"11794.15000000","i":"11784.62659091","P":"11784.25641265","r":"0.00038167","T":1562306400000}}
        """;

    public const string SubscriptionAck = """{"result":null,"id":1}""";
}
