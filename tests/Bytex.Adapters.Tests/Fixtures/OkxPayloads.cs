namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// OKX answers and stream messages.
/// <para>
/// The instrument records, the position tiers, the public stream messages and the field names are RECORDED FROM THE
/// LIVE VENUE on 2026-09-25 and are reproduced field for field, including the empty strings the venue writes where a
/// field does not apply - <c>"ctVal":""</c> on a spot pair, <c>"expTime":""</c> on a perpetual - because those are
/// what a record has to be read through. The candle and funding numbers are the venue's real shapes with values
/// chosen so that what a test expects can be worked out by hand.
/// </para>
/// <para>
/// The private answers - an order's life, the account, the positions - are shapes from the venue's own specification
/// and not recordings: every private endpoint needs a key, which is stated in this adapter's report rather than
/// implied by a fixture that looks like everything else here.
/// </para>
/// </summary>
internal static class OkxPayloads
{
    /// <summary>The envelope every answer arrives in; <c>code</c> is "0" on success whatever the HTTP status is.</summary>
    public static string Envelope(string data) => "{\"code\":\"0\",\"msg\":\"\",\"data\":" + data + "}";

    /// <summary>A refusal, which the venue sends with HTTP 200 for anything about an instrument.</summary>
    public static string Error(string code, string message) =>
        "{\"code\":\"" + code + "\",\"data\":[],\"msg\":\"" + message + "\"}";

    /// <summary>
    /// What the venue really says about an instrument it does not list - the same code and the same sentence for all
    /// three markets, and for an id that exists in a different market from the one being asked.
    /// </summary>
    public static readonly string NoSuchInstrument =
        Error("51001", "Instrument ID, Instrument ID code, or Spread ID doesn't exist.");

    // ----- catalogs -----

    /// <summary>BTC-USDT and ETH-USDT as the live venue describes them, and a pair the venue has stopped trading.</summary>
    public static readonly string SpotInstruments = Envelope("""
        [
          {"alias":"","auctionEndTime":"","baseCcy":"BTC","category":"1","contTdSwTime":"","ctMult":"","ctType":"","ctVal":"","ctValCcy":"","expTime":"","floatPxLmtPct":"0.005","freq":"","futureSettlement":false,"groupId":"12","initPxLmtPct":"","instCategory":"1","instFamily":"","instId":"BTC-USDT","instIdCode":3,"instType":"SPOT","lever":"10","listTime":"1611907686000","longPosRemainingQuota":"","lotSz":"0.00000001","maxIcebergSz":"9999999999.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"9999999999","maxMktAmt":"1000000","maxMktSz":"1000000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.01","maxStopSz":"1000000","maxTriggerSz":"9999999999.0000000000000000","maxTwapSz":"9999999999.0000000000000000","method":"","minSz":"0.00001","openType":"fix_price","optType":"","posLmtAmt":"","posLmtPct":"","preMktSwTime":"","quoteCcy":"USDT","rpiMinLevel":"3","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.1","tradeQuoteCcyList":["USDT"],"uly":"","upcChg":[]},
          {"alias":"","auctionEndTime":"","baseCcy":"ETH","category":"1","contTdSwTime":"","ctMult":"","ctType":"","ctVal":"","ctValCcy":"","expTime":"","floatPxLmtPct":"0.005","freq":"","futureSettlement":false,"groupId":"12","initPxLmtPct":"","instCategory":"1","instFamily":"","instId":"ETH-USDT","instIdCode":4,"instType":"SPOT","lever":"10","listTime":"1611907686000","longPosRemainingQuota":"","lotSz":"0.000001","maxIcebergSz":"999999999999.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"999999999999","maxMktAmt":"1000000","maxMktSz":"1000000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.01","maxStopSz":"1000000","maxTriggerSz":"999999999999.0000000000000000","maxTwapSz":"999999999999.0000000000000000","method":"","minSz":"0.0001","openType":"fix_price","optType":"","posLmtAmt":"","posLmtPct":"","preMktSwTime":"","quoteCcy":"USDT","rpiMinLevel":"3","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.01","tradeQuoteCcyList":["USDT"],"uly":"","upcChg":[]},
          {"alias":"","auctionEndTime":"","baseCcy":"OLD","category":"1","contTdSwTime":"","ctMult":"","ctType":"","ctVal":"","ctValCcy":"","expTime":"","floatPxLmtPct":"0.005","freq":"","futureSettlement":false,"groupId":"12","initPxLmtPct":"","instCategory":"1","instFamily":"","instId":"OLD-USDT","instIdCode":999999,"instType":"SPOT","lever":"10","listTime":"1611907686000","longPosRemainingQuota":"","lotSz":"0.00000001","maxIcebergSz":"9999999999.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"9999999999","maxMktAmt":"1000000","maxMktSz":"1000000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.01","maxStopSz":"1000000","maxTriggerSz":"9999999999.0000000000000000","maxTwapSz":"9999999999.0000000000000000","method":"","minSz":"0.00001","openType":"fix_price","optType":"","posLmtAmt":"","posLmtPct":"","preMktSwTime":"","quoteCcy":"USDT","rpiMinLevel":"3","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"","shortPosRemainingQuota":"","state":"suspend","stk":"","tickSz":"0.1","tradeQuoteCcyList":["USDT"],"uly":"","upcChg":[]}
        ]
        """);

    /// <summary>
    /// Two linear perpetuals and one inverse one. BTC-USD-SWAP is the inverse contract, and the only thing in the
    /// record that says so is <c>ctType</c>: nothing in the id distinguishes it from the linear contracts beside it.
    /// </summary>
    public static readonly string SwapInstruments = Envelope("""
        [
          {"alias":"","auctionEndTime":"","baseCcy":"","category":"1","contTdSwTime":"1611916860000","ctMult":"1","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","expTime":"","floatPxLmtPct":"0.005","freq":"","futureSettlement":false,"groupId":"4","initPxLmtPct":"0.02","instCategory":"1","instFamily":"BTC-USDT","instId":"BTC-USDT-SWAP","instIdCode":10459,"instType":"SWAP","lever":"100","listTime":"1573557408000","longPosRemainingQuota":"","lotSz":"0.01","maxIcebergSz":"100000000.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"100000000","maxMktAmt":"","maxMktSz":"35000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.01","maxStopSz":"35000","maxTriggerSz":"100000000.0000000000000000","maxTwapSz":"100000000.0000000000000000","method":"","minSz":"0.01","openType":"call_auction","optType":"","posLmtAmt":"250000","posLmtPct":"30","preMktSwTime":"","quoteCcy":"","rpiMinLevel":"3","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"USDT","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.1","tradeQuoteCcyList":[],"uly":"BTC-USDT","upcChg":[]},
          {"alias":"","auctionEndTime":"","baseCcy":"","category":"1","contTdSwTime":"1611916860000","ctMult":"1","ctType":"linear","ctVal":"0.1","ctValCcy":"ETH","expTime":"","floatPxLmtPct":"0.01","freq":"","futureSettlement":false,"groupId":"4","initPxLmtPct":"0.02","instCategory":"1","instFamily":"ETH-USDT","instId":"ETH-USDT-SWAP","instIdCode":10461,"instType":"SWAP","lever":"100","listTime":"1573557408000","longPosRemainingQuota":"","lotSz":"0.01","maxIcebergSz":"100000000.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"100000000","maxMktAmt":"","maxMktSz":"90000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.02","maxStopSz":"90000","maxTriggerSz":"100000000.0000000000000000","maxTwapSz":"100000000.0000000000000000","method":"","minSz":"0.01","openType":"call_auction","optType":"","posLmtAmt":"250000","posLmtPct":"30","preMktSwTime":"","quoteCcy":"","rpiMinLevel":"3","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"USDT","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.01","tradeQuoteCcyList":[],"uly":"ETH-USDT","upcChg":[]},
          {"alias":"","auctionEndTime":"","baseCcy":"","category":"1","contTdSwTime":"1611916860000","ctMult":"1","ctType":"inverse","ctVal":"100","ctValCcy":"USD","expTime":"","floatPxLmtPct":"0.005","freq":"","futureSettlement":false,"groupId":"4","initPxLmtPct":"0.02","instCategory":"1","instFamily":"BTC-USD","instId":"BTC-USD-SWAP","instIdCode":10458,"instType":"SWAP","lever":"100","listTime":"1535424203000","longPosRemainingQuota":"","lotSz":"0.1","maxIcebergSz":"100000000.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"100000000","maxMktAmt":"","maxMktSz":"49000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.01","maxStopSz":"49000","maxTriggerSz":"100000000.0000000000000000","maxTwapSz":"100000000.0000000000000000","method":"","minSz":"0.1","openType":"call_auction","optType":"","posLmtAmt":"250000","posLmtPct":"30","preMktSwTime":"","quoteCcy":"","rpiMinLevel":"2","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"BTC","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.1","tradeQuoteCcyList":[],"uly":"BTC-USD","upcChg":[]}
        ]
        """);

    /// <summary>
    /// The two dated BTC contracts of the same expiry: the linear one, whose id carries <c>_UM</c>, and the inverse
    /// one, whose id does not. Note what the ids are NOT - there is no BTC-USDT-261030 - and that the linear contract
    /// settles in USD while being sized in BTC.
    /// </summary>
    public static readonly string FuturesInstruments = Envelope("""
        [
          {"alias":"this_month","auctionEndTime":"","baseCcy":"","category":"1","contTdSwTime":"","ctMult":"1","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","expTime":"1793347200000","floatPxLmtPct":"0.02","freq":"","futureSettlement":false,"groupId":"5","initPxLmtPct":"0.05","instCategory":"1","instFamily":"BTC-USD_UM","instId":"BTC-USD_UM-261030","instIdCode":433707,"instType":"FUTURES","lever":"20","listTime":"1787904600714","longPosRemainingQuota":"","lotSz":"0.01","maxIcebergSz":"1000000.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"1000000","maxMktAmt":"","maxMktSz":"3000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.1","maxStopSz":"3000","maxTriggerSz":"1000000.0000000000000000","maxTwapSz":"1000000.0000000000000000","method":"","minSz":"0.01","openType":"","optType":"","posLmtAmt":"200000000","posLmtPct":"25","preMktSwTime":"","quoteCcy":"","rpiMinLevel":"2","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"USD","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.1","tradeQuoteCcyList":[],"uly":"BTC-USD","upcChg":[]},
          {"alias":"this_month","auctionEndTime":"","baseCcy":"","category":"1","contTdSwTime":"","ctMult":"1","ctType":"inverse","ctVal":"100","ctValCcy":"USD","expTime":"1793347200000","floatPxLmtPct":"0.01","freq":"","futureSettlement":true,"groupId":"5","initPxLmtPct":"0.05","instCategory":"1","instFamily":"BTC-USD","instId":"BTC-USD-261030","instIdCode":410297,"instType":"FUTURES","lever":"20","listTime":"1786695000686","longPosRemainingQuota":"","lotSz":"0.1","maxIcebergSz":"1000000.0000000000000000","maxLmtAmt":"20000000","maxLmtSz":"1000000","maxMktAmt":"","maxMktSz":"10000","maxPlatOICoinLmt":"","maxPlatOILmt":"","maxPxLmtPct":"0.05","maxStopSz":"10000","maxTriggerSz":"1000000.0000000000000000","maxTwapSz":"1000000.0000000000000000","method":"","minSz":"0.1","openType":"","optType":"","posLmtAmt":"200000000","posLmtPct":"25","preMktSwTime":"","quoteCcy":"","rpiMinLevel":"2","rpiMinPxBand":"20","ruleType":"normal","seriesId":"","settleCcy":"BTC","shortPosRemainingQuota":"","state":"live","stk":"","tickSz":"0.1","tradeQuoteCcyList":[],"uly":"BTC-USD","upcChg":[]}
        ]
        """);

    // ----- position tiers -----

    /// <summary>
    /// Tier one for both linear perpetual families, recorded live. One percent initial and 0.4 percent maintenance -
    /// which is what makes a hard-coded 5 and 2.5 percent wrong by a factor of five on the initial requirement and
    /// six on the maintenance one.
    /// </summary>
    public static readonly string SwapTiers = Envelope("""
        [
          {"baseMaxLoan":"","imr":"0.01","instFamily":"BTC-USDT","instId":"","maxLever":"100","maxSz":"1000","minSz":"0","mmr":"0.004","optMgnFactor":"0","quoteMaxLoan":"","tier":"1","uly":"BTC-USDT"},
          {"baseMaxLoan":"","imr":"0.01","instFamily":"ETH-USDT","instId":"","maxLever":"100","maxSz":"5000","minSz":"0","mmr":"0.004","optMgnFactor":"0","quoteMaxLoan":"","tier":"1","uly":"ETH-USDT"}
        ]
        """);

    /// <summary>
    /// Tier one for the linear dated BTC family, recorded live: 5 percent initial and 2 percent maintenance, at 20x.
    /// Five times the perpetual's requirement on the same coin, which is why one number cannot serve both markets.
    /// </summary>
    public static readonly string FuturesTiers = Envelope("""
        [
          {"baseMaxLoan":"","imr":"0.05","instFamily":"BTC-USD_UM","instId":"","maxLever":"20","maxSz":"4000","minSz":"0","mmr":"0.02","optMgnFactor":"0","quoteMaxLoan":"","tier":"1","uly":"BTC-USD"}
        ]
        """);

    /// <summary>What the venue says when a family has no tiers: a parameter error, not an empty answer.</summary>
    public static readonly string UnknownTierFamily = Error("51000", "Parameter instFamily error");

    // ----- candles -----
    //
    // The real nine-field row, newest first, stamped with the candle's OPEN in milliseconds and carrying the venue's
    // own confirmation in the last field. The test clock stands at 1700000000000 ms, which falls inside the minute
    // opening at 1699999980000 - so that row is the one still forming and is marked "0".

    /// <summary>Four one-minute spot candles: three closed and the one still forming.</summary>
    public const string SpotCandles = """
        {"code":"0","msg":"","data":[
          ["1699999980000","104","112","103","110","4","440","440","0"],
          ["1699999920000","103","108","102","104","3","312","312","1"],
          ["1699999860000","101","105","100","103","2","206","206","1"],
          ["1699999800000","100","102","99","101","1","101","101","1"]
        ]}
        """;

    /// <summary>
    /// The same four minutes of BTC-USDT-SWAP. The volume field is a number of CONTRACTS and the base-currency
    /// figure is the one beside it: 300 contracts of a 0.01 BTC contract is 3 BTC, which is the relation measured on
    /// the live venue (3498.64 contracts against 34.9864 BTC in one minute).
    /// </summary>
    public const string SwapCandles = """
        {"code":"0","msg":"","data":[
          ["1699999980000","104","112","103","110","400","4","440000","0"],
          ["1699999920000","103","108","102","104","300","3","312000","1"],
          ["1699999860000","101","105","100","103","200","2","206000","1"],
          ["1699999800000","100","102","99","101","100","1","101000","1"]
        ]}
        """;

    /// <summary>A window the venue holds nothing for, which it answers with success and an empty array.</summary>
    public const string NoCandles = """{"code":"0","msg":"","data":[]}""";

    /// <summary>
    /// A page of one-minute candles ending at <paramref name="newestOpenMs"/> and running back
    /// <paramref name="count"/> minutes, all confirmed. Used to make a paging loop really page: the venue answers at
    /// most 300 rows, so a full page is what tells the loop there is more behind it.
    /// </summary>
    public static string CandlePage(long newestOpenMs, int count)
    {
        System.Text.StringBuilder rows = new();
        for (int i = 0; i < count; i++)
        {
            long open = newestOpenMs - (i * 60_000L);
            if (rows.Length > 0)
            {
                rows.Append(',');
            }

            // A price that identifies the row, so a test can say which minute a bar came from.
            rows.Append(System.Globalization.CultureInfo.InvariantCulture, $"[\"{open}\",\"100\",\"100\",\"100\",\"100\",\"1\",\"1\",\"100\",\"1\"]");
        }

        return "{\"code\":\"0\",\"msg\":\"\",\"data\":[" + rows + "]}";
    }

    // ----- funding -----

    /// <summary>
    /// Three settlements, newest first, in the venue's real shape. The predicted rate and the realised one are
    /// deliberately different here: the venue publishes both and what an account was charged is the realised one.
    /// </summary>
    public const string FundingHistory = """
        {"code":"0","msg":"","data":[
          {"formulaType":"withRate","fundingRate":"0.0003","fundingTime":"1699999200000","instId":"BTC-USDT-SWAP","instType":"SWAP","method":"current_period","realizedRate":"0.0006"},
          {"formulaType":"withRate","fundingRate":"0.0002","fundingTime":"1699970400000","instId":"BTC-USDT-SWAP","instType":"SWAP","method":"current_period","realizedRate":"0.0004"},
          {"formulaType":"withRate","fundingRate":"0.0001","fundingTime":"1699941600000","instId":"BTC-USDT-SWAP","instType":"SWAP","method":"current_period","realizedRate":"0.0002"}
        ]}
        """;

    // ----- public stream, recorded live -----

    /// <summary>The venue's answer to a subscription, which carries no data of its own.</summary>
    public const string SubscribeAck = """
        {"event":"subscribe","arg":{"channel":"tickers","instId":"BTC-USDT"},"connId":"76defc81"}
        """;

    /// <summary>
    /// What the venue says about a channel that does not exist on the path it was asked on. This exact message is how
    /// the candle channels were found to live on the business socket rather than the public one.
    /// </summary>
    public const string CandleOnTheWrongSocket = """
        {"event":"error","msg":"Subscribe failed, wrong URL or channel:candle1m,instId:BTC-USDT doesn't exist. Please use the correct URL, channel and parameters referring to API document.","code":"60018","connId":"e1f69f02"}
        """;

    public const string SpotTicker = """
        {"arg":{"channel":"tickers","instId":"BTC-USDT"},"data":[{"instType":"SPOT","instId":"BTC-USDT","last":"83766.8","lastSz":"0.0000102","askPx":"83774.2","askSz":"0.25697131","bidPx":"83774.1","bidSz":"0.65302934","open24h":"84180.1","high24h":"85258.8","low24h":"83174.7","sodUtc0":"84409.9","sodUtc8":"83800","volCcy24h":"458903124.056360828","vol24h":"5449.95415651","ts":"1790358564077"}]}
        """;

    /// <summary>
    /// The same channel on a perpetual. The sizes here are CONTRACTS - 25 of a 0.01 BTC contract is a quarter of a
    /// bitcoin - which is the one thing that has to be converted before a quote leaves the adapter.
    /// </summary>
    public const string SwapTicker = """
        {"arg":{"channel":"tickers","instId":"BTC-USDT-SWAP"},"data":[{"instType":"SWAP","instId":"BTC-USDT-SWAP","last":"83766.8","lastSz":"1","askPx":"83774.2","askSz":"25","bidPx":"83774.1","bidSz":"50","open24h":"84180.1","high24h":"85258.8","low24h":"83174.7","sodUtc0":"84409.9","sodUtc8":"83800","volCcy24h":"458903124.056360828","vol24h":"5449.95415651","ts":"1790358564077"}]}
        """;

    public const string SpotTrade = """
        {"arg":{"channel":"trades","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","tradeId":"1062992715","px":"83772.6","sz":"0.0000178","side":"buy","ts":"1790358565298","count":"1","source":"1","seqId":81625379835}]}
        """;

    /// <summary>A trade on a perpetual: 300 contracts, which is three bitcoin.</summary>
    public const string SwapTrade = """
        {"arg":{"channel":"trades","instId":"BTC-USDT-SWAP"},"data":[{"instId":"BTC-USDT-SWAP","tradeId":"7000001","px":"83772.6","sz":"300","side":"sell","ts":"1790358565298","count":"1","source":"0","seqId":81625379836}]}
        """;

    /// <summary>The five-level book the venue pushes whole. A level is [price, size, deprecated, order count].</summary>
    public const string SpotBooks5 = """
        {"arg":{"channel":"books5","instId":"BTC-USDT"},"data":[{"asks":[["83774.2","0.25697131","0","6"],["83774.6","0.00001501","0","1"],["83775.4","0.02805105","0","2"],["83776.2","0.00298482","0","1"],["83777.6","0.08","0","1"]],"bids":[["83774.1","0.54571131","0","21"],["83772.6","0.08385569","0","2"],["83772.5","0.14753","0","2"],["83771.9","0.0000102","0","1"],["83771.1","0.15552303","0","1"]],"instId":"BTC-USDT","ts":"1790358564306","seqId":81625379489}]}
        """;

    /// <summary>The deep book's first message, which the venue labels a snapshot.</summary>
    public const string SpotBooksSnapshot = """
        {"arg":{"channel":"books","instId":"BTC-USDT"},"action":"snapshot","data":[{"asks":[["83800.9","0.41458754","0","10"],["83802","0.12","0","1"]],"bids":[["83800.8","0.00662505","0","16"],["83799.1","0.5","0","3"]],"instId":"BTC-USDT","ts":"1790358602106","seqId":81625399056}]}
        """;

    /// <summary>A change to the deep book, where a level at size zero has gone.</summary>
    public const string SpotBooksUpdate = """
        {"arg":{"channel":"books","instId":"BTC-USDT"},"action":"update","data":[{"asks":[["83802","0","0","0"]],"bids":[["83800.8","1.25","0","18"]],"instId":"BTC-USDT","ts":"1790358602306","seqId":81625399057,"prevSeqId":81625399056}]}
        """;

    public const string FundingRateTick = """
        {"arg":{"channel":"funding-rate","instId":"BTC-USDT-SWAP"},"data":[{"formulaType":"withRate","fundingRate":"0.0000263463215391","fundingTime":"1790380800000","impactValue":"20000.0000000000000000","instId":"BTC-USDT-SWAP","instType":"SWAP","interestRate":"0.0001000000000000","maxFundingRate":"0.00375","method":"current_period","minFundingRate":"-0.00375","nextFundingRate":"","nextFundingTime":"1790409600000","premium":"-0.0004010920207754","prevFundingTime":"1790352000000","settFundingRate":"0.0000341625964238","settState":"settled","ts":"1790358563915"}]}
        """;

    public const string MarkPriceTick = """
        {"arg":{"channel":"mark-price","instId":"BTC-USDT-SWAP"},"data":[{"instId":"BTC-USDT-SWAP","instType":"SWAP","markPx":"83679.8","ts":"1790358817778"}]}
        """;

    /// <summary>
    /// The index, which arrives under the INDEX's name rather than a contract's. Every loaded contract priced
    /// against BTC-USDT is told about this one message.
    /// </summary>
    public const string IndexTick = """
        {"arg":{"channel":"index-tickers","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","idxPx":"83700.1","high24h":"85258.8","low24h":"83174.7","open24h":"84180.1","sodUtc0":"84409.9","sodUtc8":"83800","ts":"1790358817778"}]}
        """;

    /// <summary>
    /// A candle from the business socket, in the same nine-field shape the REST endpoint returns. The last field is
    /// the venue saying the minute has closed.
    /// </summary>
    public const string ClosedCandle = """
        {"arg":{"channel":"candle1m","instId":"BTC-USDT"},"data":[["1699999920000","103","108","102","104","3","312","312","1"]]}
        """;

    /// <summary>The same minute while it is still forming, which the venue also sends and marks.</summary>
    public const string FormingCandle = """
        {"arg":{"channel":"candle1m","instId":"BTC-USDT"},"data":[["1699999980000","104","112","103","110","4","440","440","0"]]}
        """;

    /// <summary>A closed minute of a perpetual: 300 contracts in the volume field and 3 BTC beside it.</summary>
    public const string ClosedSwapCandle = """
        {"arg":{"channel":"candle1m","instId":"BTC-USDT-SWAP"},"data":[["1699999920000","103","108","102","104","300","3","312000","1"]]}
        """;

    // ----- private, from the venue's specification rather than recorded -----

    /// <summary>The venue's answer to a login, which is what the private subscriptions wait for.</summary>
    public const string LoginAck = """{"event":"login","code":"0","msg":"","connId":"a4d3ae55"}""";

    /// <summary>A login the venue refused, which it answers with the same code a bogus key gets over REST.</summary>
    public const string LoginRefused = """{"event":"error","msg":"Invalid apiKey","code":"60005","connId":"f6f5c56d"}""";

    /// <summary>An order the venue has accepted and is working.</summary>
    public static string OrderLive(string clientOrderId) => $$"""
        {"arg":{"channel":"orders","instType":"SPOT"},"data":[{"instType":"SPOT","instId":"BTC-USDT","ordId":"312269865356374016","clOrdId":"{{clientOrderId}}","tag":"","px":"100","sz":"1","ordType":"limit","side":"buy","posSide":"","tdMode":"cash","accFillSz":"0","fillPx":"","tradeId":"","fillSz":"0","fillTime":"","state":"live","avgPx":"","fee":"0","feeCcy":"USDT","reduceOnly":"false","uTime":"1700000000000","cTime":"1700000000000","amendResult":"","code":"0","msg":""}]}
        """;

    /// <summary>
    /// A fill. One message carries the state AND the trade that caused it, which is why an order's message can be
    /// both a fill and the terminal state after it.
    /// </summary>
    public static string OrderFilled(string clientOrderId) => $$"""
        {"arg":{"channel":"orders","instType":"SPOT"},"data":[{"instType":"SPOT","instId":"BTC-USDT","ordId":"312269865356374016","clOrdId":"{{clientOrderId}}","tag":"","px":"100","sz":"1","ordType":"limit","side":"buy","posSide":"","tdMode":"cash","accFillSz":"1","fillPx":"100","tradeId":"90957997","fillSz":"1","fillFee":"-0.08","fillFeeCcy":"USDT","fillTime":"1700000000500","execType":"M","state":"filled","avgPx":"100","fee":"-0.08","feeCcy":"USDT","reduceOnly":"false","uTime":"1700000000500","cTime":"1700000000000","amendResult":"","code":"0","msg":""}]}
        """;

    /// <summary>A fill of a perpetual, whose size is a number of contracts.</summary>
    public static string SwapOrderFilled(string clientOrderId) => $$"""
        {"arg":{"channel":"orders","instType":"SWAP"},"data":[{"instType":"SWAP","instId":"BTC-USDT-SWAP","ordId":"312269865356374017","clOrdId":"{{clientOrderId}}","tag":"","px":"100","sz":"300","ordType":"limit","side":"buy","posSide":"net","tdMode":"cross","accFillSz":"300","fillPx":"100","tradeId":"90957998","fillSz":"300","fillFee":"-0.15","fillFeeCcy":"USDT","fillTime":"1700000000500","execType":"T","state":"filled","avgPx":"100","fee":"-0.15","feeCcy":"USDT","reduceOnly":"false","uTime":"1700000000500","cTime":"1700000000000","amendResult":"","code":"0","msg":""}]}
        """;

    public static string OrderCanceled(string clientOrderId) => $$"""
        {"arg":{"channel":"orders","instType":"SPOT"},"data":[{"instType":"SPOT","instId":"BTC-USDT","ordId":"312269865356374016","clOrdId":"{{clientOrderId}}","px":"100","sz":"1","ordType":"limit","side":"buy","tdMode":"cash","accFillSz":"0","fillSz":"0","state":"canceled","avgPx":"","fee":"0","feeCcy":"USDT","reduceOnly":"false","uTime":"1700000001000","cTime":"1700000000000","amendResult":"","code":"0","msg":""}]}
        """;

    /// <summary>The unified account's balances, which the venue nests one level down under <c>details</c>.</summary>
    public const string AccountBalance = """
        {"code":"0","msg":"","data":[{"uTime":"1700000000000","totalEq":"100000","adjEq":"100000","details":[
          {"ccy":"USDT","eq":"100000","cashBal":"100000","availEq":"90000","availBal":"90000","frozenBal":"10000","uTime":"1700000000000"},
          {"ccy":"BTC","eq":"2","cashBal":"2","availEq":"2","availBal":"2","frozenBal":"0","uTime":"1700000000000"}
        ]}]}
        """;

    /// <summary>A long position of 300 contracts, which is three bitcoin, and a flat one.</summary>
    public const string Positions = """
        {"code":"0","msg":"","data":[
          {"instType":"SWAP","instId":"BTC-USDT-SWAP","mgnMode":"cross","posSide":"net","pos":"300","avgPx":"100","uTime":"1700000000000","lever":"3"},
          {"instType":"SWAP","instId":"ETH-USDT-SWAP","mgnMode":"cross","posSide":"net","pos":"0","avgPx":"","uTime":"1700000000000","lever":"3"}
        ]}
        """;

    /// <summary>A short position, which the venue signs negative.</summary>
    public const string ShortPosition = """
        {"code":"0","msg":"","data":[
          {"instType":"SWAP","instId":"BTC-USDT-SWAP","mgnMode":"cross","posSide":"net","pos":"-300","avgPx":"100","uTime":"1700000000000","lever":"3"}
        ]}
        """;

    /// <summary>
    /// The answer to a placed order, which is an array even for one order and carries the per-order verdict in
    /// <c>sCode</c>. A refused order arrives with an envelope code of 0 and an sCode of its own.
    /// </summary>
    public static string OrderPlaced(string clientOrderId) =>
        Envelope($"[{{\"clOrdId\":\"{clientOrderId}\",\"ordId\":\"312269865356374016\",\"tag\":\"\",\"sCode\":\"0\",\"sMsg\":\"\"}}]");

    /// <summary>An order the venue refused, inside a successful envelope.</summary>
    public static string OrderRefused(string clientOrderId, string code, string message) =>
        Envelope($"[{{\"clOrdId\":\"{clientOrderId}\",\"ordId\":\"\",\"tag\":\"\",\"sCode\":\"{code}\",\"sMsg\":\"{message}\"}}]");

    /// <summary>An amendment the venue took.</summary>
    public static string Amended(string clientOrderId) =>
        Envelope($"[{{\"clOrdId\":\"{clientOrderId}\",\"ordId\":\"312269865356374016\",\"reqId\":\"\",\"sCode\":\"0\",\"sMsg\":\"\"}}]");

    /// <summary>One open order, as the pending-orders endpoint returns it.</summary>
    public static string PendingOrders(string clientOrderId) => $$"""
        {"code":"0","msg":"","data":[{"instType":"SPOT","instId":"BTC-USDT","ordId":"312269865356374016","clOrdId":"{{clientOrderId}}","px":"100","sz":"1","ordType":"limit","side":"buy","tdMode":"cash","accFillSz":"0","fillSz":"0","state":"live","avgPx":"","fee":"0","feeCcy":"USDT","reduceOnly":"false","uTime":"1700000000000","cTime":"1700000000000"}]}
        """;

    /// <summary>One fill, as the fills endpoint returns it. The fee is negative because the venue charged it.</summary>
    public static string Fills(string clientOrderId) => $$"""
        {"code":"0","msg":"","data":[{"instType":"SPOT","instId":"BTC-USDT","tradeId":"90957997","ordId":"312269865356374016","clOrdId":"{{clientOrderId}}","billId":"1111","fillPx":"100","fillSz":"1","side":"buy","execType":"M","feeCcy":"USDT","fee":"-0.08","ts":"1700000000500"}]}
        """;

    /// <summary>One fill of a perpetual, whose size is a number of contracts.</summary>
    public static string SwapFills(string clientOrderId) => $$"""
        {"code":"0","msg":"","data":[{"instType":"SWAP","instId":"BTC-USDT-SWAP","tradeId":"90957998","ordId":"312269865356374017","clOrdId":"{{clientOrderId}}","billId":"1112","fillPx":"100","fillSz":"300","side":"buy","execType":"T","feeCcy":"USDT","fee":"-0.15","ts":"1700000000500"}]}
        """;

    /// <summary>The account's own configuration, which is what the key report reads.</summary>
    public const string AccountConfig = """
        {"code":"0","msg":"","data":[{"uid":"44705892343619584","acctLv":"2","posMode":"net_mode","autoLoan":false,"greeksType":"PA","level":"Lv1","levelTmp":"","ctIsoMode":"automatic","mgnIsoMode":"automatic","spotOffsetType":"","roleType":"0","traderInsts":[],"opAuth":"0","kycLv":"3","label":"bytex","ip":"1.2.3.4","perm":"read_only,trade","mainUid":"44705892343619584"}]}
        """;
}
