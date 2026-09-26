namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// Payloads written by hand in the shapes published in the Binance spot and USDⓈ-M futures API documentation, and -
/// for the coin-margined family - recorded from the live venue on 2026-09-25 rather than written from its
/// documentation, because the fields that family disagrees with its sibling about are exactly the ones a
/// hand-written payload would have copied from the sibling.
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

    /// <summary>
    /// GET /fapi/v1/exchangeInfo: one perpetual, one quarterly delivery contract, one contract being settled.
    /// <para>
    /// Every symbol carries <c>requiredMarginPercent</c> and <c>maintMarginPercent</c>, which this family publishes on
    /// all 909 of its contracts and this fixture did not carry until 2026-09-26. They were missing while a test
    /// asserted the adapter read them, and it passed: the figure the venue publishes is 5.0 and the figure the adapter
    /// falls back to is 0.05, so the assertion held either way and proved nothing about the read. A provenance is what
    /// told them apart, which is the argument for having one.
    /// </para>
    /// </summary>
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
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
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
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
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
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
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

    /// <summary>
    /// GET /dapi/v1/exchangeInfo, recorded live on 2026-09-25: the 100-USD BTCUSD perpetual, the 10-USD ETHUSD
    /// perpetual, a 100-USD BTCUSD quarterly, and - from the venue's test network, because mainnet happened to list
    /// none that day - one contract in delivery and one not yet trading.
    /// <para>
    /// Five contracts rather than the live 30, and every field the live answer carries. The last two are what make
    /// the fixture worth recording rather than writing: this family states tradability in <c>contractStatus</c> and
    /// publishes no <c>status</c> at all, so a reader looking only for the sibling family's field would take both of
    /// them for tradable - and one of them spells its contract type as the compound "CURRENT_QUARTER DELIVERING".
    /// </para>
    /// </summary>
    public const string CoinMExchangeInfo = """
        {
          "timezone": "UTC",
          "serverTime": 1700000000000,
          "rateLimits": [
            { "rateLimitType": "REQUEST_WEIGHT", "interval": "MINUTE", "intervalNum": 1, "limit": 2400 },
            { "rateLimitType": "ORDERS", "interval": "MINUTE", "intervalNum": 1, "limit": 1200 }
          ],
          "exchangeFilters": [],
          "symbols": [
            {
              "symbol": "BTCUSD_PERP", "pair": "BTCUSD", "contractType": "PERPETUAL",
              "deliveryDate": 4133404800000, "onboardDate": 1597042800000, "contractStatus": "TRADING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "BTC", "quoteAsset": "USD", "marginAsset": "BTC",
              "pricePrecision": 1, "quantityPrecision": 0, "baseAssetPrecision": 8, "quotePrecision": 8,
              "underlyingType": "COIN", "underlyingSubType": ["PoW"], "triggerProtect": "0.0500",
              "liquidationFee": "0.015000", "marketTakeBound": "0.05", "maxMoveOrderLimit": 10000,
              "contractSize": 100, "equalQtyPrecision": 4,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "1000", "maxPrice": "4520958", "tickSize": "0.1" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" },
                { "filterType": "MARKET_LOT_SIZE", "minQty": "1", "maxQty": "60000", "stepSize": "1" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "MAX_NUM_ALGO_ORDERS", "limit": 20 },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ],
              "orderTypes": ["LIMIT", "MARKET", "STOP", "STOP_MARKET", "TAKE_PROFIT", "TAKE_PROFIT_MARKET", "TRAILING_STOP_MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX"],
              "permissionSets": ["GRID"]
            },
            {
              "symbol": "ETHUSD_PERP", "pair": "ETHUSD", "contractType": "PERPETUAL",
              "deliveryDate": 4133404800000, "onboardDate": 1597042800000, "contractStatus": "TRADING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "ETH", "quoteAsset": "USD", "marginAsset": "ETH",
              "pricePrecision": 2, "quantityPrecision": 0, "baseAssetPrecision": 8, "quotePrecision": 8,
              "underlyingType": "COIN", "underlyingSubType": ["Layer-1"], "triggerProtect": "0.0500",
              "liquidationFee": "0.015000", "marketTakeBound": "0.05", "maxMoveOrderLimit": 10000,
              "contractSize": 10, "equalQtyPrecision": 4,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "50", "maxPrice": "306177", "tickSize": "0.01" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" },
                { "filterType": "MARKET_LOT_SIZE", "minQty": "1", "maxQty": "60000", "stepSize": "1" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "MAX_NUM_ALGO_ORDERS", "limit": 20 },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ],
              "orderTypes": ["LIMIT", "MARKET", "STOP", "STOP_MARKET", "TAKE_PROFIT", "TAKE_PROFIT_MARKET", "TRAILING_STOP_MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX"],
              "permissionSets": ["GRID"]
            },
            {
              "symbol": "BTCUSD_261225", "pair": "BTCUSD", "contractType": "CURRENT_QUARTER",
              "deliveryDate": 1798185600000, "onboardDate": 1766563200000, "contractStatus": "TRADING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "BTC", "quoteAsset": "USD", "marginAsset": "BTC",
              "pricePrecision": 1, "quantityPrecision": 0, "baseAssetPrecision": 8, "quotePrecision": 8,
              "underlyingType": "COIN", "underlyingSubType": [], "triggerProtect": "0.0500",
              "liquidationFee": "0.007500", "marketTakeBound": "0.05", "maxMoveOrderLimit": 10000,
              "contractSize": 100, "equalQtyPrecision": 4,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "2109.4", "maxPrice": "3515698.4", "tickSize": "0.1" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" },
                { "filterType": "MARKET_LOT_SIZE", "minQty": "0", "maxQty": "20000", "stepSize": "1" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "MAX_NUM_ALGO_ORDERS", "limit": 20 },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ],
              "orderTypes": ["LIMIT", "MARKET", "STOP", "STOP_MARKET", "TAKE_PROFIT", "TAKE_PROFIT_MARKET", "TRAILING_STOP_MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX"],
              "permissionSets": ["GRID"]
            },
            {
              "symbol": "BTCUSD_260626", "pair": "BTCUSD", "contractType": "CURRENT_QUARTER DELIVERING",
              "deliveryDate": 1782460800000, "onboardDate": 1766563200000, "contractStatus": "DELIVERING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "BTC", "quoteAsset": "USD", "marginAsset": "BTC",
              "pricePrecision": 1, "quantityPrecision": 0, "baseAssetPrecision": 8, "quotePrecision": 8,
              "underlyingType": "COIN", "underlyingSubType": [], "triggerProtect": "0.0500",
              "liquidationFee": "0.007500", "marketTakeBound": "0.05", "maxMoveOrderLimit": 10000,
              "contractSize": 100, "equalQtyPrecision": 4,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "2109.4", "maxPrice": "3515698.4", "tickSize": "0.1" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ],
              "orderTypes": ["LIMIT", "MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX"]
            },
            {
              "symbol": "EGLDUSD_PERP", "pair": "EGLDUSD", "contractType": "PERPETUAL",
              "deliveryDate": 4133404800000, "onboardDate": 1766563200000, "contractStatus": "PENDING_TRADING",
              "maintMarginPercent": "2.5000", "requiredMarginPercent": "5.0000",
              "baseAsset": "EGLD", "quoteAsset": "USD", "marginAsset": "EGLD",
              "pricePrecision": 3, "quantityPrecision": 0, "baseAssetPrecision": 8, "quotePrecision": 8,
              "underlyingType": "COIN", "underlyingSubType": [], "triggerProtect": "0.0500",
              "liquidationFee": "0.015000", "marketTakeBound": "0.05", "maxMoveOrderLimit": 10000,
              "contractSize": 10, "equalQtyPrecision": 4,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.5", "maxPrice": "10000", "tickSize": "0.001" },
                { "filterType": "LOT_SIZE", "minQty": "1", "maxQty": "1000000", "stepSize": "1" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ],
              "orderTypes": ["LIMIT", "MARKET"],
              "timeInForce": ["GTC", "IOC", "FOK", "GTX"]
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

    // ----- the coin-margined family's own frames, recorded live on 2026-09-25 -----
    //
    // Every one of these carries a field its USD-margined equivalent does not - `ps` for the pair on the book and
    // depth frames, `ap` for the estimated settlement price on the mark-price one - and sizes in contracts rather
    // than base units. They are recorded rather than adapted for that reason: a frame edited from the sibling
    // family's would have proved the parser reads a shape nobody sends.

    /// <summary>btcusd_perp@bookTicker: bid and ask sizes are numbers of 100-USD contracts.</summary>
    public const string CoinMBookTicker = """
        {"stream":"btcusd_perp@bookTicker","data":{"e":"bookTicker","u":11660059839759,"s":"BTCUSD_PERP","ps":"BTCUSD","b":"83732.4","B":"680","a":"83732.5","A":"2458","T":1790373637547,"E":1790373637547,"st":2}}
        """;

    /// <summary>btcusd_perp@trade, with the buyer as the taker.</summary>
    public const string CoinMTrade = """
        {"stream":"btcusd_perp@trade","data":{"e":"trade","E":1790373534694,"T":1790373534694,"s":"BTCUSD_PERP","t":1155839922,"p":"83781.6","q":"1","X":"MARKET","m":false}}
        """;

    /// <summary>btcusd_perp@kline_1m, closed: `v` is contracts and `q` is the coin they are worth.</summary>
    public const string CoinMKlineClosed = """
        {"stream":"btcusd_perp@kline_1m","data":{"e":"kline","E":1790373660003,"s":"BTCUSD_PERP","k":{"t":1790373600000,"T":1790373659999,"s":"BTCUSD_PERP","i":"1m","f":1155840109,"L":1155840149,"o":"83723.8","c":"83732.4","h":"83732.5","l":"83723.8","v":"180","n":41,"x":true,"q":"0.21498328","V":"94","Q":"0.11224187","B":"0"}}}
        """;

    /// <summary>btcusd_perp@markPrice@1s: mark in p, index in i, funding rate in r, exactly as on the other family.</summary>
    public const string CoinMMarkPrice = """
        {"stream":"btcusd_perp@markPrice@1s","data":{"e":"markPriceUpdate","E":1790373638000,"s":"BTCUSD_PERP","p":"83732.50000000","ap":"83732.50000000","P":"83805.72765516","i":"83786.17543766","r":"-0.00000573","T":1790380800000,"st":2}}
        """;

    /// <summary>btcusd_perp@depth@100ms, levels sized in contracts, a zero size meaning the level is gone.</summary>
    public const string CoinMDepthUpdate = """
        {"stream":"btcusd_perp@depth@100ms","data":{"e":"depthUpdate","E":1790373637622,"T":1790373637613,"s":"BTCUSD_PERP","ps":"BTCUSD","U":11660059838224,"u":11660059844303,"pu":11660059836647,"b":[["82997.3","1382"],["83517.9","0"]],"a":[["83732.5","2458"]],"st":2}}
        """;

    /// <summary>GET /dapi/v1/depth: the snapshot this family answers, which carries a `pair` the other does not.</summary>
    public const string CoinMDepthSnapshot = """
        {"symbol":"BTCUSD_PERP","pair":"BTCUSD","lastUpdateId":11660051107935,"E":1790373521101,"T":1790373521100,"bids":[["83778.8","5078"],["83778.7","1200"]],"asks":[["83778.9","956"]]}
        """;

    /// <summary>
    /// GET /dapi/v1/klines: two closed minute bars, element 0 the open and element 6 the close, and element 5 a
    /// number of contracts where the USD-margined family puts base units.
    /// </summary>
    public const string CoinMKlines = """
        [[1790373900000,"83772.1","83790.0","83750.0","83780.0","2804",1790373959999,"3.34573438",512,"1400","1.67000000","0"],
         [1790373960000,"83780.0","83800.0","83770.0","83790.0","646",1790374019999,"0.77074142",210,"300","0.35000000","0"]]
        """;

    /// <summary>
    /// GET /dapi/v1/fundingRate: three settlements of a coin-margined perpetual, oldest first, each carrying the
    /// mark price at settlement and a rate type the other family does not publish.
    /// </summary>
    public const string CoinMFundingRates = """
        [{"symbol":"BTCUSD_PERP","fundingTime":1790323200000,"fundingRate":"-0.00001838","markPrice":"73035.00000000","rateType":"Regular"},
         {"symbol":"BTCUSD_PERP","fundingTime":1790352000002,"fundingRate":"0.00000272","markPrice":"83742.41308536","rateType":"Regular"},
         {"symbol":"BTCUSD_PERP","fundingTime":1790380800000,"fundingRate":"-0.00000573","markPrice":"83778.90000000","rateType":"Regular"}]
        """;
}
