using System.Globalization;

namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// Kraken answers and stream messages, RECORDED FROM THE LIVE VENUE on 2026-09-25. Both of Kraken's platforms serve
/// their catalogs, candles, fee schedules, funding history and public sockets without a key, so everything here
/// except the private-channel shapes is a real answer with its fields and its numbers untouched.
/// <para>
/// The private shapes - the spot <c>executions</c> and <c>balances</c> channels, and the futures
/// <c>open_orders</c>, <c>fills</c> and <c>balances</c> feeds - are the venue's published contract and could not be
/// recorded: the spot socket answers an untokened subscription with "ESession:Invalid session" and the futures
/// socket answers an unsigned one with "Failed to subscribe to authenticated feed", in both cases before looking at
/// anything else in the message. They are marked where they appear.
/// </para>
/// </summary>
internal static class KrakenPayloads
{
    /// <summary>The spot envelope: an error ARRAY that is empty on success, and a result beside it.</summary>
    public static string Envelope(string result) => "{\"error\":[],\"result\":" + result + "}";

    /// <summary>
    /// A spot refusal. The venue answers HTTP 200 while doing this, so a caller reading the status learns nothing -
    /// which is why the adapter reads the array whatever the status says.
    /// </summary>
    public static string Error(string token) => "{\"error\":[\"" + token + "\"],\"result\":null}";

    // ----- spot: the catalog -----

    /// <summary>
    /// Five pairs of <c>/0/public/AssetPairs</c> exactly as the live venue returned them, chosen for what each one
    /// proves:
    /// <para>
    /// <c>XXBTZUSD</c> and <c>XDGUSD</c> are the two assets whose <c>wsname</c> nothing on the venue accepts -
    /// <c>XBT/USD</c> and <c>XDG/USD</c> are both refused by REST and by the socket, and <c>BTC/USD</c> and
    /// <c>DOGE/USD</c> are what work. <c>XETHXXBT</c> is a pair where the legacy code is the QUOTE rather than the
    /// base. <c>SOLUSD</c> is an ordinary pair that needs no substitution at all. <c>ACAEUR</c> is
    /// <c>cancel_only</c>, which is one of the 97 statuses that are not "online".
    /// </para>
    /// <para>
    /// Note the <c>fees</c> and <c>fees_maker</c> arrays: empty, on every pair. They measured empty on all 1451 the
    /// venue lists and on the <c>?info=fees</c> variant as well, so there is no per-pair rate to read anywhere.
    /// </para>
    /// </summary>
    public static readonly string AssetPairs = Envelope("""
        {
          "XXBTZUSD":{"altname":"XBTUSD","wsname":"XBT/USD","aclass_base":"currency","base":"XXBT","aclass_quote":"currency","quote":"ZUSD","lot":"unit","cost_decimals":5,"pair_decimals":1,"lot_decimals":8,"lot_multiplier":1,"leverage_buy":[2,3,4,5,6,7,8,9,10],"leverage_sell":[2,3,4,5,6,7,8,9,10],"fees":[],"fees_maker":[],"fee_volume_currency":"ZUSD","margin_call":80,"margin_stop":40,"ordermin":"0.00005","costmin":"0.5","tick_size":"0.1","status":"online","execution_venue":"international","long_position_limit":350,"short_position_limit":250},
          "XDGUSD":{"altname":"XDGUSD","wsname":"XDG/USD","aclass_base":"currency","base":"XXDG","aclass_quote":"currency","quote":"ZUSD","lot":"unit","cost_decimals":9,"pair_decimals":7,"lot_decimals":8,"lot_multiplier":1,"leverage_buy":[2,3,4,5,6,7,8,9,10],"leverage_sell":[2,3,4,5,6,7,8,9,10],"fees":[],"fees_maker":[],"fee_volume_currency":"ZUSD","margin_call":80,"margin_stop":40,"ordermin":"50","costmin":"0.5","tick_size":"0.0000001","status":"online","execution_venue":"international","long_position_limit":30000000,"short_position_limit":30000000},
          "XETHXXBT":{"altname":"ETHXBT","wsname":"ETH/XBT","aclass_base":"currency","base":"XETH","aclass_quote":"currency","quote":"XXBT","lot":"unit","cost_decimals":10,"pair_decimals":6,"lot_decimals":8,"lot_multiplier":1,"leverage_buy":[2,3,4,5],"leverage_sell":[2,3,4,5],"fees":[],"fees_maker":[],"fee_volume_currency":"ZUSD","margin_call":80,"margin_stop":40,"ordermin":"0.001","costmin":"0.00002","tick_size":"0.000001","status":"online","execution_venue":"international","long_position_limit":1000,"short_position_limit":800},
          "SOLUSD":{"altname":"SOLUSD","wsname":"SOL/USD","aclass_base":"currency","base":"SOL","aclass_quote":"currency","quote":"ZUSD","lot":"unit","cost_decimals":5,"pair_decimals":2,"lot_decimals":8,"lot_multiplier":1,"leverage_buy":[2,3,4,5,6,7,8,9,10],"leverage_sell":[2,3,4,5,6,7,8,9,10],"fees":[],"fees_maker":[],"fee_volume_currency":"ZUSD","margin_call":80,"margin_stop":40,"ordermin":"0.06","costmin":"0.5","tick_size":"0.01","status":"online","execution_venue":"international","long_position_limit":37000,"short_position_limit":24000},
          "ACAEUR":{"altname":"ACAEUR","wsname":"ACA/EUR","aclass_base":"currency","base":"ACA","aclass_quote":"currency","quote":"ZEUR","lot":"unit","cost_decimals":5,"pair_decimals":5,"lot_decimals":8,"lot_multiplier":1,"leverage_buy":[],"leverage_sell":[],"fees":[],"fees_maker":[],"fee_volume_currency":"ZUSD","margin_call":80,"margin_stop":40,"ordermin":"12000","costmin":"0.45","tick_size":"0.00001","status":"cancel_only","execution_venue":"international"}
        }
        """);

    /// <summary>What the venue answers when asked about a pair it does not list.</summary>
    public static readonly string UnknownPair = Error("EQuery:Unknown asset pair");

    /// <summary>
    /// Five assets of <c>/0/public/Assets</c> exactly as the live venue returned them. This is the endpoint that
    /// says what a private balance's key means: a balance comes back under the asset ID - <c>ZUSD</c>, <c>XXBT</c> -
    /// and every pair on the venue is quoted in the altname beside it.
    /// <para>
    /// <c>XXBT</c> and <c>XXDG</c> are the two whose altname is itself a code the venue no longer answers to, so
    /// both corrections run: the id becomes <c>XBT</c> and then <c>BTC</c>. <c>SOL</c> is an asset whose id is
    /// already its code, which is 838 of the 849 the venue lists.
    /// </para>
    /// </summary>
    public static readonly string Assets = Envelope("""
        {
          "XXBT":{"aclass":"currency","altname":"XBT","decimals":10,"display_decimals":5,"collateral_value":0.99,"status":"enabled","margin_rate":"0.01"},
          "XXDG":{"aclass":"currency","altname":"XDG","decimals":8,"display_decimals":2,"collateral_value":0.925,"status":"enabled","margin_rate":"0.02"},
          "XETH":{"aclass":"currency","altname":"ETH","decimals":10,"display_decimals":5,"collateral_value":0.99,"status":"enabled","margin_rate":"0.02"},
          "ZUSD":{"aclass":"currency","altname":"USD","decimals":4,"display_decimals":2,"collateral_value":1.0,"status":"enabled","margin_rate":"0.036"},
          "SOL":{"aclass":"currency","altname":"SOL","decimals":10,"display_decimals":5,"collateral_value":0.925,"status":"enabled","margin_rate":"0.02"}
        }
        """);

    // ----- spot: candles -----

    /// <summary>
    /// Four hourly candles of <c>/0/public/OHLC</c> as the live venue returned them, with the <c>last</c> cursor it
    /// sent with them.
    /// <para>
    /// Two things this fixture exists to pin. The row is
    /// <c>[open time in SECONDS, open, high, low, close, vwap, volume, count]</c> - so the CLOSE is the fifth field
    /// and reading the third as a close, which the KuCoin spot row invites, would silently swap two real prices.
    /// And the last row is the candle STILL FORMING: <c>last</c> here is 1790355600, one interval earlier than the
    /// final row's 1790359200.
    /// </para>
    /// </summary>
    public static readonly string Ohlc = Envelope("""
        {
          "BTC/USD":[
            [1790348400,"84001.9","84075.9","83351.5","83777.5","83714.6","159.04484404",9205],
            [1790352000,"83777.6","84097.0","83706.0","83732.6","83909.9","177.10097004",7919],
            [1790355600,"83732.7","83969.8","83612.9","83765.4","83766.9","81.13696690",5879],
            [1790359200,"83765.3","84050.3","83765.3","84030.6","83921.9","53.72114923",2375]
          ],
          "last":1790355600
        }
        """);

    /// <summary>The open time of the first candle of <see cref="Ohlc"/>, in seconds.</summary>
    public const long OhlcFirstOpenSeconds = 1790348400;

    /// <summary>The open time of the forming candle of <see cref="Ohlc"/>, which is its last row.</summary>
    public const long OhlcFormingOpenSeconds = 1790359200;

    /// <summary>
    /// Three trades of <c>/0/public/Trades</c> as the live venue returned them. The time is in FRACTIONAL SECONDS,
    /// which is the field worth a fixture: read as whole seconds, the second and third trades here would share a
    /// timestamp with each other and lose their order.
    /// </summary>
    public static readonly string Trades = Envelope("""
        {
          "BTC/USD":[
            ["83930.30000","0.00005100",1790359840.9392912,"s","l","",109118758],
            ["83928.00000","0.00044682",1790359841.1382287,"b","m","",109118759],
            ["83935.40000","0.00551352",1790359841.1382287,"b","m","",109118760]
          ],
          "last":"1790359841138228746"
        }
        """);

    // ----- spot: the public socket -----

    /// <summary>The banner the venue sends on connecting, before anything is subscribed.</summary>
    public const string Status = """
        {"channel":"status","type":"update","data":[{"version":"2.0.10","system":"online","api_version":"v2","connection_id":18125794168470749634,"upcoming_maintenance":[],"emergency":[]}]}
        """;

    public const string SubscribeAck = """
        {"method":"subscribe","result":{"channel":"trade","snapshot":false,"symbol":"BTC/USD"},"success":true,"time_in":"2026-09-25T17:48:24.745996Z","time_out":"2026-09-25T17:48:24.746032Z"}
        """;

    /// <summary>
    /// What the venue says when asked to subscribe to a pair by the name its OWN CATALOG publishes. This is the
    /// measured refusal of <c>XBT/USD</c>, the <c>wsname</c> of <c>XXBTZUSD</c>.
    /// </summary>
    public const string SubscribeRefusedLegacyName = """
        {"error":"Currency pair not supported XBT/USD","method":"subscribe","success":false,"symbol":"XBT/USD","time_in":"2026-09-25T17:48:54.701041Z","time_out":"2026-09-25T17:48:54.701062Z"}
        """;

    /// <summary>
    /// What the PUBLIC host says when asked for a private channel, and the reason this venue needs two sockets. The
    /// mirror image - a public channel asked of the private host - is
    /// <see cref="PublicRefusedOnPrivateHost"/>.
    /// </summary>
    public const string PrivateRefusedOnPublicHost = """
        {"error":"Private data and trading are unavailable on this endpoint. Try ws-auth.kraken.com","method":"subscribe","success":false,"time_in":"2026-09-25T17:58:01.484193Z","time_out":"2026-09-25T17:58:01.484212Z"}
        """;

    /// <inheritdoc cref="PrivateRefusedOnPublicHost"/>
    public const string PublicRefusedOnPrivateHost = """
        {"error":"Public market data subscriptions are unavailable on this endpoint. Try ws.kraken.com","method":"subscribe","success":false,"time_in":"2026-09-25T17:58:17.423598Z","time_out":"2026-09-25T17:58:17.423624Z"}
        """;

    public const string Ticker = """
        {"channel":"ticker","type":"update","data":[{"symbol":"BTC/USD","bid":83744.2,"bid_qty":0.56251979,"ask":83744.3,"ask_qty":0.00044780,"last":83744.3,"volume":3156.84782110,"vwap":84174.6,"low":83163.6,"high":85247.4,"change":-429.3,"change_pct":-0.51,"trades":135262,"timestamp":"2026-09-25T17:48:26.844359Z"}]}
        """;

    public const string Trade = """
        {"channel":"trade","type":"update","data":[{"symbol":"BTC/USD","side":"buy","price":83744.3,"qty":0.00044780,"ord_type":"limit","trade_id":109116693,"timestamp":"2026-09-25T17:48:26.844359Z"}]}
        """;

    public const string BookSnapshot = """
        {"channel":"book","type":"snapshot","data":[{"symbol":"BTC/USD","bids":[{"price":83744.2,"qty":0.56251979},{"price":83743.4,"qty":0.10052207},{"price":83743.3,"qty":1.50816106}],"asks":[{"price":83744.3,"qty":0.06223911},{"price":83745.5,"qty":0.17911426},{"price":83746.2,"qty":0.10052207}],"checksum":3832923880,"timestamp":"2026-09-25T17:48:24.799230Z"}]}
        """;

    /// <summary>
    /// A book delta that does NOT touch the top of either side. It is here because a client reading a quote
    /// straight off a delta would publish 83743.4 as the best bid, which it is not: the snapshot above has 83744.2
    /// above it and this message says nothing about that level.
    /// </summary>
    public const string BookUpdateBelowTop = """
        {"channel":"book","type":"update","data":[{"symbol":"BTC/USD","bids":[{"price":83743.4,"qty":0.85713986}],"asks":[],"checksum":977902900,"timestamp":"2026-09-25T17:48:24.880679Z"}]}
        """;

    /// <summary>A book delta that removes the best bid, which a zero quantity is how the venue says.</summary>
    public const string BookUpdateRemovesTop = """
        {"channel":"book","type":"update","data":[{"symbol":"BTC/USD","bids":[{"price":83744.2,"qty":0.0}],"asks":[],"checksum":977902901,"timestamp":"2026-09-25T17:48:25.880679Z"}]}
        """;

    /// <summary>
    /// A candle on the <c>ohlc</c> channel. Both times are the venue's: <c>interval_begin</c> is the start of the
    /// interval and <c>timestamp</c> is its END - which no other venue here publishes, and which is why a bar off
    /// this channel carries the venue's own close rather than a computed one.
    /// </summary>
    public static string Candle(string intervalBegin, string close, string open, string high, string low, string closePrice, string volume, int interval = 1) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"channel":"ohlc","type":"update","timestamp":"{{close}}","data":[{"symbol":"BTC/USD","open":{{open}},"high":{{high}},"low":{{low}},"close":{{closePrice}},"trades":33,"volume":{{volume}},"vwap":83736.4,"interval_begin":"{{intervalBegin}}","interval":{{interval}},"timestamp":"{{close}}"}]}
            """);

    /// <summary>One real candle message, unedited, so the shape above is known to be the shape the venue sends.</summary>
    public const string CandleRecorded = """
        {"channel":"ohlc","type":"update","timestamp":"2026-09-25T17:48:26.844359566Z","data":[{"symbol":"BTC/USD","open":83729.5,"high":83745.2,"low":83729.4,"close":83744.3,"trades":33,"volume":0.21155628,"vwap":83736.4,"interval_begin":"2026-09-25T17:48:00.000000000Z","interval":1,"timestamp":"2026-09-25T17:49:00.000000Z"}]}
        """;

    // ----- spot: the private socket. PUBLISHED SHAPES, NOT RECORDED -----

    /// <summary>
    /// The refusal the private host gives an untokened subscription, which IS recorded - it is why the shapes below
    /// it are not.
    /// </summary>
    public const string PrivateSessionRefused = """
        {"channel":"executions","error":"ESession:Invalid session","method":"subscribe","status":"error","success":false,"time_in":"2026-09-25T17:58:09.493462Z","time_out":"2026-09-25T17:58:09.493897Z"}
        """;

    /// <summary>The socket token answer. PUBLISHED SHAPE.</summary>
    public static readonly string WebSocketsToken = Envelope("""{"token":"a-short-lived-token","expires":900}""");

    /// <summary>An order's acceptance on the <c>executions</c> channel. PUBLISHED SHAPE.</summary>
    public static string Execution(string clientOrderId, string execType, string extra = "") =>
        "{\"channel\":\"executions\",\"type\":\"update\",\"data\":[{\"exec_type\":\"" + execType
        + "\",\"order_id\":\"OQCLML-BW3P3-BUCMWZ\",\"cl_ord_id\":\"" + clientOrderId
        + "\",\"symbol\":\"BTC/USD\",\"side\":\"buy\",\"order_type\":\"limit\",\"order_qty\":0.5,\"cum_qty\":0,"
        + "\"limit_price\":50000,\"order_status\":\"new\",\"timestamp\":\"2026-09-25T17:49:00.000000Z\"" + extra + "}]}";

    /// <summary>A fill on the <c>executions</c> channel. PUBLISHED SHAPE.</summary>
    public static string Fill(string clientOrderId, string lastQty, string lastPrice, string fees, string liquidity = "t") =>
        "{\"channel\":\"executions\",\"type\":\"update\",\"data\":[{\"exec_type\":\"trade\",\"exec_id\":\"TRADE-1\","
        + "\"order_id\":\"OQCLML-BW3P3-BUCMWZ\",\"cl_ord_id\":\"" + clientOrderId
        + "\",\"symbol\":\"BTC/USD\",\"side\":\"buy\",\"last_qty\":" + lastQty + ",\"last_price\":" + lastPrice
        + ",\"fees\":" + fees + ",\"liquidity_ind\":\"" + liquidity + "\",\"order_status\":\"filled\","
        + "\"timestamp\":\"2026-09-25T17:49:05.000000Z\"}]}";

    /// <summary>An account's balances over REST. PUBLISHED SHAPE of <c>/0/private/BalanceEx</c>.</summary>
    public static readonly string BalanceEx = Envelope("""
        {"ZUSD":{"balance":"100000.0000","hold_trade":"250.0000"},"XXBT":{"balance":"1.5000000000","hold_trade":"0.0000000000"}}
        """);

    /// <summary>The answer to <c>/0/private/AddOrder</c>. PUBLISHED SHAPE.</summary>
    public static readonly string AddOrder = Envelope("""
        {"descr":{"order":"buy 0.50000000 XBTUSD @ limit 50000.0"},"txid":["OQCLML-BW3P3-BUCMWZ"]}
        """);

    // ----- futures: the catalog -----

    /// <summary>
    /// Five contracts of <c>/derivatives/api/v3/instruments</c> exactly as the live venue returned them, with only
    /// the per-platform margin schedules and the permission lists dropped for length. Each one proves something:
    /// <para>
    /// <c>PF_XBTUSD</c> is a perpetual of the type this adapter offers, and its margin schedule is keyed by
    /// <c>numNonContractUnits</c>. <c>PF_DOGEUSD</c> is another, with a different first tier, so a test on bitcoin
    /// alone would pass with a fixed margin. <c>FF_XBTUSD_261225</c> is a DATED contract of the SAME
    /// <c>flexible_futures</c> type - the reason the class cannot be read off the type - and carries
    /// <c>lastTradingTime</c>, which is the field that says so. <c>PI_XBTUSD</c> and <c>FI_XBTUSD_261225</c> are
    /// coin-margined, this adapter does not offer them, and their schedules are keyed by <c>contracts</c> instead.
    /// </para>
    /// </summary>
    public const string Instruments = """
        {"result":"success","instruments":[
          {"symbol":"PF_XBTUSD","type":"flexible_futures","tickSize":1,"contractSize":1,"tradeable":true,"impactMidSize":0.08,"maxPositionSize":1200.0,"openingDate":"2022-03-22T13:15:36Z","marginLevels":[{"numNonContractUnits":0.0,"initialMargin":0.01,"maintenanceMargin":0.005},{"numNonContractUnits":1000000.0,"initialMargin":0.02,"maintenanceMargin":0.01},{"numNonContractUnits":3000000.0,"initialMargin":0.04,"maintenanceMargin":0.02}],"fundingRateCoefficient":8,"maxRelativeFundingRate":0.005,"isin":"GB00BQ84JX13","contractValueTradePrecision":4,"postOnly":false,"feeScheduleUid":"723888f7-0a8e-4183-8648-f920a22339e3","mtf":true,"base":"BTC","quote":"USD","pair":"BTC:USD","category":"Layer 1","tradfi":false,"isExpired":false},
          {"symbol":"PF_DOGEUSD","type":"flexible_futures","tickSize":0.00001,"contractSize":1,"tradeable":true,"impactMidSize":45000.0,"maxPositionSize":200000000.0,"openingDate":"2022-06-20T12:57:30Z","marginLevels":[{"numNonContractUnits":0.0,"initialMargin":0.02,"maintenanceMargin":0.01},{"numNonContractUnits":2000000.0,"initialMargin":0.04,"maintenanceMargin":0.02}],"fundingRateCoefficient":8,"maxRelativeFundingRate":0.005,"isin":"GB00BQ84K163","contractValueTradePrecision":0,"postOnly":false,"feeScheduleUid":"723888f7-0a8e-4183-8648-f920a22339e3","mtf":true,"base":"DOGE","quote":"USD","pair":"DOGE:USD","category":"Community","tradfi":false,"isExpired":false},
          {"symbol":"FF_XBTUSD_261225","type":"flexible_futures","lastTradingTime":"2026-12-25T08:00:00Z","tickSize":1,"contractSize":1,"tradeable":true,"impactMidSize":0.05,"maxPositionSize":600.0,"openingDate":"2026-05-22T08:00:00Z","marginLevels":[{"numNonContractUnits":0.0,"initialMargin":0.02,"maintenanceMargin":0.01},{"numNonContractUnits":2000000.0,"initialMargin":0.04,"maintenanceMargin":0.02}],"isin":"GB00BTQJX047","contractValueTradePrecision":4,"postOnly":false,"feeScheduleUid":"723888f7-0a8e-4183-8648-f920a22339e3","mtf":true,"base":"BTC","quote":"USD","pair":"BTC:USD","category":"Layer 1","tradfi":false,"isExpired":false},
          {"symbol":"PI_XBTUSD","type":"futures_inverse","underlying":"rr_xbtusd","tickSize":0.5,"contractSize":1,"tradeable":true,"impactMidSize":1000.0,"maxPositionSize":75000000.0,"openingDate":"2018-08-31T00:00:00Z","marginLevels":[{"contracts":0,"initialMargin":0.02,"maintenanceMargin":0.01},{"contracts":500000,"initialMargin":0.04,"maintenanceMargin":0.02}],"fundingRateCoefficient":8,"maxRelativeFundingRate":0.005,"isin":"GB00J62YGL67","contractValueTradePrecision":0,"postOnly":false,"feeScheduleUid":"a6cbc326-9477-4a6c-911a-d4cb3ed7481e","mtf":true,"base":"BTC","quote":"USD","pair":"BTC:USD","category":"","makerProtectionMillis":20,"tradfi":false,"isExpired":false},
          {"symbol":"FI_XBTUSD_261225","type":"futures_inverse","underlying":"rr_xbtusd","lastTradingTime":"2026-12-25T16:00:00Z","tickSize":0.5,"contractSize":1,"tradeable":true,"impactMidSize":1000.0,"maxPositionSize":40000000.0,"openingDate":"2026-05-29T15:00:00Z","marginLevels":[{"contracts":0,"initialMargin":0.02,"maintenanceMargin":0.01}],"isin":"GB00BTQJRM47","contractValueTradePrecision":0,"postOnly":false,"feeScheduleUid":"a6cbc326-9477-4a6c-911a-d4cb3ed7481e","mtf":true,"base":"BTC","quote":"USD","pair":"BTC:USD","category":"Layer 1","tradfi":false,"isExpired":false}
        ],"serverTime":"2026-09-25T17:43:59.000Z"}
        """;

    /// <summary>
    /// Three fee schedules of <c>/derivatives/api/v3/feeschedules</c> as the live venue returned them, trimmed to
    /// their first two tiers. The numbers are PERCENTAGES - <c>makerFee: 0.02</c> is two basis points - which is
    /// the whole reason this endpoint is read rather than the rates being written down.
    /// </summary>
    public const string FeeSchedules = """
        {"result":"success","serverTime":"2026-09-25T17:46:00.000Z","feeSchedules":[
          {"uid":"723888f7-0a8e-4183-8648-f920a22339e3","name":"MTF Linear Rebate Fees","tiers":[{"makerFee":0.02,"takerFee":0.05,"usdVolume":0.0},{"makerFee":0.0175,"takerFee":0.045,"usdVolume":5000000.0}]},
          {"uid":"97ae0cc8-2964-43ea-91e9-d8e720b02b19","name":"Linear Multi-Collateral Rebate Fees","tiers":[{"makerFee":0.02,"takerFee":0.05,"usdVolume":0.0},{"makerFee":0.0175,"takerFee":0.045,"usdVolume":5000000.0}]},
          {"uid":"a6cbc326-9477-4a6c-911a-d4cb3ed7481e","name":"Inverse Single-Collateral Rebate Fees","tiers":[{"makerFee":0.02,"takerFee":0.05,"usdVolume":0.0},{"makerFee":0.0175,"takerFee":0.045,"usdVolume":5000000.0}]}
        ]}
        """;

    /// <summary>
    /// A refusal from the derivatives API's request parser - an <c>errors</c> ARRAY of objects. Recorded: it is what
    /// <c>editorder</c> answered a malformed order id, and it is one of TWO failure shapes the same API uses.
    /// </summary>
    public const string BadRequest = """
        {"status":"BAD_REQUEST","result":"error","errors":[{"code":11,"message":"Argument invalid: symbol"}],"serverTime":"2026-09-25T17:46:57.678Z"}
        """;

    /// <summary>
    /// The other one: a single <c>error</c> STRING. Recorded - it is what every signed endpoint answers a bad
    /// credential, and it is the same word whether the key, the signature or the clock is wrong.
    /// </summary>
    public const string AuthenticationError = """
        {"result":"error","error":"authenticationError","serverTime":"2026-09-25T17:54:01.754Z"}
        """;

    /// <summary>What the documented v3 funding path answers, and the reason the adapter calls v4 instead.</summary>
    public const string NotFound = """
        {"status":"NOT_FOUND","result":"error","errors":[{"code":0,"message":"404 NOT_FOUND"}],"serverTime":"2026-09-25T17:45:46.067Z"}
        """;

    // ----- futures: candles and funding -----

    /// <summary>
    /// Three hourly candles of the charts service as the live venue returned them. The envelope is
    /// <c>{ candles, more_candles }</c> with no wrapper at all - not the derivatives API's <c>result</c> - the row
    /// is an OBJECT rather than an array, and <c>time</c> is the interval's OPEN in MILLISECONDS.
    /// </summary>
    public const string Candles = """
        {"candles":[{"time":1790265600000,"open":"84406","high":"84924","low":"84009","close":"84476","volume":"250.90500000000"},{"time":1790269200000,"open":"84476","high":"84556","low":"83975","close":"84089","volume":"148.78190000000"},{"time":1790272800000,"open":"84089","high":"84540","low":"83864","close":"84454","volume":"167.90730000000"}],"more_candles":false}
        """;

    /// <summary>The open time of the first candle of <see cref="Candles"/>, in milliseconds.</summary>
    public const long CandlesFirstOpenMs = 1790265600000;

    /// <summary>A charts page the service cut short, which is how it says there is more history behind it.</summary>
    public const string CandlesTruncated = """
        {"candles":[{"time":1790265600000,"open":"84406","high":"84924","low":"84009","close":"84476","volume":"250.90500000000"}],"more_candles":true}
        """;

    /// <summary>A charts request the service refused - PLAIN TEXT with an HTTP 400, not JSON and not an envelope.</summary>
    public const string InvalidResolution = "Invalid resolution";

    /// <summary>
    /// Three funding settlements of <c>/derivatives/api/v4/historicalfundingrates</c> as the live venue returned
    /// them. Two numbers per settlement, and only <c>relativeFundingRate</c> is a rate: <c>fundingRate</c> is an
    /// absolute charge per contract, and 1.33 read as a rate is 133 per cent.
    /// </summary>
    public const string FundingRates = """
        {"result":"success","serverTime":"2026-09-25T17:46:54.822Z","rates":[
          {"timestamp":"2025-09-24T08:00:00Z","fundingRate":1.3285114808978307,"relativeFundingRate":1.1795815277778e-05},
          {"timestamp":"2025-09-24T09:00:00Z","fundingRate":1.534717467664686,"relativeFundingRate":1.3625181944444e-05},
          {"timestamp":"2025-09-24T10:00:00Z","fundingRate":1.682787683034,"relativeFundingRate":1.49229e-05}
        ]}
        """;

    // ----- futures: the public socket -----

    public const string FuturesInfo = """{"event":"info","version":1}""";

    public const string FuturesSubscribed = """{"event":"subscribed","feed":"ticker","product_ids":["PF_XBTUSD"]}""";

    /// <summary>
    /// What the venue says about a feed that does not exist. There is no feed name in it and no request id, so
    /// nothing can correlate it with what was asked - which is why the adapter logs it rather than failing a
    /// subscription on it.
    /// </summary>
    public const string FuturesInvalidFeed = """{"event":"alert","message":"Couldn't subscribe to invalid feed"}""";

    /// <summary>What it says about an authenticated feed asked for without a signed challenge.</summary>
    public const string FuturesAuthFailed = """{"event":"alert","message":"Failed to subscribe to authenticated feed"}""";

    /// <summary>
    /// The challenge the venue issues. Recorded: it hands one out for any api_key at all without checking it, so
    /// the handshake is known even though the answer to it is not.
    /// </summary>
    public const string FuturesChallenge = """{"event":"challenge","message":"5a2d547e-2226-4193-957e-7b19e32cf5b4"}""";

    /// <summary>
    /// The ticker, which carries a quote, a mark price, an index price AND a funding rate with the time of the next
    /// settlement. The funding rate appears twice and only <c>relative_funding_rate</c> is a rate.
    /// </summary>
    public const string FuturesTicker = """
        {"time":1790359997224,"product_id":"PF_XBTUSD","funding_rate":0.912482213835,"funding_rate_prediction":1.3232500286425,"relative_funding_rate":0.00001089345,"relative_funding_rate_prediction":0.00001575425,"next_funding_rate_time":1790362800000,"leverage":"100x","premium":0.0,"feed":"ticker","bid":84001.0,"ask":84002.0,"bid_size":0.0273,"ask_size":0.006,"volume":4237.2723,"dtm":0,"index":83995.76,"last":83997.0,"change":-0.22,"suspended":false,"tag":"perpetual","pair":"XBT:USD","openInterest":2189.7813,"markPrice":84004.11362574516,"maturityTime":0,"post_only":false,"volumeQuote":356641614.007}
        """;

    public const string FuturesTrade = """
        {"product_id":"PF_XBTUSD","feed":"trade","uid":"8a2be12f-7a3f-4f8f-a111-d1b5200ab1d0","side":"sell","type":"fill","time":1790360004381,"qty":0.0002,"price":83994.0,"seq":421135}
        """;

    /// <summary>
    /// The trade snapshot, which the venue sends NEWEST FIRST - the opposite of the order everything downstream
    /// expects, which is why this fixture has three trades and not one.
    /// </summary>
    public const string FuturesTradeSnapshot = """
        {"feed":"trade_snapshot","product_id":"PF_XBTUSD","trades":[
          {"product_id":"PF_XBTUSD","feed":"trade","uid":"1893f0aa-0084-4289-b02f-67444b325728","side":"sell","type":"fill","time":1790359963339,"qty":0.0001,"price":83999.0,"seq":421131},
          {"product_id":"PF_XBTUSD","feed":"trade","uid":"26ec9b3a-0cf1-41c0-8ac3-72ab82f97c61","side":"sell","type":"fill","time":1790359961107,"qty":0.0003,"price":84002.0,"seq":421130},
          {"product_id":"PF_XBTUSD","feed":"trade","uid":"cd411c24-ea62-4e9d-ab93-d58ce635654a","side":"buy","type":"fill","time":1790359960000,"qty":0.0003,"price":84002.0,"seq":421129}
        ]}
        """;

    public const string FuturesBookSnapshot = """
        {"feed":"book_snapshot","product_id":"PF_XBTUSD","timestamp":1790359964969,"seq":115335794,"tickSize":null,"bids":[{"price":83998.0,"qty":0.0371},{"price":83996.0,"qty":0.0235},{"price":83995.0,"qty":0.0208}],"asks":[{"price":83999.0,"qty":0.0400},{"price":84001.0,"qty":0.0500},{"price":84003.0,"qty":0.0600}]}
        """;

    /// <summary>A delta that removes a level, which the venue says with a zero quantity and a side as a word.</summary>
    public const string FuturesBookRemovesTopAsk = """
        {"feed":"book","product_id":"PF_XBTUSD","side":"sell","seq":115358039,"price":83999.0,"qty":0.0,"timestamp":1790359997729}
        """;

    /// <summary>
    /// A candle on the socket. The feed NAME carries the length, the time is the interval's OPEN in milliseconds,
    /// and the first message after subscribing arrives under the same name with <c>_snapshot</c> appended - which is
    /// <see cref="FuturesCandleSnapshot"/>.
    /// </summary>
    public static string FuturesCandle(long openMs, string open, string high, string low, string close, string volume, string resolution = "1m") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"feed":"candles_trade_{{resolution}}","candle":{"time":{{openMs}},"open":"{{open}}","high":"{{high}}","low":"{{low}}","close":"{{close}}","volume":"{{volume}}"},"product_id":"PF_XBTUSD"}
            """);

    /// <inheritdoc cref="FuturesCandle"/>
    public const string FuturesCandleSnapshot = """
        {"feed":"candles_trade_1m_snapshot","candle":{"time":1790359920000,"open":"83954","high":"84007","low":"83954","close":"83999","volume":"0.97830000"},"product_id":"PF_XBTUSD"}
        """;

    // ----- futures: private. PUBLISHED SHAPES, NOT RECORDED -----

    /// <summary>The multi-collateral account over REST. PUBLISHED SHAPE of <c>/derivatives/api/v3/accounts</c>.</summary>
    public const string Accounts = """
        {"result":"success","serverTime":"2026-09-25T18:00:00.000Z","accounts":{"flex":{"currencies":{"USD":{"quantity":100000.0,"value":100000.0,"available":99000.0,"collateral":100000.0},"BTC":{"quantity":0.5,"value":42000.0,"available":0.5,"collateral":41580.0}},"type":"multiCollateralMarginAccount"}}}
        """;

    /// <summary>The answer to <c>sendorder</c>. PUBLISHED SHAPE - and note that a REFUSAL arrives in here.</summary>
    public static string SendOrder(string status = "placed") =>
        "{\"result\":\"success\",\"sendStatus\":{\"order_id\":\"179f9af8-e45e-469d-b3e9-2fd4675cb7d0\",\"status\":\""
        + status + "\",\"receivedTime\":\"2026-09-25T18:00:00.000Z\"},\"serverTime\":\"2026-09-25T18:00:00.000Z\"}";

    /// <summary>The answer to <c>editorder</c>. PUBLISHED SHAPE, on an endpoint whose EXISTENCE is measured.</summary>
    public static string EditOrder(string status = "edited") =>
        "{\"result\":\"success\",\"editStatus\":{\"order_id\":\"179f9af8-e45e-469d-b3e9-2fd4675cb7d0\",\"status\":\""
        + status + "\",\"receivedTime\":\"2026-09-25T18:00:00.000Z\"},\"serverTime\":\"2026-09-25T18:00:00.000Z\"}";

    /// <summary>An acknowledged cancel or cancel-all. PUBLISHED SHAPE.</summary>
    public const string CancelAcknowledged = """
        {"result":"success","cancelStatus":{"status":"cancelled","receivedTime":"2026-09-25T18:00:00.000Z"},"serverTime":"2026-09-25T18:00:00.000Z"}
        """;

    /// <summary>The leverage preference having been set. PUBLISHED SHAPE.</summary>
    public const string LeveragePreferenceSet = """
        {"result":"success","serverTime":"2026-09-25T18:00:00.000Z"}
        """;

    /// <summary>The account's open orders. PUBLISHED SHAPE.</summary>
    public static string OpenOrders(string clientOrderId) =>
        "{\"result\":\"success\",\"openOrders\":[{\"order_id\":\"179f9af8-e45e-469d-b3e9-2fd4675cb7d0\",\"cliOrdId\":\""
        + clientOrderId + "\",\"symbol\":\"PF_XBTUSD\",\"side\":\"buy\",\"orderType\":\"lmt\",\"limitPrice\":50000,"
        + "\"unfilledSize\":0.0004,\"filledSize\":0.0006,\"reduceOnly\":false,\"status\":\"partiallyFilled\","
        + "\"receivedTime\":\"2026-09-25T18:00:00.000Z\",\"lastUpdateTime\":\"2026-09-25T18:00:01.000Z\"}],"
        + "\"serverTime\":\"2026-09-25T18:00:02.000Z\"}";

    /// <summary>The account's fills. PUBLISHED SHAPE.</summary>
    public static string Fills(string clientOrderId) =>
        "{\"result\":\"success\",\"fills\":[{\"fill_id\":\"c14ee7cb-ae25-4029-b8cf-32a2a0b45dd9\",\"order_id\":"
        + "\"179f9af8-e45e-469d-b3e9-2fd4675cb7d0\",\"cliOrdId\":\"" + clientOrderId
        + "\",\"symbol\":\"PF_XBTUSD\",\"side\":\"buy\",\"size\":0.0006,\"price\":50000,\"fillType\":\"taker\","
        + "\"fillTime\":\"2026-09-25T18:00:01.000Z\"}],\"serverTime\":\"2026-09-25T18:00:02.000Z\"}";

    /// <summary>The account's positions. PUBLISHED SHAPE.</summary>
    public const string OpenPositions = """
        {"result":"success","openPositions":[{"side":"long","symbol":"PF_XBTUSD","price":50000.0,"fillTime":"2026-09-25T18:00:01.000Z","size":0.0006,"unrealizedFunding":0.0000123}],"serverTime":"2026-09-25T18:00:02.000Z"}
        """;
}
