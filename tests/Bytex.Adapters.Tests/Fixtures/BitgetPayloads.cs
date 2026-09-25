namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// Bitget payloads recorded from the live venue on 2026-09-25, byte for byte as it answered. Every public endpoint
/// this adapter reads needs no key, so none of these is invented: they are what <c>api.bitget.com</c> and
/// <c>ws.bitget.com</c> really sent, trimmed to a few rows where the real answer held hundreds.
/// <para>
/// Recorded rather than written because the differences that matter here are the ones nobody would think to write
/// down: the spot candle row has eight columns and the derivative row seven, the spot catalog states precision as a
/// number of decimal places, the USDC contracts leave their quote currency out of the symbol, and the fee on a spot
/// pair is not the fee the venue's fee schedule leads with.
/// </para>
/// </summary>
internal static class BitgetPayloads
{
    /// <summary>The envelope every Bitget answer arrives in, around whatever a caller wants inside it.</summary>
    public static string Envelope(string data) =>
        $$"""{"code":"00000","msg":"success","requestTime":1790358274804,"data":{{data}}}""";

    /// <summary>A refusal, which the venue sends with HTTP 200 as readily as with 400.</summary>
    public static string Error(string code, string message) =>
        $$"""{"code":"{{code}}","msg":"{{message}}","requestTime":1790358274804,"data":null}""";

    /// <summary>
    /// Three of the 3169 spot pairs the venue listed. BTCUSDT and XRPUSDT charge 0.2 percent and not the 0.1 percent
    /// that 3115 of the pairs charge, which is why a family default is an estimate and an instrument's own rate is not.
    /// </summary>
    public static string SpotSymbols => Envelope("""
        [
                {"symbol":"XRPUSDT","baseCoin":"XRP","quoteCoin":"USDT","minTradeAmount":"0","maxTradeAmount":"900000000000000000000","takerFeeRate":"0.002","makerFeeRate":"0.002","pricePrecision":"4","quantityPrecision":"4","quotePrecision":"8","status":"online","minTradeUSDT":"1","buyLimitPriceRatio":"0.02","sellLimitPriceRatio":"0.02","areaSymbol":"no","orderQuantity":"200","openTime":"1557133320000","offTime":"","maxLimitOrderValue":"20000000","maxMarketOrderValue":"1000000"},
                {"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","minTradeAmount":"0","maxTradeAmount":"900000000000000000000","takerFeeRate":"0.002","makerFeeRate":"0.002","pricePrecision":"2","quantityPrecision":"6","quotePrecision":"8","status":"online","minTradeUSDT":"1","buyLimitPriceRatio":"0.02","sellLimitPriceRatio":"0.02","areaSymbol":"no","orderQuantity":"200","openTime":"1532454360000","offTime":"","maxLimitOrderValue":"20000000","maxMarketOrderValue":"1000000"},
                {"symbol":"ETHUSDT","baseCoin":"ETH","quoteCoin":"USDT","minTradeAmount":"0.0005","maxTradeAmount":"900000000000000000000","takerFeeRate":"0.002","makerFeeRate":"0.002","pricePrecision":"2","quantityPrecision":"4","quotePrecision":"6","status":"online","minTradeUSDT":"1","buyLimitPriceRatio":"0.02","sellLimitPriceRatio":"0.02","areaSymbol":"no","orderQuantity":"200","openTime":"1532450400000","offTime":"","maxLimitOrderValue":"20000000","maxMarketOrderValue":"1000000"}
            ]
        """);

    /// <summary>Three of the 805 USDT-margined contracts. Every one of them is <c>symbolType: perpetual</c>.</summary>
    public static string UsdtContracts => Envelope("""
        [
                {"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","buyLimitPriceRatio":"0.05","sellLimitPriceRatio":"0.05","feeRateUpRatio":"0.005","makerFeeRate":"0.0002","takerFeeRate":"0.0006","openCostUpRatio":"0.01","supportMarginCoins":["USDT"],"minTradeNum":"0.0001","priceEndStep":"1","volumePlace":"4","pricePlace":"1","sizeMultiplier":"0.0001","symbolType":"perpetual","minTradeUSDT":"5","maxSymbolOrderNum":"200","maxProductOrderNum":"1000","maxPositionNum":"200","symbolStatus":"normal","offTime":"-1","limitOpenTime":"-1","deliveryTime":"","deliveryStartTime":"","deliveryPeriod":"","launchTime":"","fundInterval":"8","minLever":"1","maxLever":"150","posLimit":"0.2","maintainTime":"","openTime":"","maxMarketOrderQty":"220","maxOrderQty":"1200","isRwa":"NO"},
                {"symbol":"ETHUSDT","baseCoin":"ETH","quoteCoin":"USDT","buyLimitPriceRatio":"0.05","sellLimitPriceRatio":"0.05","feeRateUpRatio":"0.005","makerFeeRate":"0.0002","takerFeeRate":"0.0006","openCostUpRatio":"0.01","supportMarginCoins":["USDT"],"minTradeNum":"0.01","priceEndStep":"1","volumePlace":"2","pricePlace":"2","sizeMultiplier":"0.01","symbolType":"perpetual","minTradeUSDT":"5","maxSymbolOrderNum":"200","maxProductOrderNum":"1000","maxPositionNum":"200","symbolStatus":"normal","offTime":"-1","limitOpenTime":"-1","deliveryTime":"","deliveryStartTime":"","deliveryPeriod":"","launchTime":"","fundInterval":"8","minLever":"1","maxLever":"150","posLimit":"0.1","maintainTime":"","openTime":"","maxMarketOrderQty":"1900","maxOrderQty":"9900","isRwa":"NO"},
                {"symbol":"XRPUSDT","baseCoin":"XRP","quoteCoin":"USDT","buyLimitPriceRatio":"0.07","sellLimitPriceRatio":"0.07","feeRateUpRatio":"0.005","makerFeeRate":"0.0002","takerFeeRate":"0.0006","openCostUpRatio":"0.01","supportMarginCoins":["USDT"],"minTradeNum":"1","priceEndStep":"1","volumePlace":"0","pricePlace":"4","sizeMultiplier":"1","symbolType":"perpetual","minTradeUSDT":"5","maxSymbolOrderNum":"200","maxProductOrderNum":"1000","maxPositionNum":"200","symbolStatus":"normal","offTime":"-1","limitOpenTime":"-1","deliveryTime":"","deliveryStartTime":"","deliveryPeriod":"","launchTime":"","fundInterval":"8","minLever":"1","maxLever":"125","posLimit":"0.15","maintainTime":"","openTime":"","maxMarketOrderQty":"460000","maxOrderQty":"3700000","isRwa":"NO"}
            ]
        """);

    /// <summary>
    /// Two of the 49 USDC-margined contracts. The symbol leaves the quote currency out - <c>BTCPERP</c>, not
    /// <c>BTCUSDC</c> - which is the whole reason the instrument id of this family is built rather than copied.
    /// </summary>
    public static string UsdcContracts => Envelope("""
        [
                {"symbol":"BTCPERP","baseCoin":"BTC","quoteCoin":"USDC","buyLimitPriceRatio":"0.05","sellLimitPriceRatio":"0.05","feeRateUpRatio":"0.005","makerFeeRate":"0.0002","takerFeeRate":"0.0006","openCostUpRatio":"0.01","supportMarginCoins":["USDC"],"minTradeNum":"0.0001","priceEndStep":"1","volumePlace":"4","pricePlace":"1","sizeMultiplier":"0.0001","symbolType":"perpetual","minTradeUSDT":"5","maxSymbolOrderNum":"200","maxProductOrderNum":"1000","maxPositionNum":"200","symbolStatus":"normal","offTime":"-1","limitOpenTime":"-1","deliveryTime":"","deliveryStartTime":"","deliveryPeriod":"","launchTime":"","fundInterval":"8","minLever":"1","maxLever":"125","posLimit":"0.2","maintainTime":"","openTime":"","maxMarketOrderQty":"20","maxOrderQty":"100","isRwa":"NO"},
                {"symbol":"ETHPERP","baseCoin":"ETH","quoteCoin":"USDC","buyLimitPriceRatio":"0.05","sellLimitPriceRatio":"0.05","feeRateUpRatio":"0.005","makerFeeRate":"0.0002","takerFeeRate":"0.0006","openCostUpRatio":"0.01","supportMarginCoins":["USDC"],"minTradeNum":"0.01","priceEndStep":"1","volumePlace":"2","pricePlace":"2","sizeMultiplier":"0.01","symbolType":"perpetual","minTradeUSDT":"5","maxSymbolOrderNum":"200","maxProductOrderNum":"1000","maxPositionNum":"200","symbolStatus":"normal","offTime":"-1","limitOpenTime":"-1","deliveryTime":"","deliveryStartTime":"","deliveryPeriod":"","launchTime":"","fundInterval":"8","minLever":"1","maxLever":"100","posLimit":"0.2","maintainTime":"","openTime":"","maxMarketOrderQty":"200","maxOrderQty":"1000","isRwa":"NO"}
            ]
        """);

    /// <summary>
    /// BTCUSDT's twelve position tiers. The first row is what an empty account is held to: 150x leverage, so a
    /// 0.67 percent initial margin, and a 0.4 percent maintenance rate - not the 5 and 2.5 percent two other adapters
    /// in this repository publish for every contract they hold.
    /// </summary>
    public static string BtcUsdtPositionTiers => Envelope("""
        [
                {"symbol":"BTCUSDT","level":"1","startUnit":"0","endUnit":"200000","leverage":"150","keepMarginRate":"0.0040"},
                {"symbol":"BTCUSDT","level":"2","startUnit":"200000","endUnit":"1000000","leverage":"100","keepMarginRate":"0.0050"},
                {"symbol":"BTCUSDT","level":"3","startUnit":"1000000","endUnit":"5000000","leverage":"75","keepMarginRate":"0.0070"},
                {"symbol":"BTCUSDT","level":"4","startUnit":"5000000","endUnit":"15000000","leverage":"50","keepMarginRate":"0.0100"},
                {"symbol":"BTCUSDT","level":"5","startUnit":"15000000","endUnit":"50000000","leverage":"25","keepMarginRate":"0.0200"},
                {"symbol":"BTCUSDT","level":"6","startUnit":"50000000","endUnit":"100000000","leverage":"20","keepMarginRate":"0.0300"},
                {"symbol":"BTCUSDT","level":"7","startUnit":"100000000","endUnit":"150000000","leverage":"10","keepMarginRate":"0.0600"},
                {"symbol":"BTCUSDT","level":"8","startUnit":"150000000","endUnit":"300000000","leverage":"5","keepMarginRate":"0.1200"},
                {"symbol":"BTCUSDT","level":"9","startUnit":"300000000","endUnit":"500000000","leverage":"4","keepMarginRate":"0.1500"},
                {"symbol":"BTCUSDT","level":"10","startUnit":"500000000","endUnit":"700000000","leverage":"3","keepMarginRate":"0.2000"},
                {"symbol":"BTCUSDT","level":"11","startUnit":"700000000","endUnit":"900000000","leverage":"2","keepMarginRate":"0.3000"},
                {"symbol":"BTCUSDT","level":"12","startUnit":"900000000","endUnit":"1200000000","leverage":"1","keepMarginRate":"0.6000"}
            ]
        """);

    /// <summary>BTCPERP's tiers, whose first row allows 125x where BTCUSDT's allows 150x.</summary>
    public static string BtcPerpPositionTiers => Envelope("""
        [
                {"symbol":"BTCPERP","level":"1","startUnit":"0","endUnit":"150000","leverage":"125","keepMarginRate":"0.0040"},
                {"symbol":"BTCPERP","level":"2","startUnit":"150000","endUnit":"1000000","leverage":"100","keepMarginRate":"0.0050"},
                {"symbol":"BTCPERP","level":"3","startUnit":"1000000","endUnit":"3000000","leverage":"75","keepMarginRate":"0.0070"},
                {"symbol":"BTCPERP","level":"4","startUnit":"3000000","endUnit":"6000000","leverage":"50","keepMarginRate":"0.0100"},
                {"symbol":"BTCPERP","level":"5","startUnit":"6000000","endUnit":"9000000","leverage":"40","keepMarginRate":"0.0150"},
                {"symbol":"BTCPERP","level":"6","startUnit":"9000000","endUnit":"12000000","leverage":"25","keepMarginRate":"0.0200"},
                {"symbol":"BTCPERP","level":"7","startUnit":"12000000","endUnit":"15000000","leverage":"20","keepMarginRate":"0.0300"},
                {"symbol":"BTCPERP","level":"8","startUnit":"15000000","endUnit":"20000000","leverage":"15","keepMarginRate":"0.0400"},
                {"symbol":"BTCPERP","level":"9","startUnit":"20000000","endUnit":"30000000","leverage":"10","keepMarginRate":"0.0600"},
                {"symbol":"BTCPERP","level":"10","startUnit":"30000000","endUnit":"50000000","leverage":"8","keepMarginRate":"0.0800"},
                {"symbol":"BTCPERP","level":"11","startUnit":"50000000","endUnit":"100000000","leverage":"5","keepMarginRate":"0.1200"},
                {"symbol":"BTCPERP","level":"12","startUnit":"100000000","endUnit":"250000000","leverage":"4","keepMarginRate":"0.1500"},
                {"symbol":"BTCPERP","level":"13","startUnit":"250000000","endUnit":"500000000","leverage":"2","keepMarginRate":"0.3000"},
                {"symbol":"BTCPERP","level":"14","startUnit":"500000000","endUnit":"1000000000","leverage":"1","keepMarginRate":"0.6000"}
            ]
        """);

    /// <summary>
    /// Five minutes of spot candles. Eight columns: the open time in milliseconds, open, high, low, close, the volume
    /// in base currency, the turnover in quote currency, and the turnover converted to USDT.
    /// </summary>
    public static string SpotHistoryCandles => Envelope("""
        [
                ["1790358660000","83765.17","83765.17","83738.85","83738.85","0.701343","58742.42150798","58742.42150798"],
                ["1790358720000","83738.85","83738.85","83715.68","83715.7","0.100386","8404.36639961","8404.36639961"],
                ["1790358780000","83715.7","83728.46","83710.49","83723.94","0.66116","55352.83569164","55352.83569164"],
                ["1790358840000","83723.94","83734.53","83702","83727.14","0.761523","63757.37195462","63757.37195462"],
                ["1790358900000","83727.14","83740.8","83717.11","83740.8","0.125023","10468.90104126","10468.90104126"]
            ]
        """);

    /// <summary>
    /// The same five minutes of derivative candles. Seven columns - the USDT turnover is not there - and the first six
    /// are the same fields in the same order, which is what lets one reader serve both.
    /// </summary>
    public static string FuturesHistoryCandles => Envelope("""
        [
                ["1790358660000","83733.2","83733.2","83709","83709","0.7569","63370.46146"],
                ["1790358720000","83709","83709.1","83677.7","83677.8","5.3355","446549.44025"],
                ["1790358780000","83677.8","83700.9","83676.7","83691.8","4.193","350883.07327"],
                ["1790358840000","83691.8","83704.4","83668.5","83694.2","1.8871","157938.86312"],
                ["1790358900000","83694.2","83711.6","83688.1","83710.1","2.0763","173803.13488"]
            ]
        """);

    /// <summary>
    /// Five funding settlements of BTCUSDT, newest first, which is the order this endpoint answers in and the opposite
    /// of the order anything storing history wants.
    /// </summary>
    public static string FundingHistory => Envelope("""
        [
                {"symbol":"BTCUSDT","fundingRate":"0.000078","fundingTime":"1790352000000"},
                {"symbol":"BTCUSDT","fundingRate":"0.000053","fundingTime":"1790323200000"},
                {"symbol":"BTCUSDT","fundingRate":"0.000027","fundingTime":"1790294400000"},
                {"symbol":"BTCUSDT","fundingRate":"0.000044","fundingTime":"1790265600000"},
                {"symbol":"BTCUSDT","fundingRate":"0.000059","fundingTime":"1790236800000"}
            ]
        """);

    /// <summary>
    /// What the public stream answers a subscription with. The venue confirms each argument separately before any data
    /// arrives.
    /// </summary>
    public static string SubscribeAck(string instType, string channel, string instId) =>
        $$$"""{"event":"subscribe","arg":{"instType":"{{{instType}}}","channel":"{{{channel}}}","instId":"{{{instId}}}"}}""";

    /// <summary>
    /// What it answers a channel it does not publish. Recorded by subscribing to <c>candle1h</c> on the spot stream,
    /// which is the REST spelling of the same length and is refused here - the one place that refusal is visible.
    /// </summary>
    public static string SubscribeError =>
        """{"event":"error","arg":{"instType":"SPOT","channel":"candle1h","instId":"BTCUSDT"},"code":30016,"msg":"Param error","op":"subscribe"}""";

    /// <summary>A spot ticker, which carries the best bid and ask and nothing that is not a quote.</summary>
    public static string SpotTicker =>
        """{"action":"snapshot","arg":{"instType":"SPOT","channel":"ticker","instId":"BTCUSDT"},"data":[{"instId":"BTCUSDT","lastPr":"83715.7","open24h":"83812.27","high24h":"85250","low24h":"83172.57","change24h":"-0.00491","bidPr":"83715.69","askPr":"83715.7","bidSz":"0.074435","askSz":"2.145777","baseVolume":"2673.629245","quoteVolume":"225237773.321358","openUtc":"84409.37","changeUtc24h":"-0.00819","ts":"1790358774065"}],"ts":1790358774066}""";

    /// <summary>
    /// A derivative ticker, which carries the quote and the mark price, the index price, the funding rate and the time
    /// of the next settlement as well - so one subscription answers four kinds of question.
    /// </summary>
    public static string FuturesTicker =>
        """{"action":"snapshot","arg":{"instType":"USDT-FUTURES","channel":"ticker","instId":"BTCUSDT"},"data":[{"instId":"BTCUSDT","lastPr":"83692.3","bidPr":"83692.3","askPr":"83692.4","bidSz":"1.2043","askSz":"4.5377","open24h":"84052.9","high24h":"85220","low24h":"83121.1","change24h":"-0.00429","fundingRate":"0.0001","nextFundingTime":"1790380800000","markPrice":"83692.3","indexPrice":"83720.9225","holdingAmount":"31911.3455999999605","baseVolume":"30736.4509","quoteVolume":"2588367727.81816","openUtc":"84364.6","symbolType":"1","symbol":"BTCUSDT","ts":"1790358794983"}],"ts":1790358794987}""";

    /// <summary>A trade. The rows carry no symbol of their own and "side" is the taker's side.</summary>
    public static string SpotTrade =>
        """{"action":"update","arg":{"instType":"SPOT","channel":"trade","instId":"BTCUSDT"},"data":[{"ts":"1790358780904","price":"83715.7","size":"0.000012","side":"buy","tradeId":"1487438024184414208"}],"ts":1790358780904}""";

    /// <summary>The five-level book, which arrives as a fresh snapshot every time and never as a delta.</summary>
    public static string SpotBooks5 =>
        """{"action":"snapshot","arg":{"instType":"SPOT","channel":"books5","instId":"BTCUSDT"},"data":[{"asks":[["83715.7","2.145777"],["83718.23","0.959984"]],"bids":[["83715.69","0.074435"],["83715.68","0.002988"]],"ts":"1790358773905","seq":850167842345,"pseq":0}],"ts":1790358773907}""";

    /// <summary>
    /// A delta on the whole-book channel, in which a size of zero removes a level. This is the one book channel that
    /// sends deltas at all.
    /// </summary>
    public static string SpotBooksDelta =>
        """{"action":"update","arg":{"instType":"SPOT","channel":"books","instId":"BTCUSDT"},"data":[{"asks":[["83744.78","0"],["83747.24","0.238934"]],"bids":[],"ts":"1790358811400","seq":850169288983,"pseq":850169286126}],"ts":1790358811401}""";

    /// <summary>
    /// The opening snapshot of a candle subscription, cut from the hundreds of rows the venue really sends to the
    /// three that matter: the last of them is the candle being built and the others have already closed.
    /// </summary>
    public static string CandleSnapshot =>
        """{"action":"snapshot","arg":{"instType":"SPOT","channel":"candle1m","instId":"BTCUSDT"},"data":[["1790358660000","83765.17","83765.17","83738.85","83738.85","0.701343","58742.42150798","58742.42150798"],["1790358720000","83738.85","83738.85","83715.68","83715.7","0.100386","8404.36639961","8404.36639961"],["1790358780000","83715.7","83715.7","83715.7","83715.7","0","0","0"]]}""";

    /// <summary>
    /// A candle update. The venue republishes the candle being built as it changes and never says one has finished, so
    /// a bar is only closed when the open time advances - or by the clock.
    /// </summary>
    public static string CandleUpdate(string openMs, string close, string volume) =>
        $$"""{"action":"update","arg":{"instType":"SPOT","channel":"candle1m","instId":"BTCUSDT"},"data":[["{{openMs}}","83715.7","{{close}}","83710.49","{{close}}","{{volume}}","0","0"]],"ts":1790358780653}""";
}
