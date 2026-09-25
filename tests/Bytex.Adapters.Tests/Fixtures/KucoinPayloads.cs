namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// KuCoin answers and stream messages. Shapes and field names are the venue's own (its published API specification and,
/// for the symbol and the candles, answers recorded from the live venue); the numbers are chosen so that expected values
/// can be worked out by hand.
/// </summary>
internal static class KucoinPayloads
{
    public static string Envelope(string data) => "{\"code\":\"200000\",\"data\":" + data + "}";

    public static string Error(string code, string message) => "{\"code\":\"" + code + "\",\"msg\":\"" + message + "\"}";

    /// <summary>BTC-USDT as the live venue describes it, ETH-USDT with a fee coefficient, and a pair that is not trading.</summary>
    public static readonly string Symbols = Envelope("""
        [
          {"symbol":"BTC-USDT","name":"BTC-USDT","baseCurrency":"BTC","quoteCurrency":"USDT","feeCurrency":"USDT","market":"USDS","baseMinSize":"0.00001","quoteMinSize":"0.1","baseMaxSize":"10000000000","quoteMaxSize":"99999999","baseIncrement":"0.00000001","quoteIncrement":"0.000001","priceIncrement":"0.1","priceLimitRate":"0.01","minFunds":"0.1","isMarginEnabled":true,"enableTrading":true,"feeCategory":1,"makerFeeCoefficient":"1.00","takerFeeCoefficient":"1.00","st":false},
          {"symbol":"ETH-USDT","name":"ETH-USDT","baseCurrency":"ETH","quoteCurrency":"USDT","feeCurrency":"USDT","market":"USDS","baseMinSize":"0.0001","quoteMinSize":"0.1","baseMaxSize":"5000","quoteMaxSize":"99999999","baseIncrement":"0.0000001","quoteIncrement":"0.000001","priceIncrement":"0.01","priceLimitRate":"0.01","minFunds":"0.1","isMarginEnabled":true,"enableTrading":true,"feeCategory":2,"makerFeeCoefficient":"2.00","takerFeeCoefficient":"3.00","st":false},
          {"symbol":"OLD-BTC","name":"OLD-BTC","baseCurrency":"OLD","quoteCurrency":"BTC","feeCurrency":"BTC","market":"BTC","baseMinSize":"1","quoteMinSize":"0.00001","baseMaxSize":"1000","quoteMaxSize":"10","baseIncrement":"0.01","quoteIncrement":"0.00000001","priceIncrement":"0.00000001","minFunds":"0.00001","isMarginEnabled":false,"enableTrading":false,"feeCategory":1,"makerFeeCoefficient":"1.00","takerFeeCoefficient":"1.00","st":false}
        ]
        """);

    public static string Bullet(string wsBase, string token = "public-token-1", int pingInterval = 18000) => Envelope(
        "{\"token\":\"" + token + "\",\"instanceServers\":[{\"endpoint\":\"" + wsBase + "/endpoint\",\"encrypt\":true,\"protocol\":\"websocket\",\"pingInterval\":" + pingInterval + ",\"pingTimeout\":10000}]}");

    public const string Level1 = """
        {"topic":"/spotMarket/level1:BTC-USDT","type":"message","subject":"level1","data":{"asks":["68145.8","0.51987471"],"bids":["68145.7","1.29267802"],"timestamp":1729816058766}}
        """;

    public const string Match = """
        {"topic":"/market/match:BTC-USDT","type":"message","subject":"trade.l3match","data":{"makerOrderId":"671b5007389355000701b1d3","price":"67523","sequence":"11067996711960577","side":"buy","size":"0.003","symbol":"BTC-USDT","takerOrderId":"671b50161777ff00074c168d","time":"1729843222921000000","tradeId":"11067996711960577","type":"match"}}
        """;

    /// <summary>A candle message for the 1-minute topic: start (seconds), open, close, high, low, volume, turnover.</summary>
    public static string Candle(long startSeconds, string open, string close, string high, string low, string volume) =>
        "{\"topic\":\"/market/candles:BTC-USDT_1min\",\"type\":\"message\",\"subject\":\"trade.candles.update\",\"data\":{\"symbol\":\"BTC-USDT\",\"candles\":[\"" + startSeconds + "\",\"" + open + "\",\"" + close + "\",\"" + high + "\",\"" + low + "\",\"" + volume + "\",\"1\"],\"time\":" + (startSeconds * 1_000_000_000L + 5) + "}}";

    public const string Depth = """
        {"topic":"/spotMarket/level2Depth50:BTC-USDT","type":"message","subject":"level2","data":{"asks":[["95964.3","0.08168874"],["95967.9","0.00985094"]],"bids":[["95964.2","0.5"],["95960","1.25"],["95955.5","2"]],"timestamp":1733124805073}}
        """;

    public const string Welcome = """{"id":"welcome-1","type":"welcome"}""";

    // ----- private stream -----

    public static string OrderChange(string clientOid, string type, string status, string extra = "") =>
        "{\"topic\":\"/spotMarket/tradeOrdersV2\",\"type\":\"message\",\"subject\":\"orderChange\",\"userId\":\"633559791e1cbc0001f319bc\",\"channelType\":\"private\",\"data\":{\"clientOid\":\"" + clientOid
        + "\",\"orderId\":\"6720da3fa30a360007f5f832\",\"orderTime\":1730206271588,\"orderType\":\"limit\",\"originSize\":\"0.5\",\"price\":\"50000\",\"side\":\"buy\",\"size\":\"0.5\",\"status\":\"" + status
        + "\",\"symbol\":\"BTC-USDT\",\"ts\":1730206271616000000,\"type\":\"" + type + "\"" + extra + "}}";

    public const string Balance = """
        {"topic":"/account/balance","type":"message","subject":"account.balance","id":"354689988084000","userId":"633559791e1cbc0001f319bc","channelType":"private","data":{"accountId":"548674591753","currency":"USDT","total":"21.133773386762","available":"20.132773386762","hold":"1.001","availableChange":"-0.5005","holdChange":"0.5005","relationContext":{"symbol":"BTC-USDT","orderId":"6721d0632db25b0007071fdc"},"relationEvent":"trade.hold","relationEventId":"354689988084000","time":"1730269283892"}}
        """;

    public static string StopOrderChange(string orderId, string type) =>
        "{\"topic\":\"/spotMarket/advancedOrders\",\"type\":\"message\",\"subject\":\"stopOrder\",\"userId\":\"633559791e1cbc0001f319bc\",\"channelType\":\"private\",\"data\":{\"orderId\":\"" + orderId
        + "\",\"orderPrice\":\"70000\",\"orderType\":\"stop\",\"side\":\"sell\",\"size\":\"0.5\",\"stop\":\"loss\",\"stopPrice\":\"49000\",\"symbol\":\"BTC-USDT\",\"tradeType\":\"TRADE\",\"type\":\"" + type + "\",\"createdAt\":1742305928064,\"ts\":1742305928091268493}}";

    // ----- private REST -----

    public static readonly string Accounts = Envelope("""
        [
          {"id":"548674591753","currency":"USDT","type":"trade","balance":"26.66759503","available":"25.66759503","holds":"1"},
          {"id":"548674591754","currency":"BTC","type":"trade","balance":"0.5","available":"0.5","holds":"0"}
        ]
        """);

    public static readonly string ActiveOrders = Envelope("""
        [
          {"id":"67120bbef094e200070976f6","clientOid":"O-open-1","symbol":"BTC-USDT","opType":"DEAL","type":"limit","side":"buy","price":"50000","size":"0.5","funds":"25000","dealSize":"0.2","dealFunds":"9990","fee":"9.99","feeCurrency":"USDT","stp":null,"timeInForce":"GTC","postOnly":true,"hidden":false,"iceberg":false,"visibleSize":"0","cancelAfter":0,"channel":"API","remark":null,"tags":null,"cancelExist":false,"tradeType":"TRADE","inOrderBook":true,"cancelledSize":"0","cancelledFunds":"0","remainSize":"0.3","remainFunds":"15000","tax":"0","active":true,"createdAt":1729235902748,"lastUpdatedAt":1729235909862}
        ]
        """);

    public static readonly string StopOrders = Envelope("""
        {"currentPage":1,"pageSize":500,"totalNum":1,"totalPage":1,"items":[
          {"id":"vs93gptvr9t2fsql003l8k5p","symbol":"BTC-USDT","userId":"633559791e1cbc0001f319bc","status":"NEW","type":"limit","side":"sell","price":"48900.00000000000000000000","size":"0.50000000000000000000","funds":null,"stp":null,"timeInForce":"GTC","cancelAfter":-1,"postOnly":false,"hidden":false,"iceberg":false,"visibleSize":null,"channel":"API","clientOid":"O-stop-1","remark":null,"tags":null,"relatedNo":null,"orderTime":1740626554883000024,"domainId":"kucoin","tradeSource":"USER","tradeType":"TRADE","feeCurrency":"USDT","takerFeeRate":"0.00100000000000000000","makerFeeRate":"0.00100000000000000000","createdAt":1740626554884,"stop":"loss","stopTriggerTime":null,"stopPrice":"49000.00000000000000000000"}
        ]}
        """);

    public static readonly string Fills = Envelope("""
        {"items":[
          {"id":19814995255305,"orderId":"6717422bd51c29000775ea03","counterOrderId":"67174228135f9e000709da8c","tradeId":11029373945659392,"symbol":"BTC-USDT","side":"buy","liquidity":"taker","type":"limit","forceTaker":false,"price":"67717.6","size":"0.00001","funds":"0.677176","fee":"0.000677176","feeRate":"0.001","feeCurrency":"USDT","stop":"","tradeType":"TRADE","taxRate":"0","tax":"0","createdAt":1729577515473}
        ],"lastId":19814995255305}
        """);

    public static readonly string ApiKeyInfo = Envelope("""
        {"remark":"bot","apiKey":"6705f5c311545b000157d3eb","apiVersion":3,"permission":"General,Spot","ipWhitelist":"203.0.113.7","createdAt":1728443843000,"uid":165111215,"isMaster":true}
        """);

    /// <summary>
    /// GET /api/v1/contracts/active, in the shape the live venue answered with on 2026-09-25. Four contracts: a
    /// linear perpetual, a second one whose contract size is bigger than a whole unit, an inverse perpetual and an
    /// inverse dated future. The inverse ones are what this adapter does not offer, and they are here so that it is
    /// a test rather than an intention that they stay out.
    /// <para>
    /// The last row is the exception to "as the venue answered": every inverse contract the venue lists carries a
    /// multiplier of -1, so leaving them out would happen by itself, from a sign check that is there for a different
    /// reason. This one is inverse with an ordinary positive multiplier, which the venue does not return today. It
    /// is here so that the rule being tested is "an inverse contract is never offered" rather than "a contract with
    /// a negative size is never offered", which are the same thing only by luck and only for now.
    /// </para>
    /// <para>
    /// The row after it is the other shape the venue does not return today: a LINEAR contract with a delivery date.
    /// Every dated contract KuCoin lists is inverse, so without this row "not inverse" and "perpetual" would look
    /// like one rule, and a contract that expires would be published as one that never does with nothing to notice.
    /// </para>
    /// </summary>
    public static readonly string FuturesContracts = Envelope("""
        [
          {"symbol":"XBTUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":0.001,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,"maxPrice":1000000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"initialMargin":0.008,"maintainMargin":0.004,"maxLeverage":125,
           "isInverse":false,"expireDate":null,"fundingRateGranularity":28800000},
          {"symbol":"DOGEUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"DOGE","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":10.0,"lotSize":1,"tickSize":0.00001,"maxOrderQty":1000000,"maxPrice":1000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"initialMargin":0.01,"maintainMargin":0.005,"maxLeverage":75,
           "isInverse":false,"expireDate":null,"fundingRateGranularity":28800000},
          {"symbol":"XBTUSDM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USD","settleCurrency":"XBT",
           "multiplier":-1.0,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,"maxPrice":1000000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":true,"expireDate":null},
          {"symbol":"XBTMU26","type":"FFICSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USD","settleCurrency":"XBT",
           "multiplier":-1.0,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,"maxPrice":1000000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":true,"expireDate":1790323200000},
          {"symbol":"ETHUSDXM","type":"FFWCSX","status":"Open","baseCurrency":"ETH","quoteCurrency":"USD","settleCurrency":"ETH",
           "multiplier":1.0,"lotSize":1,"tickSize":0.01,"maxOrderQty":1000000,"maxPrice":100000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":true,"expireDate":null},
          {"symbol":"ETHUSDTZ26","type":"FFICSX","status":"Open","baseCurrency":"ETH","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":0.01,"lotSize":1,"tickSize":0.01,"maxOrderQty":1000000,"maxPrice":100000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":1798185600000}
        ]
        """);

    /// <summary>
    /// Contracts whose SYMBOLS contain another currency's name: AIXBTUSDTM is a token called AIXBT, not bitcoin with
    /// a prefix, and ETHBTCUSDTM has a base of ETHBTC. Anything resolving a base currency by looking for it inside a
    /// symbol, or by stripping a quote off the end to guess what is left, mis-resolves both.
    /// </summary>
    public static readonly string FuturesLookalikeContracts = Envelope("""
        [
          {"symbol":"XBTUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"XBT","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":0.001,"lotSize":1,"tickSize":0.1,"maxOrderQty":1000000,"maxPrice":1000000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":null},
          {"symbol":"AIXBTUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"AIXBT","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":1.0,"lotSize":1,"tickSize":0.0001,"maxOrderQty":1000000,"maxPrice":10000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":null},
          {"symbol":"ETHBTCUSDTM","type":"FFWCSX","status":"Open","baseCurrency":"ETHBTC","quoteCurrency":"USDT","settleCurrency":"USDT",
           "multiplier":0.01,"lotSize":1,"tickSize":0.00001,"maxOrderQty":1000000,"maxPrice":1000.0,
           "makerFeeRate":0.0002,"takerFeeRate":0.0006,"isInverse":false,"expireDate":null}
        ]
        """);

    /// <summary>
    /// One futures candle row: [start in MILLISECONDS, open, high, low, close, volume in CONTRACTS, turnover]. Spot
    /// puts close where this puts high and counts in seconds, so the two are laid out here rather than shared.
    /// </summary>
    public static string FuturesCandle(long startMs, string open, string high, string low, string close, string contracts) =>
        "[" + startMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + open + "," + high + "," + low + "," + close + "," + contracts + ",0]";

    /// <summary>A futures kline answer: rows oldest first, as the venue orders them.</summary>
    public static string FuturesCandles(params string[] rows) => Envelope("[" + string.Join(",", rows) + "]");

    /// <summary>GET /api/v1/contract/funding-rates, newest settlement first as the venue answers it.</summary>
    public static string FundingRates(params (long TimepointMs, string Rate)[] settlements) => Envelope(
        "[" + string.Join(",", settlements.Select(s =>
            "{\"symbol\":\"XBTUSDTM\",\"fundingRate\":" + s.Rate + ",\"timepoint\":" + s.TimepointMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}")) + "]");
}
