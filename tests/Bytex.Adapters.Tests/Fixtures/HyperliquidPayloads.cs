namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// What the live venue really answered, captured on 2026-09-25 against api.hyperliquid.xyz and its websocket.
/// <para>
/// Recorded rather than written, because every one of these payloads carries something that would have been got
/// wrong from a plausible hand-made copy: the universe's delisted entry that still holds its index, the candle rows
/// that carry both ends of the interval with the last one still forming, the funding stamps that are tens of
/// milliseconds past the hour, the account read whose maintenance margin is a fixed fraction of the notional, and
/// the fill stream that arrives on a channel named differently from the subscription that asked for it.
/// </para>
/// </summary>
internal static class HyperliquidPayloads
{
    /// <summary>
    /// The first six entries of the perpetual universe, byte for byte, with the venue's margin tables for them.
    /// <para>
    /// The fourth is a DELISTED contract, and it is here for the reason it is dangerous: the venue leaves it in the
    /// array, so DYDX after it is index 4 and not index 3. An adapter that filtered the delisted entries before
    /// numbering them would put every order after the gap on the wrong asset - a real order, accepted, in a
    /// contract nobody asked for.
    /// </para>
    /// </summary>
    public const string Meta = """
        {"universe":[{"szDecimals":5,"name":"BTC","maxLeverage":40,"marginTableId":56},{"szDecimals":4,"name":"ETH","maxLeverage":25,"marginTableId":55},{"szDecimals":2,"name":"ATOM","maxLeverage":5,"marginTableId":5},{"szDecimals":1,"name":"MATIC","maxLeverage":20,"marginTableId":20,"isDelisted":true},{"szDecimals":1,"name":"DYDX","maxLeverage":5,"marginTableId":5},{"szDecimals":2,"name":"SOL","maxLeverage":20,"marginTableId":54}],"marginTables":[[54,{"description":"tiered 20x (2)","marginTiers":[{"lowerBound":"0.0","maxLeverage":20},{"lowerBound":"70000000.0","maxLeverage":10}]}],[55,{"description":"tiered 25x","marginTiers":[{"lowerBound":"0.0","maxLeverage":25},{"lowerBound":"100000000.0","maxLeverage":15}]}],[56,{"description":"tiered 40x","marginTiers":[{"lowerBound":"0.0","maxLeverage":40},{"lowerBound":"150000000.0","maxLeverage":20}]}]],"collateralToken":0}
        """;

    /// <summary>
    /// Five hourly BTC candles as the venue sent them at 18:01 UTC.
    /// <para>
    /// Two things are recorded here that no hand-made copy would have. Each row carries BOTH ends of the interval -
    /// <c>t</c> opens it and <c>T</c> is its LAST MILLISECOND, 3599999 later rather than 3600000 - and the final row
    /// opened at 18:00 and had not closed when this was taken, so a reader that trusted the venue's list would
    /// report a candle as closed an hour before it was.
    /// </para>
    /// </summary>
    public const string BtcHourlyCandles = """
        [{"t":1790344800000,"T":1790348399999,"s":"BTC","i":"1h","o":"83912.0","c":"84010.0","h":"84039.0","l":"83133.0","v":"3997.94344","n":35095},{"t":1790348400000,"T":1790351999999,"s":"BTC","i":"1h","o":"84010.0","c":"83772.0","h":"84097.0","l":"83328.0","v":"2718.73729","n":20343},{"t":1790352000000,"T":1790355599999,"s":"BTC","i":"1h","o":"83772.0","c":"83728.0","h":"84114.0","l":"83700.0","v":"1753.01762","n":15687},{"t":1790355600000,"T":1790359199999,"s":"BTC","i":"1h","o":"83737.0","c":"83767.0","h":"83977.0","l":"83600.0","v":"910.98068","n":8461},{"t":1790359200000,"T":1790362799999,"s":"BTC","i":"1h","o":"83766.0","c":"83931.0","h":"83952.0","l":"83766.0","v":"243.50048","n":1829}]
        """;

    /// <summary>
    /// Three hourly funding settlements.
    /// <para>
    /// Note the stamps: 1790352000015, 1790355600045, 1790359200046. Fifteen, forty-five and forty-six milliseconds
    /// past the hour, never on it - so nothing that pages this may compute its next cursor from a boundary, and the
    /// interval here is one HOUR where the venues beside it settle every eight.
    /// </para>
    /// </summary>
    public const string BtcFunding = """
        [{"coin":"BTC","fundingRate":"0.0000125","premium":"-0.0001875303","time":1790352000015},{"coin":"BTC","fundingRate":"0.0000125","premium":"-0.0001547518","time":1790355600045},{"coin":"BTC","fundingRate":"0.0000125","premium":"-0.0002083901","time":1790359200046}]
        """;

    /// <summary>The top of a real BTC book. Twenty levels a side; five of the bids are kept here.</summary>
    public const string BtcBook = """
        {"coin":"BTC","time":1790358575700,"levels":[[{"px":"83767.0","sz":"6.87634","n":45},{"px":"83766.0","sz":"1.85737","n":8},{"px":"83765.0","sz":"1.2196","n":4},{"px":"83764.0","sz":"1.0718","n":5},{"px":"83763.0","sz":"1.30332","n":8}],[{"px":"83768.0","sz":"0.30543","n":3},{"px":"83769.0","sz":"1.25161","n":3},{"px":"83770.0","sz":"1.92106","n":7},{"px":"83771.0","sz":"0.99011","n":4},{"px":"83772.0","sz":"1.23158","n":5}]]}
        """;

    /// <summary>
    /// A real account, and the payload the maintenance-margin rule was measured from: 377667.0 of notional at the
    /// asset's published 40x maximum, and 4720.8375 of maintenance margin used - which is 377667.0 / (2 x 40) to
    /// the last digit, at the asset's MAXIMUM leverage and not at the 10x this account had chosen.
    /// </summary>
    public const string ClearinghouseState = """
        {"marginSummary":{"accountValue":"38252.7","totalNtlPos":"377667.0","totalRawUsd":"-339414.3","totalMarginUsed":"37766.7"},"crossMarginSummary":{"accountValue":"38252.7","totalNtlPos":"377667.0","totalRawUsd":"-339414.3","totalMarginUsed":"37766.7"},"crossMaintenanceMarginUsed":"4720.8375","withdrawable":"486.0","assetPositions":[{"type":"oneWay","position":{"coin":"BTC","szi":"4.5","leverage":{"type":"cross","value":10},"entryPx":"81285.0","positionValue":"377667.0","unrealizedPnl":"11884.306507","returnOnEquity":"0.3249007326","liquidationPx":"74501.802790436","marginUsed":"37766.7","maxLeverage":40,"cumFunding":{"allTime":"982.43637","sinceOpen":"982.43637","sinceChange":"0.0"}}}],"time":1790360864964}
        """;

    /// <summary>
    /// A real fill from a live account's own stream, with the cloid replaced by the one a test computes.
    /// <para>
    /// <c>crossed</c> is what says this account was the taker, and the <c>fee</c> on the original was NEGATIVE - a
    /// maker rebate, which is a number the venue really pays and not a sign error. The <c>coin</c> on the untouched
    /// original was <c>@142</c>: a SPOT pair, in the same list as the perpetual fills, which is why a fill is looked
    /// up by coin and a miss is not a warning.
    /// </para>
    /// </summary>
    public const string UserFillsChannel = """
        {"channel":"user","data":{"fills":[{"coin":"BTC","px":"83843.0","sz":"0.31975","side":"A","time":1790357129742,"startPosition":"1.5197585293","dir":"Sell","closedPnl":"86.62649561","hash":"0xaa843fb6a70b0a5dabfd044530cd4d020487009c420e292f4e4ceb09660ee448","oid":556657630865,"crossed":true,"fee":"18.01551309","tid":246814713305894,"cloid":"CLOID","feeToken":"USDC","twapId":null}]}}
        """;

    /// <summary>
    /// A real order update, with the coin and cloid replaced. The order is nested INSIDE the status wrapper, and the
    /// one-letter side is the venue's: <c>B</c> is a buy.
    /// </summary>
    public const string OrderUpdatesChannel = """
        {"channel":"orderUpdates","data":[{"order":{"coin":"BTC","side":"B","limitPx":"83000.0","sz":"0.01","oid":556696349186,"timestamp":1790360256851,"origSz":"0.01","cloid":"CLOID"},"status":"open","statusTimestamp":1790360256851}]}
        """;

    /// <summary>A real trade message, five trades in one frame, trimmed to two.</summary>
    public const string TradesChannel = """
        {"channel":"trades","data":[{"coin":"BTC","side":"B","px":"83770.0","sz":"0.00819","time":1790358604045,"hash":"0x0000000000000000000000000000000000000000000000000000000000000000","tid":437413095582311,"users":["0x10d944a35f99c141b9121ec3693000531825bef8","0x1a2e6afa298b1cc50939b4b2b0b430e3c1ef3459"]},{"coin":"BTC","side":"A","px":"83769.0","sz":"0.0048","time":1790358606027,"hash":"0xa7c8a1a1ff42f7b2a9420445311dcb0201b500879a4616844b914cf4be46d19d","tid":784469253141982,"users":["0xdfdac7220711df4e8c362db7d11eef2bd0c51188","0x1a2e6afa298b1cc50939b4b2b0b430e3c1ef3459"]}]}
        """;

    /// <summary>A real best-bid-and-offer message. The two sides are an ARRAY, not two named fields.</summary>
    public const string BboChannel = """
        {"channel":"bbo","data":{"coin":"BTC","time":1790358617881,"bbo":[{"px":"83757.0","sz":"22.61305","n":81},{"px":"83758.0","sz":"0.00016","n":1}]}}
        """;

    /// <summary>A real forming candle from the socket. Its coin field is <c>s</c>, not <c>coin</c>.</summary>
    public const string CandleChannel = """
        {"channel":"candle","data":{"t":1790358600000,"T":1790358659999,"s":"BTC","i":"1m","o":"83765.0","c":"83765.0","h":"83770.0","l":"83756.0","v":"0.3317","n":36}}
        """;

    /// <summary>And the next one, which is what says the one above has closed.</summary>
    public const string CandleChannelNext = """
        {"channel":"candle","data":{"t":1790358660000,"T":1790358719999,"s":"BTC","i":"1m","o":"83765.0","c":"83801.0","h":"83805.0","l":"83760.0","v":"1.2044","n":88}}
        """;

    /// <summary>
    /// A real asset context: the mark a position is liquidated against, the oracle price this venue computes from
    /// other exchanges, and the funding for the current hour.
    /// </summary>
    public const string ActiveAssetCtxChannel = """
        {"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":"0.0000125","openInterest":"39338.76246","prevDayPx":"84086.0","dayNtlVlm":"3096053627.5618200302","premium":"-0.0001348801","oraclePx":"83778.1","markPx":"83757.0","midPx":"83757.5","impactPxs":["83757.0","83766.8"],"dayBaseVlm":"36757.56691"}}}
        """;

    /// <summary>
    /// What the venue says about a bad signature: HTTP 200, with the refusal in the body, and it NAMES THE ADDRESS
    /// IT RECOVERED. That last part is what made the signing verifiable without a funded account - sign with a key
    /// nobody has used, and if the venue names that key's own address then every byte of the encoding matched.
    /// </summary>
    public const string ExchangeUnknownUser = """
        {"status":"err","response":"User or API Wallet 0xc06d73162e9bffbcfbf1da59c511002a8f9155e5 does not exist."}
        """;

    /// <summary>The refusal that means the digest was wrong rather than the account: the signature recovered nothing.</summary>
    public const string ExchangeBadSignature = """
        {"status":"err","response":"Unable to recover signer."}
        """;

    /// <summary>A nonce this account has already used, which the venue refuses outright.</summary>
    public const string ExchangeDuplicateNonce = """
        {"status":"err","response":"Invalid nonce: duplicate nonce 1700000000000"}
        """;

    /// <summary>An accepted order, as the venue answers one.</summary>
    public const string ExchangeOrderAccepted = """
        {"status":"ok","response":{"type":"order","data":{"statuses":[{"resting":{"oid":556696349186}}]}}}
        """;

    /// <summary>
    /// What <c>/info</c> says about a request type it does not know: not JSON at all, at HTTP 422. So nothing can
    /// read a code out of it, which is why this venue's exception carries a message and no code.
    /// </summary>
    public const string InfoUnknownType = "Failed to deserialize the JSON body into the target type";

    /// <summary>A real account's resting orders, which is an empty array on every account that was measured.</summary>
    public const string NoOpenOrders = "[]";
}
