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

    /// <summary>
    /// GET /v5/market/instruments-info?category=inverse, exactly as the live venue answered on 2026-09-26: the
    /// BTCUSD perpetual and the BTCUSDZ26 dated contract, the two shapes this family holds.
    /// <para>
    /// The dated one is the reason the family cannot reuse the linear naming rule. Its symbol carries no dash
    /// anywhere, so "a symbol with no dash is a perpetual" would have called it one; what says otherwise is the
    /// venue's own contractType, and its delivery time. Both are here so a test can prove the class comes from the
    /// field and the id from neither.
    /// </para>
    /// </summary>
    public static readonly string InverseInstruments = Envelope("""
        {
          "category": "inverse",
          "list": [
            {
              "symbol": "BTCUSD", "contractType": "InversePerpetual", "status": "Trading", "baseCoin": "BTC",
              "quoteCoin": "USD", "launchTime": "1542211200000", "deliveryTime": "0", "deliveryFeeRate": "",
              "priceScale": "2",
              "leverageFilter": { "minLeverage": "1", "maxLeverage": "100.00", "leverageStep": "0.01" },
              "priceFilter": { "minPrice": "0.10", "maxPrice": "1999999.80", "tickSize": "0.10" },
              "lotSizeFilter": { "maxOrderQty": "25000000", "minOrderQty": "1", "qtyStep": "1", "postOnlyMaxOrderQty": "25000000", "maxMktOrderQty": "5000000", "minNotionalValue": "5" },
              "unifiedMarginTrade": true, "fundingInterval": 480, "settleCoin": "BTC", "copyTrading": "none",
              "upperFundingRate": "0.005", "lowerFundingRate": "-0.005", "displayName": "BTCUSD"
            },
            {
              "symbol": "BTCUSDZ26", "contractType": "InverseFutures", "status": "Trading", "baseCoin": "BTC",
              "quoteCoin": "USD", "launchTime": "1781251200000", "deliveryTime": "1798185600000",
              "deliveryFeeRate": "0.0005", "priceScale": "2",
              "leverageFilter": { "minLeverage": "1", "maxLeverage": "100.00", "leverageStep": "0.01" },
              "priceFilter": { "minPrice": "0.50", "maxPrice": "9999999.00", "tickSize": "0.50" },
              "lotSizeFilter": { "maxOrderQty": "5000000", "minOrderQty": "1", "qtyStep": "1", "postOnlyMaxOrderQty": "5000000", "maxMktOrderQty": "1000000", "minNotionalValue": "5" },
              "unifiedMarginTrade": true, "fundingInterval": 0, "settleCoin": "BTC", "copyTrading": "none",
              "displayName": "BTCUSD1225"
            }
          ],
          "nextPageCursor": ""
        }
        """);

    /// <summary>
    /// What the venue answered for BTCUSD's inverse risk limits on 2026-09-26, first two tiers. The lowest is the
    /// instrument's margin - 1 percent initial and 0.5 percent maintenance - and the second is here so a test can
    /// prove the tier is chosen rather than the first row taken.
    /// </summary>
    public static readonly string InverseRiskLimits = Envelope("""
        {
          "category": "inverse",
          "list": [
            { "id": 1, "symbol": "BTCUSD", "riskLimitValue": "150", "maintenanceMargin": "0.005",
              "initialMargin": "0.01", "isLowestRisk": 1, "maxLeverage": "100.00", "mmDeduction": "" },
            { "id": 2, "symbol": "BTCUSD", "riskLimitValue": "300", "maintenanceMargin": "0.01",
              "initialMargin": "0.015", "isLowestRisk": 0, "maxLeverage": "66.67", "mmDeduction": "0.75" },
            { "id": 1, "symbol": "BTCUSDZ26", "riskLimitValue": "100", "maintenanceMargin": "0.005",
              "initialMargin": "0.01", "isLowestRisk": 1, "maxLeverage": "100.00", "mmDeduction": "" }
          ],
          "nextPageCursor": ""
        }
        """);

    /// <summary>
    /// GET /v5/market/instruments-info?category=option, as the live venue answered on 2026-09-26. Thirteen fields
    /// per contract and NOT ONE OF THEM IS THE STRIKE: the strike exists only inside the symbol, which is why the
    /// provider reads it from there and checks it against baseCoin and optionsType as it does.
    /// <para>
    /// There is no leverageFilter either, and the venue refuses the risk-limit endpoint for this category, so
    /// nothing here says what an option is margined at. The third row is an XRP put struck at 0.4 against a
    /// ten-thousandth tick, which is the case where the strike needs fewer digits than the premium's precision.
    /// </para>
    /// </summary>
    public static readonly string OptionInstruments = Envelope("""
        {
          "category": "option",
          "nextPageCursor": "",
          "list": [
            {
              "symbolId": 394577, "symbol": "BTC-25JUN27-106000-P-USDT", "status": "Trading", "baseCoin": "BTC",
              "quoteCoin": "USDT", "settleCoin": "USDT", "optionsType": "Put", "launchTime": "1789950000000",
              "deliveryTime": "1813910400000", "deliveryFeeRate": "0.00015",
              "priceFilter": { "minPrice": "5", "maxPrice": "1110000", "tickSize": "5" },
              "lotSizeFilter": { "maxOrderQty": "500", "minOrderQty": "0.01", "qtyStep": "0.01" },
              "displayName": "BTCUSDT-25JUN27-106000-P"
            },
            {
              "symbolId": 394578, "symbol": "BTC-25JUN27-106000-C-USDT", "status": "Trading", "baseCoin": "BTC",
              "quoteCoin": "USDT", "settleCoin": "USDT", "optionsType": "Call", "launchTime": "1789950000000",
              "deliveryTime": "1813910400000", "deliveryFeeRate": "0.00015",
              "priceFilter": { "minPrice": "5", "maxPrice": "1110000", "tickSize": "5" },
              "lotSizeFilter": { "maxOrderQty": "500", "minOrderQty": "0.01", "qtyStep": "0.01" },
              "displayName": "BTCUSDT-25JUN27-106000-C"
            },
            {
              "symbolId": 385731, "symbol": "XRP-30OCT26-0.4-P-USDT", "status": "Trading", "baseCoin": "XRP",
              "quoteCoin": "USDT", "settleCoin": "USDT", "optionsType": "Put", "launchTime": "1788422400000",
              "deliveryTime": "1793347200000", "deliveryFeeRate": "0.0002",
              "priceFilter": { "minPrice": "0.0001", "maxPrice": "100", "tickSize": "0.0001" },
              "lotSizeFilter": { "maxOrderQty": "400000", "minOrderQty": "10", "qtyStep": "10" },
              "displayName": "XRPUSDT-30OCT26-0.4-P"
            }
          ]
        }
        """);

    /// <summary>
    /// The option tickers topic, recorded live on 2026-09-26. It carries the top of book under ITS OWN field names
    /// - bidPrice and bidSize where the contract markets write bid1Price and bid1Size - which is why an option's
    /// quotes come from here: the option socket accepts orderbook.1 and delivers nothing for it.
    /// </summary>
    public const string OptionTickerSnapshot = """
        {"topic":"tickers.BTC-25JUN27-106000-P-USDT","ts":1790373953406,"type":"snapshot","id":"tickers.BTC-25JUN27-106000-P-USDT-76572337574-1790373953406","data":{"symbol":"BTC-25JUN27-106000-P-USDT","bidPrice":"22915","bidSize":"18.55","bidIv":"0.3285","askPrice":"26205","askSize":"18.55","askIv":"0.4489","lastPrice":"0","highPrice24h":"0","lowPrice24h":"0","markPrice":"24465","indexPrice":"83885","markPriceIv":"0.3868","underlyingPrice":"86977.1","openInterest":"0","turnover24h":"0","volume24h":"0","totalVolume":"0","totalTurnover":"0","delta":"-0.5","gamma":"0.00001","vega":"100","theta":"-40","change24h":"0"}}
        """;

    /// <summary>A delta on the same topic: only the ask moved, so the bid has to be carried forward.</summary>
    public const string OptionTickerDelta = """
        {"topic":"tickers.BTC-25JUN27-106000-P-USDT","ts":1790373954406,"type":"delta","id":"tickers.BTC-25JUN27-106000-P-USDT-76572337575-1790373954406","data":{"symbol":"BTC-25JUN27-106000-P-USDT","askPrice":"26200","askSize":"20.00","askIv":"0.4480"}}
        """;

    /// <summary>
    /// The option trade topic, recorded live on 2026-09-26. It is named for the UNDERLYING and not for a contract -
    /// publicTrade.BTC - and every row names the contract it belongs to, so one subscription carries the trades of
    /// every BTC option. The second row is a contract the client has not loaded, which must produce nothing.
    /// </summary>
    public const string OptionPublicTrades = """
        {"topic":"publicTrade.BTC","ts":1790373961392,"type":"snapshot","id":"publicTrade.BTC-76572340339-1790373961392","data":[
          {"i":"44f06f5c-3628-5a2d-9a1c-0142ddf5e2ab","T":1790373961369,"p":"240","v":"0.1","S":"Buy","seq":76572340339,"s":"BTC-25JUN27-106000-C-USDT","BT":false,"mP":"238.7827077","iP":"83885.18491835","mIv":"0.1389","iv":"0.14"},
          {"i":"a34d7d60-0fbe-5ff2-865f-8621c27f64cb","T":1790373961369,"p":"245","v":"0.3","S":"Buy","seq":76572340339,"s":"BTC-26SEP26-83750-C-USDT","BT":false,"mP":"244.1","iP":"83885.18491835","mIv":"0.1389","iv":"0.14"}]}
        """;

    /// <summary>
    /// The option book, at the depth this market really publishes. Recorded live on 2026-09-26 from
    /// orderbook.25; the same contract subscribed to orderbook.1 and orderbook.50 delivered nothing in
    /// twenty-five seconds while the venue listed both under successTopics.
    /// </summary>
    public const string OptionBookSnapshot = """
        {"topic":"orderbook.25.BTC-25JUN27-106000-P-USDT","ts":1790373952615,"type":"snapshot","id":"orderbook.25.BTC-25JUN27-106000-P-USDT-76571748900-1790373952615","data":{"s":"BTC-25JUN27-106000-P-USDT","b":[["22915","18.55"],["5","0.96"]],"a":[["26205","18.55"]],"u":4846,"seq":76571748900},"cts":1790372489986}
        """;

    /// <summary>
    /// The subscription reply of the OPTION socket, recorded live. It is a different envelope from the one the
    /// other three families answer with, and - the part that matters - it reports every topic asked for under
    /// <c>successTopics</c> including the four that then send nothing at all.
    /// </summary>
    public const string OptionSubscribeAck = """
        {"success":true,"conn_id":"da7u80nak99fkijm8s6g-3x3ed","data":{"failTopics":[],"successTopics":["kline.1.BTC-25JUN27-106000-P-USDT","orderbook.1.BTC-25JUN27-106000-P-USDT","tickers.BTC-25JUN27-106000-P-USDT"]},"type":"COMMAND_RESP"}
        """;

    /// <summary>The inverse tickers topic, recorded live: mark, index and funding, as the linear family serves them.</summary>
    public const string InverseTickerSnapshot = """
        {"topic":"tickers.BTCUSD","type":"snapshot","data":{"symbol":"BTCUSD","tickDirection":"ZeroPlusTick","price24hPcnt":"-0.004837","lastPrice":"83807.60","markPrice":"83822.10","indexPrice":"83876.10","openInterest":"462590534","openInterestValue":"5518.71","fundingIntervalHour":"8","fundingCap":"0.005","nextFundingTime":"1790380800000","fundingRate":"-0.0000392","bid1Price":"83819.30","bid1Size":"5949","ask1Price":"83819.40","ask1Size":"46909"},"cs":24987956059,"ts":1790373926264}
        """;

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
