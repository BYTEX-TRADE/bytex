namespace Bytex.Adapters.Tests.Fixtures;

/// <summary>
/// Gate answers and stream messages, RECORDED FROM THE LIVE VENUE on 2026-09-25 rather than transcribed from its
/// documentation. Every public payload here came off <c>api.gateio.ws</c> or one of the venue's own sockets and is
/// kept field for field, including the fields this adapter ignores: a fixture trimmed to what the adapter reads
/// cannot catch an adapter reading the wrong field.
/// <para>
/// Five of the shapes below differ from what the venue's documentation says or implies, and each difference is the
/// silent kind. They are listed on the payloads they belong to.
/// </para>
/// <para>
/// The private payloads - orders, fills, accounts, positions - are the documented shapes rather than recordings,
/// because they cannot be reached without an API key. Where that matters it is said so on the payload.
/// </para>
/// </summary>
internal static class GatePayloads
{
    public static string Error(string label, string message) =>
        "{\"label\":\"" + label + "\",\"message\":\"" + message + "\"}";

    /// <summary>
    /// Four spot pairs as <c>GET /spot/currency_pairs</c> returned them: BTC_USDT and ETH_USDT, a pair the venue
    /// calls <c>untradable</c> and one it calls <c>sellable</c>.
    /// <para>
    /// Two facts to read off this. <c>fee</c> is a PERCENTAGE - "0.2" is twenty basis points - and <c>precision</c>
    /// and <c>amount_precision</c> are COUNTS of decimal places rather than increments, which every other venue in
    /// this repository publishes as increments.
    /// </para>
    /// </summary>
    public const string CurrencyPairs = """
        [
          {"id":"BTC_USDT","base":"BTC","base_name":"Bitcoin","quote":"USDT","quote_name":"Tether","trade_quotes":[],"fee":"0.2","min_base_amount":"0.000001","min_quote_amount":"3","max_base_amount":"100","max_quote_amount":"5000000","amount_precision":6,"precision":1,"trade_status":"tradable","sell_start":0,"buy_start":0,"type":"normal","trade_url":"https://www.gate.com/trade/BTC_USDT","st_tag":false,"up_rate":"0.08","down_rate":"0.08","slippage":"0.03","market_order_max_stock":"65","market_order_max_money":"5000000"},
          {"id":"ETH_USDT","base":"ETH","base_name":"Ethereum","quote":"USDT","quote_name":"Tether","trade_quotes":[],"fee":"0.2","min_base_amount":"0.001","min_quote_amount":"3","max_base_amount":"1000","max_quote_amount":"5000000","amount_precision":4,"precision":2,"trade_status":"tradable","sell_start":0,"buy_start":0,"type":"normal","trade_url":"https://www.gate.com/trade/ETH_USDT","st_tag":false,"up_rate":"0.08","down_rate":"0.08","slippage":"0.03","market_order_max_stock":"2096","market_order_max_money":"5000000"},
          {"id":"POOLX_USDT","base":"POOLX","base_name":"POOLX","quote":"USDT","quote_name":"Tether","trade_quotes":[],"fee":"0.2","min_base_amount":"1","min_quote_amount":"3","max_base_amount":"100000000","max_quote_amount":"5000000","amount_precision":0,"precision":6,"trade_status":"untradable","sell_start":0,"buy_start":0,"type":"normal","trade_url":"https://www.gate.com/trade/POOLX_USDT","st_tag":false,"up_rate":"0.5","down_rate":"0.5","slippage":"0.1","market_order_max_stock":"0","market_order_max_money":"0"},
          {"id":"MAG7XON_USDT","base":"MAG7XON","base_name":"MAG7XON","quote":"USDT","quote_name":"Tether","trade_quotes":[],"fee":"0.2","min_base_amount":"0.01","min_quote_amount":"3","max_base_amount":"100000","max_quote_amount":"5000000","amount_precision":2,"precision":4,"trade_status":"sellable","sell_start":0,"buy_start":0,"type":"normal","trade_url":"https://www.gate.com/trade/MAG7XON_USDT","st_tag":false,"up_rate":"0.5","down_rate":"0.5","slippage":"0.1","market_order_max_stock":"0","market_order_max_money":"0"}
        ]
        """;

    /// <summary>BTC_USDT on its own, as <c>GET /spot/currency_pairs/BTC_USDT</c> returns it.</summary>
    public const string CurrencyPair = """
        {"id":"BTC_USDT","base":"BTC","base_name":"Bitcoin","quote":"USDT","quote_name":"Tether","trade_quotes":[],"fee":"0.2","min_base_amount":"0.000001","min_quote_amount":"3","max_base_amount":"100","max_quote_amount":"5000000","amount_precision":6,"precision":1,"trade_status":"tradable","sell_start":0,"buy_start":0,"type":"normal","trade_url":"https://www.gate.com/trade/BTC_USDT","st_tag":false,"up_rate":"0.08","down_rate":"0.08","slippage":"0.03","market_order_max_stock":"65","market_order_max_money":"5000000"}
        """;

    /// <summary>
    /// Four USDT-settled perpetual contracts as <c>GET /futures/usdt/contracts</c> returned them: BTC_USDT and
    /// ETH_USDT, one of the fourteen contracts the venue has enabled fractional sizes on (ARIA_USDT, whose
    /// <c>order_size_min</c> is 0), and a tokenised-equity contract (AAPL_USDT, <c>contract_type: stocks</c>).
    /// <para>
    /// The fields the margin is taken from: <c>leverage_max</c>, whose reciprocal is the initial margin rate, and
    /// <c>maintenance_rate</c>. The fields the size is taken from: <c>quanto_multiplier</c>, which is what one
    /// contract is worth in base currency. And the maker fee is NEGATIVE on all four - a rebate.
    /// </para>
    /// </summary>
    public const string FuturesContracts = """
        [
          {"funding_rate_indicative":"-0.000002","mark_price_round":"0.01","funding_offset":0,"in_delisting":false,"risk_limit_base":"500000","interest_rate":"0.0003","index_price":"83802.16","order_price_round":"0.1","order_size_min":1,"enable_decimal":false,"ref_rebate_rate":"0.2","name":"BTC_USDT","ref_discount_rate":"0","order_price_deviate":"0.03","maintenance_rate":"0.003","mark_type":"index","funding_interval":28800,"type":"direct","risk_limit_step":"1499500000","enable_bonus":true,"enable_credit":true,"leverage_min":"1","funding_rate":"-0.000002","last_price":"83760.6","mark_price":"83760.7","order_size_max":12000000,"funding_next_apply":1790380800,"short_users":15558,"config_change_time":1788234002,"create_time":1574035200,"trade_size":664685004030,"position_size":289031937,"long_users":18268,"quanto_multiplier":"0.0001","funding_impact_value":"30000","leverage_max":"200","cross_leverage_default":"10","risk_limit_max":"1500000000","maker_fee_rate":"-0.0001","taker_fee_rate":"0.00075","orders_limit":100,"trade_id":842233200,"orderbook_id":126201707497,"funding_cap_ratio":"0.75","voucher_leverage":"2","is_pre_market":false,"status":"trading","launch_time":1574035200,"enable_circuit_breaker":false,"funding_rate_limit":"0.003","market_order_slip_ratio":"0.01","market_order_size_max":"10000000","contract_type":""},
          {"funding_rate_indicative":"0.000013","mark_price_round":"0.01","funding_offset":0,"in_delisting":false,"risk_limit_base":"300000","interest_rate":"0.0003","index_price":"2699.15","order_price_round":"0.01","order_size_min":0,"enable_decimal":true,"ref_rebate_rate":"0.2","name":"ETH_USDT","ref_discount_rate":"0","order_price_deviate":"0.03","maintenance_rate":"0.003","mark_type":"index","funding_interval":28800,"type":"direct","risk_limit_step":"299700000","enable_bonus":true,"enable_credit":true,"leverage_min":"1","funding_rate":"0.000013","last_price":"2698.82","mark_price":"2699.1","order_size_max":10000000,"funding_next_apply":1790380800,"short_users":11402,"config_change_time":1788234002,"create_time":1574035200,"trade_size":901234567,"position_size":12345678,"long_users":13950,"quanto_multiplier":"0.01","funding_impact_value":"30000","leverage_max":"200","cross_leverage_default":"10","risk_limit_max":"300000000","maker_fee_rate":"-0.0001","taker_fee_rate":"0.00075","orders_limit":100,"trade_id":123456789,"orderbook_id":987654321,"funding_cap_ratio":"0.75","voucher_leverage":"2","is_pre_market":false,"status":"trading","launch_time":1574035200,"enable_circuit_breaker":false,"funding_rate_limit":"0.003","market_order_slip_ratio":"0.01","market_order_size_max":"10000000","contract_type":""},
          {"funding_rate_indicative":"0.001026","mark_price_round":"0.00001","funding_offset":0,"in_delisting":false,"risk_limit_base":"5000","interest_rate":"0.0003","index_price":"0.037251","order_price_round":"0.00001","order_size_min":0,"enable_decimal":true,"ref_rebate_rate":"0.2","name":"ARIA_USDT","ref_discount_rate":"0","order_price_deviate":"0.15","maintenance_rate":"0.08","mark_type":"index","funding_interval":14400,"type":"direct","risk_limit_step":"2495000","enable_bonus":true,"enable_credit":true,"leverage_min":"1","funding_rate":"0.001026","last_price":"0.0377","mark_price":"0.03764","order_size_max":12000,"funding_next_apply":1790366400,"short_users":140,"config_change_time":1789984943,"create_time":1755758876,"trade_size":33776349,"position_size":34293,"long_users":199,"quanto_multiplier":"100","funding_impact_value":"7000","leverage_max":"10","cross_leverage_default":"10","risk_limit_max":"2500000","maker_fee_rate":"-0.0001","taker_fee_rate":"0.00075","orders_limit":100,"trade_id":5920815,"orderbook_id":1054173333,"funding_cap_ratio":"0.4","voucher_leverage":"0","is_pre_market":false,"status":"trading","launch_time":1755781800,"enable_circuit_breaker":false,"funding_rate_limit":"0.02","market_order_slip_ratio":"0.04","market_order_size_max":"7500","contract_type":""},
          {"funding_rate_indicative":"0.0001","mark_price_round":"0.001","funding_offset":0,"in_delisting":false,"risk_limit_base":"5000","interest_rate":"0.0003","index_price":"254.61","order_price_round":"0.001","order_size_min":1,"enable_decimal":false,"ref_rebate_rate":"0.2","name":"AAPL_USDT","ref_discount_rate":"0","order_price_deviate":"0.1","maintenance_rate":"0.005","mark_type":"index","funding_interval":14400,"type":"direct","risk_limit_step":"495000","enable_bonus":true,"enable_credit":true,"leverage_min":"1","funding_rate":"0.0001","last_price":"254.6","mark_price":"254.61","order_size_max":100000,"funding_next_apply":1790366400,"short_users":12,"config_change_time":1789984943,"create_time":1745758876,"trade_size":12345,"position_size":678,"long_users":34,"quanto_multiplier":"0.01","funding_impact_value":"1000","leverage_max":"100","cross_leverage_default":"10","risk_limit_max":"500000","maker_fee_rate":"-0.0001","taker_fee_rate":"0.00075","orders_limit":100,"trade_id":12345,"orderbook_id":67890,"funding_cap_ratio":"0.4","voucher_leverage":"0","is_pre_market":false,"status":"trading","launch_time":1745781800,"enable_circuit_breaker":false,"funding_rate_limit":"0.02","market_order_slip_ratio":"0.04","market_order_size_max":"50000","contract_type":"stocks"}
        ]
        """;

    /// <summary>BTC_USDT on its own, as <c>GET /futures/usdt/contracts/BTC_USDT</c> returns it.</summary>
    public const string FuturesContract = """
        {"funding_rate_indicative":"-0.000002","mark_price_round":"0.01","funding_offset":0,"in_delisting":false,"risk_limit_base":"500000","interest_rate":"0.0003","index_price":"83802.16","order_price_round":"0.1","order_size_min":1,"enable_decimal":false,"ref_rebate_rate":"0.2","name":"BTC_USDT","ref_discount_rate":"0","order_price_deviate":"0.03","maintenance_rate":"0.003","mark_type":"index","funding_interval":28800,"type":"direct","risk_limit_step":"1499500000","enable_bonus":true,"enable_credit":true,"leverage_min":"1","funding_rate":"-0.000002","last_price":"83760.6","mark_price":"83760.7","order_size_max":12000000,"funding_next_apply":1790380800,"short_users":15558,"config_change_time":1788234002,"create_time":1574035200,"trade_size":664685004030,"position_size":289031937,"long_users":18268,"quanto_multiplier":"0.0001","funding_impact_value":"30000","leverage_max":"200","cross_leverage_default":"10","risk_limit_max":"1500000000","maker_fee_rate":"-0.0001","taker_fee_rate":"0.00075","orders_limit":100,"trade_id":842233200,"orderbook_id":126201707497,"funding_cap_ratio":"0.75","voucher_leverage":"2","is_pre_market":false,"status":"trading","launch_time":1574035200,"enable_circuit_breaker":false,"funding_rate_limit":"0.003","market_order_slip_ratio":"0.01","market_order_size_max":"10000000","contract_type":""}
        """;

    /// <summary>
    /// The one contract <c>GET /futures/btc/contracts</c> holds, which is INVERSE - <c>type: inverse</c> with a
    /// <c>quanto_multiplier</c> of zero. It is here so a test can prove the provider leaves it out: an inverse
    /// contract's quantity cannot be expressed in base currency without a price.
    /// </summary>
    public const string InverseContracts = """
        [
          {"funding_rate_indicative":"0.0001","mark_price_round":"0.01","funding_offset":0,"in_delisting":false,"risk_limit_base":"3","interest_rate":"0.0003","index_price":"83780.64","order_price_round":"0.1","order_size_min":1,"enable_decimal":false,"ref_rebate_rate":"0.2","name":"BTC_USD","ref_discount_rate":"0","order_price_deviate":"0.5","maintenance_rate":"0.005","mark_type":"index","funding_interval":28800,"type":"inverse","risk_limit_step":"2497","enable_bonus":false,"enable_credit":true,"leverage_min":"1","funding_rate":"0.0001","last_price":"83802","mark_price":"83787.19","order_size_max":530000,"funding_next_apply":1790380800,"short_users":231,"config_change_time":1776852838,"create_time":1545235200,"trade_size":61678957853,"position_size":7488838,"long_users":355,"quanto_multiplier":"0","funding_impact_value":"0.5","leverage_max":"100","cross_leverage_default":"10","risk_limit_max":"2500","maker_fee_rate":"-0.0002","taker_fee_rate":"0.00075","orders_limit":50,"trade_id":49445234,"orderbook_id":5895259469,"funding_cap_ratio":"0.75","voucher_leverage":"0","is_pre_market":false,"status":"trading","launch_time":1545235200,"enable_circuit_breaker":false,"funding_rate_limit":"0.00375","market_order_slip_ratio":"0.02","market_order_size_max":"350000","contract_type":""}
        ]
        """;

    /// <summary>
    /// Two dated contracts as <c>GET /delivery/usdt/contracts</c> returned them. There is no funding rate, no
    /// funding interval and no next-settlement field anywhere on them - the venue's own statement that a dated
    /// contract is not funded - and there is no creation or launch time either, so nothing publishes an activation.
    /// The <c>underlying</c> field is what the venue says the contract tracks; <c>expire_time</c> is in SECONDS.
    /// </summary>
    public const string DeliveryContracts = """
        [
          {"basis_rate":"0.00636","cycle":"BI-WEEKLY","settle_fee_rate":"0.00015","in_delisting":false,"expire_time":1791532800,"risk_limit_base":"1000000","index_price":"2685.52","order_price_round":"0.01","order_size_min":1,"ref_rebate_rate":"0.2","name":"ETH_USDT_20261009","ref_discount_rate":"0","order_price_deviate":"0.5","maintenance_rate":"0.005","mark_type":"index","type":"direct","basis_value":"0.66","leverage_min":"1","settle_price_interval":60,"last_price":"2687.01","mark_price":"2686.18","order_size_max":1000000,"maker_fee_rate":"-0.00015","settle_price_duration":1800,"config_change_time":1790322302,"orderbook_id":282636,"trade_size":5979,"underlying":"ETH_USDT","position_size":7,"orders_limit":50,"quanto_multiplier":"0.01","basis_impact_value":"1000","mark_price_round":"0.01","settle_price":"0","leverage_max":"100","risk_limit_max":"8000000","taker_fee_rate":"0.00025","trade_id":2513,"risk_limit_step":"1000000"},
          {"basis_rate":"0.00521","cycle":"BI-WEEKLY","settle_fee_rate":"0.00015","in_delisting":false,"expire_time":1791532800,"risk_limit_base":"1000000","index_price":"83999.8","order_price_round":"0.1","order_size_min":1,"ref_rebate_rate":"0.2","name":"BTC_USDT_20261009","ref_discount_rate":"0","order_price_deviate":"0.5","maintenance_rate":"0.005","mark_type":"index","type":"direct","basis_value":"12.5","leverage_min":"1","settle_price_interval":60,"last_price":"84169.0","mark_price":"84128.5","order_size_max":1000000,"maker_fee_rate":"-0.00015","settle_price_duration":1800,"config_change_time":1790322302,"orderbook_id":282637,"trade_size":22361,"underlying":"BTC_USDT","position_size":38,"orders_limit":50,"quanto_multiplier":"0.0001","basis_impact_value":"1000","mark_price_round":"0.1","settle_price":"0","leverage_max":"100","risk_limit_max":"8000000","taker_fee_rate":"0.00025","trade_id":2514,"risk_limit_step":"1000000"}
        ]
        """;

    /// <summary>
    /// The first five risk-limit tiers of BTC_USDT as <c>GET /futures/usdt/risk_limit_tiers</c> returned them. The
    /// venue publishes the initial margin rate here and nowhere on the contract, and tier one's figures are the
    /// contract's own: <c>initial_rate</c> 0.005 is one over the contract's <c>leverage_max</c> of 200, and
    /// <c>maintenance_rate</c> 0.003 is the contract's. That is what lets the adapter publish the margin off the
    /// contract it already holds instead of spending a request per instrument across a thousand of them.
    /// </summary>
    public const string RiskLimitTiers = """
        [
          {"maintenance_rate":"0.003","tier":1,"initial_rate":"0.005","leverage_max":"200","risk_limit":"500000","deduction":"0"},
          {"maintenance_rate":"0.0035","tier":2,"initial_rate":"0.006666","leverage_max":"150.01","risk_limit":"1000000","deduction":"250"},
          {"maintenance_rate":"0.004","tier":3,"initial_rate":"0.008","leverage_max":"125","risk_limit":"1500000","deduction":"750"},
          {"maintenance_rate":"0.0045","tier":4,"initial_rate":"0.00909","leverage_max":"110.01","risk_limit":"2000000","deduction":"1500"},
          {"maintenance_rate":"0.005","tier":5,"initial_rate":"0.01","leverage_max":"100","risk_limit":"3000000","deduction":"2500"}
        ]
        """;

    // ----- candles -----
    //
    // The three markets answer three different shapes, and every difference below was measured.
    //
    // Spot answers an ARRAY OF STRING ARRAYS ordered
    //   [open time in seconds, quote volume, CLOSE, HIGH, LOW, OPEN, base volume, window closed]
    // - close before high, open LAST, and a boolean-as-string at the end that says whether the interval has closed.
    //
    // Perpetual futures answers OBJECTS {t, o, h, l, c, v, sum} with the volume in CONTRACTS and NO window-closed
    // field at all.
    //
    // Delivery answers the same objects WITHOUT `sum`.
    //
    // All three stamp `t` in SECONDS and at the candle's OPEN.

    /// <summary>
    /// Six one-minute spot candles from 2026-09-25T17:26:00Z, and the last one is still forming - its window-closed
    /// flag is "false". Recorded with <c>from=1790349960&amp;to=1790350260</c>, which returned six rows: both ends of
    /// the window are inclusive, so a span of five intervals is six rows.
    /// </summary>
    public const string SpotCandles = """
        [["1790349960","355349.00613100","83859","83864.5","83829.6","83853.4","4.23818400","true"],["1790350020","454574.69406020","83816.1","83872.5","83812.2","83867.2","5.42217100","true"],["1790350080","643818.56576750","83771.5","83812.2","83754.1","83812.2","7.68526800","true"],["1790350140","442052.42285650","83770.7","83780.2","83736.9","83771.4","5.27751600","true"],["1790350200","507875.46163100","83766.1","83882.6","83746.3","83770.6","6.06078800","true"],["1790350260","532272.62128430","83781.7","83798.9","83740.4","83767.9","6.35429100","false"]]
        """;

    /// <summary>The same six minutes of BTC_USDT off the perpetual market. Volumes are contracts of 0.0001 BTC.</summary>
    public const string FuturesCandles = """
        [{"o":"83807.3","v":291170,"t":1790349960,"c":"83817.9","l":"83788.1","h":"83823","sum":"2440050.23996"},{"o":"83821.4","v":230651,"t":1790350020,"c":"83765.6","l":"83763","h":"83830.1","sum":"1932591.37262"},{"o":"83765.5","v":254233,"t":1790350080,"c":"83727.1","l":"83708.1","h":"83765.5","sum":"2128546.40587"},{"o":"83727.1","v":541352,"t":1790350140,"c":"83729","l":"83685.9","h":"83735","sum":"4531606.99173"},{"o":"83729","v":1454666,"t":1790350200,"c":"83714.8","l":"83699","h":"83836.1","sum":"12184193.43352"},{"o":"83722.1","v":872273,"t":1790350260,"c":"83750.8","l":"83693.3","h":"83750.8","sum":"7302592.8209"}]
        """;

    /// <summary>
    /// Four one-minute candles of BTC_USDT_20261009 off the delivery market. No <c>sum</c> field, and the last row
    /// has a volume of zero - the venue writes a flat candle for an interval nothing traded in, so a history fetch
    /// has no gaps to fill.
    /// </summary>
    public const string DeliveryCandles = """
        [{"t":1790349960,"o":"83924.4","v":40,"h":"83999.7","c":"83860.9","l":"83860.9"},{"t":1790350020,"o":"83955.9","v":35,"h":"84079.3","c":"84079.3","l":"83847.5"},{"t":1790350080,"o":"83919.1","v":39,"h":"83946.9","c":"83871.2","l":"83854.8"},{"t":1790350140,"o":"83871.2","c":"83871.2","v":0,"l":"83871.2","h":"83871.2"}]
        """;

    /// <summary>
    /// Sixty-one consecutive one-minute candles of BVOL_USDT, every one with a volume of zero and no gap between any
    /// two. This is the measurement behind the adapter filling nothing in: on KuCoin a quiet interval is simply
    /// absent and has to be built, and on this venue it is written.
    /// </summary>
    public static string QuietCandles(long firstOpenSeconds, int count)
    {
        List<string> rows = new(count);
        for (int i = 0; i < count; i++)
        {
            long t = firstOpenSeconds + (i * 60);
            rows.Add($"{{\"o\":\"1.25\",\"v\":0,\"t\":{t},\"c\":\"1.25\",\"l\":\"1.25\",\"h\":\"1.25\",\"sum\":\"0\"}}");
        }

        return "[" + string.Join(',', rows) + "]";
    }

    /// <summary>
    /// Five funding settlements of BTC_USDT as <c>GET /futures/usdt/funding_rate</c> returned them: newest first,
    /// the rate in <c>r</c> and the settlement time in <c>t</c>, in SECONDS.
    /// </summary>
    public const string FundingRates = """
        [{"r":"-0.000007","t":1790352000},{"r":"0.000028","t":1790323200},{"r":"0.000007","t":1790294400},{"r":"-0.000008","t":1790265600},{"r":"0.000026","t":1790236800}]
        """;

    /// <summary>The venue's refusal when funding history is asked for further back than it keeps.</summary>
    public static readonly string FundingTooOld = Error("INVALID_PARAM_VALUE", "from time exceeds 180-day limit");

    /// <summary>The venue's refusal when a spot candle window is wider than it will answer.</summary>
    public static readonly string SpotWindowTooWide =
        Error("INVALID_PARAM_VALUE", "Candlestick range too broad. Maximum 1000 data points are allowed per request");

    // ----- REST trades -----

    /// <summary>
    /// Two spot trades as <c>GET /spot/trades</c> returned them. <c>create_time_ms</c> is a STRING of MILLISECONDS
    /// with a fractional part.
    /// </summary>
    public const string SpotTrades = """
        [{"id":"220643972","create_time":"1790359364","create_time_ms":"1790359364969.221000","currency_pair":"BTC_USDT","side":"sell","amount":"0.000761","price":"83818.9","sequence_id":"220643972"},{"id":"220643971","create_time":"1790359364","create_time_ms":"1790359364413.017000","currency_pair":"BTC_USDT","side":"sell","amount":"0.000449","price":"83818.9","sequence_id":"220643971"}]
        """;

    /// <summary>
    /// Two perpetual trades as <c>GET /futures/usdt/trades</c> returned them, and both of this row's traps are
    /// visible in it. <c>create_time_ms</c> is a NUMBER OF SECONDS with a fraction despite its name - reading it as
    /// milliseconds puts the trade in January 1970 - and <c>size</c> is SIGNED, with a negative size meaning the
    /// taker sold. There is no side field to fall back on.
    /// </summary>
    public const string FuturesTrades = """
        [{"id":842236400,"contract":"BTC_USDT","create_time":1790359375.258,"create_time_ms":1790359375.258,"size":9,"price":"83775.5"},{"id":842236399,"contract":"BTC_USDT","create_time":1790359375.111,"create_time_ms":1790359375.111,"size":-2,"price":"83775.4"}]
        """;

    // ----- socket messages, recorded from the venue's own sockets -----

    /// <summary>Best bid and ask off the spot socket. Sizes in base currency, timestamp in whole milliseconds.</summary>
    public const string SpotBookTicker = """
        {"time":1790359165,"time_ms":1790359165714,"channel":"spot.book_ticker","event":"update","result":{"t":1790359165710,"u":40013503861,"s":"BTC_USDT","b":"83790.9","B":"0.497321","a":"83791","A":"0.055894"}}
        """;

    /// <summary>A spot trade off the socket: the same field names the REST endpoint uses, including the string-of-milliseconds time.</summary>
    public const string SpotTrade = """
        {"time":1790359171,"time_ms":1790359171120,"channel":"spot.trades","event":"update","result":{"id":220643714,"id_market":220643714,"create_time":1790359171,"create_time_ms":"1790359171119.653000","side":"sell","currency_pair":"BTC_USDT","amount":"0.002984","price":"83790.9","range":"220643714-220643714","trade_mode":0}}
        """;

    /// <summary>
    /// A spot candle off the socket. <c>a</c> is the BASE volume and <c>v</c> the QUOTE volume, which is the reverse
    /// of what the letters suggest; <c>n</c> is the subscription's own name and <c>w</c> says whether the interval
    /// has closed.
    /// </summary>
    public static string SpotCandle(long openSeconds, string open, string high, string low, string close, string baseVolume, bool windowClosed) =>
        "{\"time\":" + openSeconds + ",\"time_ms\":" + (openSeconds * 1000L) + ",\"channel\":\"spot.candlesticks\",\"event\":\"update\","
        + "\"result\":{\"t\":\"" + openSeconds + "\",\"v\":\"31654.7815163\",\"c\":\"" + close + "\",\"h\":\"" + high + "\",\"l\":\"" + low
        + "\",\"o\":\"" + open + "\",\"n\":\"1m_BTC_USDT\",\"a\":\"" + baseVolume + "\",\"w\":" + (windowClosed ? "true" : "false") + "}}";

    /// <summary>A twenty-level spot depth snapshot, trimmed to three levels a side. Its event is "update".</summary>
    public const string SpotOrderBook = """
        {"time":1790360084,"time_ms":1790360084771,"channel":"spot.order_book","event":"update","result":{"t":1790360084770,"lastUpdateId":40013700725,"s":"BTC_USDT","l":"20","bids":[["84005.6","0.035056"],["84002.4","0.012887"],["84002.3","0.043173"]],"asks":[["84005.7","0.115252"],["84006.7","0.011023"],["84006.8","0.032386"]]}}
        """;

    /// <summary>Best bid and ask off the perpetual socket. The SIZES ARE INTEGERS and count contracts.</summary>
    public const string FuturesBookTicker = """
        {"time":1790359189,"time_ms":1790359189821,"channel":"futures.book_ticker","event":"update","result":{"t":1790359189815,"u":126202649505,"s":"BTC_USDT","b":"83746.8","B":13855,"a":"83746.9","A":11465}}
        """;

    /// <summary>
    /// A perpetual trade off the socket, as an ARRAY of one. Here <c>create_time_ms</c> really is milliseconds,
    /// where the REST row of the same name is seconds; the size is signed, as everywhere on this market.
    /// </summary>
    public const string FuturesTrade = """
        {"time":1790359195,"time_ms":1790359195732,"channel":"futures.trades","event":"update","result":[{"id":842235619,"size":-3,"create_time":1790359195,"create_time_ms":1790359195732,"price":"83746.9","contract":"BTC_USDT"}]}
        """;

    /// <summary>
    /// A perpetual candle off the socket, as an array of one. <c>v</c> is a number of CONTRACTS here where on spot
    /// it is the quote volume, and <c>w</c> says whether the interval has closed.
    /// </summary>
    public static string FuturesCandle(long openSeconds, string open, string high, string low, string close, long contracts, bool windowClosed) =>
        "{\"time\":" + openSeconds + ",\"time_ms\":" + (openSeconds * 1000L) + ",\"channel\":\"futures.candlesticks\",\"event\":\"update\","
        + "\"result\":[{\"t\":" + openSeconds + ",\"c\":\"" + close + "\",\"h\":\"" + high + "\",\"l\":\"" + low + "\",\"o\":\"" + open
        + "\",\"a\":\"337220.70089\",\"n\":\"1m_BTC_USDT\",\"w\":" + (windowClosed ? "true" : "false") + ",\"v\":" + contracts + "}]}";

    /// <summary>
    /// A DELIVERY candle off the socket: the same channel name and the same fields as the perpetual one MINUS the
    /// window-closed flag and the quote volume. An interval here is closed by the arrival of the next one.
    /// </summary>
    public static string DeliveryCandle(long openSeconds, string open, string high, string low, string close, long contracts) =>
        "{\"time\":" + openSeconds + ",\"time_ms\":" + (openSeconds * 1000L) + ",\"channel\":\"futures.candlesticks\",\"event\":\"update\","
        + "\"result\":[{\"t\":" + openSeconds + ",\"v\":" + contracts + ",\"c\":\"" + close + "\",\"h\":\"" + high + "\",\"l\":\"" + low
        + "\",\"o\":\"" + open + "\",\"n\":\"1m_BTC_USDT_20261009\"}]}";

    /// <summary>
    /// A perpetual depth snapshot, trimmed to two levels a side. Two things differ from the spot one: the event is
    /// "all" rather than "update", and a level is an object with <c>p</c> and <c>s</c> rather than a two-element
    /// array - with <c>s</c> in contracts.
    /// </summary>
    public const string FuturesOrderBook = """
        {"time":1790360091,"time_ms":1790360091666,"channel":"futures.order_book","event":"all","result":{"t":1790360091660,"id":126203785686,"contract":"BTC_USDT","asks":[{"p":"83956.4","s":45979},{"p":"83957.5","s":113}],"bids":[{"p":"83956.3","s":13070},{"p":"83956.2","s":650}],"l":"20"}}
        """;

    /// <summary>
    /// The perpetual ticker, which carries the mark price, the index price and the funding rate on one channel.
    /// Arrives as an array.
    /// </summary>
    public const string FuturesTicker = """
        {"time":1790359195,"time_ms":1790359195603,"channel":"futures.tickers","event":"update","result":[{"contract":"BTC_USDT","last":"83746.8","change_percentage":"-0.3892","total_size":"577776862","volume_24h":"670521289","volume_24h_base":"67052.1289","volume_24h_quote":"5615401236","volume_24h_settle":"5615401236","mark_price":"83746.8","funding_rate":"0.000125","funding_rate_indicative":"0.000125","funding_interval":28800,"funding_offset":0,"funding_next_apply":1790380800,"index_price":"83786.4","quanto_base_rate":"","low_24h":"83120.0","high_24h":"85212.0","price_type":"last","change_from":"24h","change_price":"-327.2","t":1790359194966}]}
        """;

    /// <summary>
    /// The DELIVERY ticker, recorded from the venue's delivery socket. Its funding fields are EMPTY STRINGS rather
    /// than absent - the venue saying a dated contract is not funded - and an empty string parses to zero, so a rate
    /// of nought would otherwise be published as though it had been measured.
    /// </summary>
    public const string DeliveryTicker = """
        {"time":1790360096,"time_ms":1790360096905,"channel":"futures.tickers","event":"update","result":[{"contract":"BTC_USDT_20261009","last":"84169.0","change_percentage":"0","funding_rate":"","funding_rate_indicative":"","mark_price":"84128.5","index_price":"83999.8","total_size":"38","volume_24h":"22361","volume_24h_base":"2","volume_24h_quote":"188210","volume_24h_settle":"188210","quanto_base_rate":"","low_24h":"82932.5","high_24h":"85498.5","price_type":"last"}]}
        """;

    /// <summary>The venue's answer to a subscribe request that it refused.</summary>
    public const string SubscribeRefused = """
        {"time":1790359189,"time_ms":1790359189780,"channel":"futures.candlesticks","event":"subscribe","payload":["1m_BTC_USDT"],"error":{"code":1,"message":"request payload does not follow json schema"},"result":{"status":"fail"}}
        """;

    // ----- private payloads (documented shapes; not reachable without an API key) -----

    /// <summary>A spot account balance list, as <c>GET /spot/accounts</c> is documented to answer.</summary>
    public const string SpotAccounts = """
        [{"currency":"USDT","available":"1000.5","locked":"25.5"},{"currency":"BTC","available":"0.05","locked":"0"}]
        """;

    /// <summary>
    /// A perpetual settlement account, as <c>GET /futures/usdt/accounts</c> is documented to answer. The
    /// <c>user</c> field is what the private channels need in their payload and is the reason this is read on connect.
    /// </summary>
    public const string FuturesAccount = """
        {"user":12345678,"currency":"USDT","total":"2500.25","unrealised_pnl":"0","position_margin":"100","order_margin":"0","available":"2400.25","point":"0","bonus":"0","in_dual_mode":false,"enable_credit":true,"position_initial_margin":"100","maintenance_margin":"3"}
        """;

    /// <summary>A spot order, as the venue is documented to answer one. The client order id lives in <c>text</c>.</summary>
    public static string SpotOrder(string text, string status = "open", string left = "0.5", string finishAs = "") =>
        "{\"id\":\"170000001\",\"text\":\"" + text + "\",\"create_time\":\"1790350000\",\"update_time\":\"1790350100\","
        + "\"create_time_ms\":1790350000123,\"update_time_ms\":1790350100456,\"status\":\"" + status + "\","
        + "\"currency_pair\":\"BTC_USDT\",\"type\":\"limit\",\"account\":\"spot\",\"side\":\"buy\",\"amount\":\"1\","
        + "\"price\":\"83000\",\"time_in_force\":\"gtc\",\"left\":\"" + left + "\",\"filled_total\":\"41500\","
        + "\"fee\":\"0.001\",\"fee_currency\":\"BTC\""
        + (finishAs.Length > 0 ? ",\"finish_as\":\"" + finishAs + "\"" : string.Empty) + "}";

    /// <summary>
    /// A perpetual order, as the venue is documented to answer one. The size is signed contracts and a price of
    /// "0" is the market order this adapter sends as one.
    /// </summary>
    public static string FuturesOrder(string text, long size, string price = "83000", string status = "open", long left = 0, string finishAs = "") =>
        "{\"id\":900000001,\"text\":\"" + text + "\",\"create_time\":1790350000.123,\"status\":\"" + status + "\","
        + "\"contract\":\"BTC_USDT\",\"size\":" + size + ",\"left\":" + left + ",\"price\":\"" + price + "\","
        + "\"fill_price\":\"83010\",\"tif\":\"gtc\",\"is_reduce_only\":false,\"is_close\":false,\"is_liq\":false"
        + (finishAs.Length > 0 ? ",\"finish_time\":1790350200.5,\"finish_as\":\"" + finishAs + "\"" : string.Empty) + "}";

    /// <summary>A perpetual position list, as the venue is documented to answer it. A short position has a negative size.</summary>
    public const string FuturesPositions = """
        [{"user":12345678,"contract":"BTC_USDT","size":-500,"leverage":"10","risk_limit":"500000","leverage_max":"200","maintenance_rate":"0.003","value":"4188","margin":"418.8","entry_price":"83500","liq_price":"91000","mark_price":"83760.7","unrealised_pnl":"-13","realised_pnl":"0","mode":"single","update_time":1790350000,"cross_leverage_limit":"0"},{"user":12345678,"contract":"ETH_USDT","size":0,"leverage":"10","risk_limit":"300000","leverage_max":"200","maintenance_rate":"0.003","value":"0","margin":"0","entry_price":"0","liq_price":"0","mark_price":"2699.1","unrealised_pnl":"0","realised_pnl":"0","mode":"single","update_time":1790350000,"cross_leverage_limit":"0"}]
        """;
}
