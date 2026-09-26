using System.Globalization;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Gate;

// Why: two of this venue's candle facts are silent when they are wrong, and both were measured rather than read.
//
// The timestamps are in SECONDS - the row's own stamp and the from/to a request carries - where every other venue in
// this repository counts in milliseconds. Sending milliseconds is refused with a range error, which is the mercy;
// READING a stamp as milliseconds is refused by nothing and places a 2026 bar in 1970, where BarWindow drops it and
// a history request quietly answers with nothing.
//
// And the row's SHAPE differs between the venue's own three markets: spot answers arrays ordered
// [open time, quote volume, CLOSE, HIGH, LOW, OPEN, base volume, window closed], the perpetual market answers named
// objects with contract volumes, and the delivery market answers those objects minus two fields. Reading one with
// another's layout gives four real prices in the wrong slots, with no error anywhere.
public sealed class GateHistoryTests
{
    /// <summary>The first candle in every recorded payload opens here: 2026-09-25T17:26:00Z.</summary>
    private const long FirstOpen = 1790349960;

    private const long Minute = 60;

    private static readonly BarSpecification _oneMinute = new(1, BarAggregation.Minute, PriceType.Last);

    private static Instrument Spot() => new CurrencyPair(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC_USDT.GATE"),
        RawSymbol = new Symbol("BTC_USDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currency.FromCode("USDT", 8),
        BaseCurrency = Currency.FromCode("BTC", 8),
        SettlementCurrency = Currency.FromCode("USDT", 8),
        PricePrecision = 1,
        SizePrecision = 8,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.000001m, 8),
    });

    private static Instrument Derivative(string symbol = "BTC_USDT", decimal multiplier = 0.0001m) => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse(symbol + ".GATE"),
        RawSymbol = new Symbol(symbol),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currency.FromCode("USDT", 8),
        BaseCurrency = Currency.FromCode("BTC", 8),
        SettlementCurrency = Currency.FromCode("USDT", 8),
        PricePrecision = 1,
        SizePrecision = 8,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(multiplier, 8),
        Info = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contractMultiplier"] = multiplier.ToString(CultureInfo.InvariantCulture),
        },
    });

    private static BarType BarTypeFor(Instrument instrument) =>
        new(instrument.Id, _oneMinute, AggregationSource.External);

    // ----- spot -----

    [Fact]
    public async Task A_spot_row_is_read_close_high_low_open_and_stamped_at_its_close()
    {
        // The first recorded row is ["1790349960","355349.00613100","83859","83864.5","83829.6","83853.4",...]:
        // close 83859, high 83864.5, low 83829.6, OPEN LAST at 83853.4. Read left to right as open-high-low-close -
        // which is the order Binance, Bybit and KuCoin's futures all use - the open and the close would swap and the
        // bar would still pass every sanity check, because both numbers are real prices inside the range.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/candlesticks", GatePayloads.SpotCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { BaseUrlHttp = server.HttpBase });
        Instrument instrument = Spot();
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        Bar first = bars[0];
        Assert.Equal(83853.4m, first.Open.Value);
        Assert.Equal(83864.5m, first.High.Value);
        Assert.Equal(83829.6m, first.Low.Value);
        Assert.Equal(83859m, first.Close.Value);

        // The base volume, not the quote one. Index 6 is 4.23818400 BTC; index 1 is 355349 USDT and would make the
        // bar look eighty thousand times as busy as it was.
        Assert.Equal(4.238184m, first.Volume.Value);

        // A stamp is an OPEN and a bar is stamped at its CLOSE, so one interval is added.
        Assert.Equal(UnixNanos.FromSeconds(FirstOpen + Minute), first.TsEvent);
    }

    [Fact]
    public async Task A_spot_request_asks_for_seconds_and_names_the_pair_the_venues_own_way()
    {
        // Milliseconds here are refused outright with "less than -9223372036 or larger than 9223372036", and this is
        // the half of the unit that IS loud - so it is pinned, because a change to milliseconds would break the
        // whole venue in one place rather than quietly in every bar.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/candlesticks", GatePayloads.SpotCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { BaseUrlHttp = server.HttpBase });
        Instrument instrument = Spot();
        await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        RecordedRequest sent = server.RequestsTo("/api/v4/spot/candlesticks")[0];

        Assert.Equal("BTC_USDT", sent.Query("currency_pair"));
        Assert.Equal("1m", sent.Query("interval"));

        long from = long.Parse(sent.Query("from")!, CultureInfo.InvariantCulture);
        long to = long.Parse(sent.Query("to")!, CultureInfo.InvariantCulture);

        // Ten digits, not thirteen: a seconds stamp for 2026 is about 1.79e9 and a millisecond one 1.79e12.
        Assert.InRange(from, 1_600_000_000L, 9_999_999_999L);
        Assert.InRange(to, 1_600_000_000L, 9_999_999_999L);
        Assert.True(to >= from);
    }

    [Fact]
    public async Task The_candle_still_forming_is_not_returned_as_a_bar()
    {
        // The last recorded spot row carries the venue's own window-closed flag as "false". The shared window rule
        // decides the same thing from the clock, and both agree here: asked as of a moment inside that interval,
        // five bars come back and not six. A history that included it would say a candle had closed when it had not.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/candlesticks", GatePayloads.SpotCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { BaseUrlHttp = server.HttpBase });
        Instrument instrument = Spot();
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            null,
            null,

            // Thirty seconds into the sixth interval, which is what the recording was taken at.
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute) + 30),
            CancellationToken.None);

        Assert.Equal(5, bars.Count);
        Assert.Equal(UnixNanos.FromSeconds(FirstOpen + (5 * Minute)), bars[^1].TsEvent);
    }

    // ----- perpetual futures -----

    [Fact]
    public async Task A_derivative_row_is_read_by_name_and_its_volume_converted_out_of_contracts()
    {
        // The first recorded row is {"o":"83807.3","v":291170,"t":1790349960,"c":"83817.9","l":"83788.1","h":"83823"}.
        // The fields are named so nothing depends on their order - but `v` is a number of CONTRACTS, and 291170
        // contracts of 0.0001 BTC is 29.117 BTC. Left as published the bar would report a volume ten thousand times
        // too large, which is a number no sanity check would catch.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative();
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        Bar first = bars[0];
        Assert.Equal(83807.3m, first.Open.Value);
        Assert.Equal(83823m, first.High.Value);
        Assert.Equal(83788.1m, first.Low.Value);
        Assert.Equal(83817.9m, first.Close.Value);
        Assert.Equal(29.117m, first.Volume.Value);
        Assert.Equal(UnixNanos.FromSeconds(FirstOpen + Minute), first.TsEvent);
    }

    [Fact]
    public async Task A_contract_with_a_different_multiplier_converts_by_its_own()
    {
        // The conversion is per instrument and not per venue: the family's multipliers run from 0.0001 to 10000000.
        // 291170 contracts of 100 units is 29,117,000 of them.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative("ARIA_USDT", 100m);
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        Assert.Equal(29_117_000m, bars[0].Volume.Value);
    }

    [Fact]
    public async Task A_derivative_request_names_the_contract_rather_than_the_pair()
    {
        // The two markets take the symbol under different parameter names on the same venue - `currency_pair` on
        // spot and `contract` on the derivatives - and a request carrying the wrong one is refused for the missing
        // one rather than answered about the wrong instrument.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative();
        await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        RecordedRequest sent = server.RequestsTo("/api/v4/futures/usdt/candlesticks")[0];
        Assert.Equal("BTC_USDT", sent.Query("contract"));
        Assert.Null(sent.Query("currency_pair"));
    }

    [Fact]
    public async Task A_window_is_asked_for_in_pieces_no_wider_than_the_venue_will_answer()
    {
        // The measurement this exists for: the perpetual market answers a too-wide from..to window with the NEWEST
        // rows of it and nothing to say the front was dropped, where delivery refuses it and spot refuses it with a
        // sentence. So the window is sized here. Asked for three thousand minutes it takes two requests, each no
        // wider than 1999 intervals, and the earlier one starts where the caller asked.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative();
        long start = FirstOpen;
        long end = FirstOpen + (3000 * Minute);

        await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(start),
            UnixNanos.FromSeconds(end),
            null,
            UnixNanos.FromSeconds(end + Minute),
            CancellationToken.None);

        IReadOnlyList<RecordedRequest> sent = server.RequestsTo("/api/v4/futures/usdt/candlesticks");
        Assert.Equal(2, sent.Count);

        foreach (RecordedRequest request in sent)
        {
            long from = long.Parse(request.Query("from")!, CultureInfo.InvariantCulture);
            long to = long.Parse(request.Query("to")!, CultureInfo.InvariantCulture);
            Assert.True(
                (to - from) / Minute <= GateFuturesVenue.CandleSpan,
                $"a window of {(to - from) / Minute} intervals is wider than the {GateFuturesVenue.CandleSpan} the venue answers");
        }
    }

    [Fact]
    public async Task A_spot_window_is_sized_to_the_spot_cap_and_not_the_derivative_one()
    {
        // The two caps differ by a factor of two on one venue. A spot fetch sized to the derivative cap would be
        // refused with "Candlestick range too broad", which is loud - but a derivative fetch sized to the spot cap
        // would silently take twice as many requests, so both are pinned.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/candlesticks", GatePayloads.SpotCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { BaseUrlHttp = server.HttpBase });
        Instrument instrument = Spot();
        long start = FirstOpen;
        long end = FirstOpen + (1500 * Minute);

        await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(start),
            UnixNanos.FromSeconds(end),
            null,
            UnixNanos.FromSeconds(end + Minute),
            CancellationToken.None);

        IReadOnlyList<RecordedRequest> sent = server.RequestsTo("/api/v4/spot/candlesticks");
        Assert.Equal(2, sent.Count);

        foreach (RecordedRequest request in sent)
        {
            long from = long.Parse(request.Query("from")!, CultureInfo.InvariantCulture);
            long to = long.Parse(request.Query("to")!, CultureInfo.InvariantCulture);
            Assert.True((to - from) / Minute <= GateVenue.SpotCandleSpan);
        }
    }

    // ----- delivery -----

    [Fact]
    public async Task A_delivery_row_reads_the_same_way_without_the_two_fields_it_lacks()
    {
        // The delivery market answers the same named objects MINUS `sum`, so a reader that required every field the
        // perpetual market sends would find nothing here. The last recorded row has a volume of zero, which is the
        // venue writing a flat candle for an interval nothing traded in.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/delivery/usdt/candlesticks", GatePayloads.DeliveryCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Delivery, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative("BTC_USDT_20261009");
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (3 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (4 * Minute)),
            CancellationToken.None);

        Assert.Equal(4, bars.Count);
        Assert.Equal(83924.4m, bars[0].Open.Value);
        Assert.Equal(83860.9m, bars[0].Close.Value);
        Assert.Equal(0.004m, bars[0].Volume.Value);
        Assert.Equal(0m, bars[^1].Volume.Value);
    }

    // ----- gaps, which this venue does not leave -----

    [Fact]
    public async Task A_quiet_stretch_needs_nothing_filled_in_because_the_venue_writes_it()
    {
        // Measured on BVOL_USDT: 61 consecutive one-minute rows, every one at zero volume, with no gap between any
        // two. On KuCoin a quiet interval is simply absent from the answer and has to be built, and this adapter
        // deliberately builds nothing - so the proof that the venue really writes them belongs here.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.QuietCandles(FirstOpen, 61))
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative("BVOL_USDT", 1m);
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (60 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (61 * Minute)),
            CancellationToken.None);

        Assert.Equal(61, bars.Count);
        Assert.All(bars, b => Assert.Equal(0m, b.Volume.Value));

        for (int i = 1; i < bars.Count; i++)
        {
            Assert.Equal(Minute * UnixNanos.NanosPerSecond, bars[i].TsEvent.Value - bars[i - 1].TsEvent.Value);
        }
    }

    // ----- the limit, counted from the end the caller anchored -----

    [Fact]
    public async Task A_limit_with_no_start_takes_the_newest_bars()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative();
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            null,
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            2,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        Assert.Equal(2, bars.Count);

        // The sixth recorded row opens at FirstOpen + 5 minutes and so closes at FirstOpen + 6, which is exactly the
        // moment asked as of - closed, not forming - so it is the newest bar and the fifth row is the other one.
        Assert.Equal(UnixNanos.FromSeconds(FirstOpen + (6 * Minute)), bars[^1].TsEvent);
        Assert.Equal(UnixNanos.FromSeconds(FirstOpen + (5 * Minute)), bars[0].TsEvent);
    }

    [Fact]
    public async Task A_limit_with_a_start_takes_the_bars_that_follow_it()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative();
        IReadOnlyList<Bar> bars = await GateHistory.FetchBarsAsync(
            http,
            instrument,
            BarTypeFor(instrument),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            2,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None);

        Assert.Equal(2, bars.Count);
        Assert.Equal(UnixNanos.FromSeconds(FirstOpen + Minute), bars[0].TsEvent);
    }

    [Fact]
    public async Task A_bar_length_the_venue_does_not_keep_never_reaches_the_venue()
    {
        await using LoopbackServer server = new();
        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        Instrument instrument = Derivative();

        await Assert.ThrowsAsync<NotSupportedException>(() => GateHistory.FetchBarsAsync(
            http,
            instrument,
            new BarType(instrument.Id, new BarSpecification(1, BarAggregation.Month, PriceType.Last), AggregationSource.External),
            UnixNanos.FromSeconds(FirstOpen),
            UnixNanos.FromSeconds(FirstOpen + (5 * Minute)),
            null,
            UnixNanos.FromSeconds(FirstOpen + (6 * Minute)),
            CancellationToken.None));

        Assert.Empty(server.Requests);
    }

    // ----- funding -----

    [Fact]
    public async Task Funding_settlements_come_back_oldest_first_and_stamped_in_seconds()
    {
        // The venue answers newest first, and everything that stores history expects oldest first. The stamps are in
        // SECONDS: 1790236800 is 2026-09-22T16:00Z, and read as milliseconds it would be three weeks after the epoch.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/funding_rate", GatePayloads.FundingRates)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        IReadOnlyList<FundingRateUpdate> rates = await GateHistory.FetchFundingRatesAsync(
            http,
            InstrumentId.Parse("BTC_USDT.GATE"),
            UnixNanos.FromSeconds(1790236800),
            UnixNanos.FromSeconds(1790352000),
            UnixNanos.FromSeconds(1790352000),
            CancellationToken.None);

        Assert.Equal(5, rates.Count);
        Assert.Equal(UnixNanos.FromSeconds(1790236800), rates[0].TsEvent);
        Assert.Equal(0.000026m, rates[0].Rate);
        Assert.Equal(UnixNanos.FromSeconds(1790352000), rates[^1].TsEvent);
        Assert.Equal(-0.000007m, rates[^1].Rate);

        for (int i = 1; i < rates.Count; i++)
        {
            Assert.True(rates[i].TsEvent.Value > rates[i - 1].TsEvent.Value);
        }
    }

    [Fact]
    public async Task Funding_is_paged_by_a_thirty_day_window_walked_forward()
    {
        // The paging the endpoint's own limit hides. One request answers the thirty days FOLLOWING `from`, whatever
        // the limit says, so a ninety-day period is three requests whose `from` walks forward by thirty days each -
        // and a loop that stopped on a short page would have read a third of it.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/funding_rate", GatePayloads.FundingRates)
            .Handle);

        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        long now = 1790352000;
        long start = now - (long)TimeSpan.FromDays(90).TotalSeconds;

        await GateHistory.FetchFundingRatesAsync(
            http,
            InstrumentId.Parse("BTC_USDT.GATE"),
            UnixNanos.FromSeconds(start),
            UnixNanos.FromSeconds(now),
            UnixNanos.FromSeconds(now),
            CancellationToken.None);

        IReadOnlyList<RecordedRequest> sent = server.RequestsTo("/api/v4/futures/usdt/funding_rate");
        Assert.Equal(4, sent.Count);

        long window = (long)GateVenue.FundingWindow.TotalSeconds;
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.Equal(
                start + (i * window),
                long.Parse(sent[i].Query("from")!, CultureInfo.InvariantCulture));
        }

        Assert.Equal("BTC_USDT", sent[0].Query("contract"));
        Assert.Equal("1000", sent[0].Query("limit"));
    }

    [Fact]
    public async Task Funding_further_back_than_the_venue_keeps_is_refused_here_with_the_reason()
    {
        // The venue refuses a `from` older than 180 days with "from time exceeds 180-day limit". Refusing it here
        // says the same thing in a sentence a caller can act on, and costs no request.
        await using LoopbackServer server = new();
        using GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        long now = 1790352000;

        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => GateHistory.FetchFundingRatesAsync(
                http,
                InstrumentId.Parse("BTC_USDT.GATE"),
                UnixNanos.FromSeconds(now - (long)TimeSpan.FromDays(200).TotalSeconds),
                UnixNanos.FromSeconds(now),
                UnixNanos.FromSeconds(now),
                CancellationToken.None));

        Assert.Contains("180 days", refused.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Only_the_perpetual_market_has_funding_to_fetch()
    {
        // Spot pairs are not margined and dated contracts settle at expiry, so neither is charged funding and
        // neither has an endpoint to ask. Asking is a programming mistake and says so rather than returning nothing.
        await using LoopbackServer server = new();

        foreach (GateProductType product in new[] { GateProductType.Spot, GateProductType.Delivery })
        {
            using GateHttp http = new(new GateDataClientConfig { ProductType = product, BaseUrlHttp = server.HttpBase });
            await Assert.ThrowsAsync<ArgumentException>(() => GateHistory.FetchFundingRatesAsync(
                http,
                InstrumentId.Parse("BTC_USDT.GATE"),
                UnixNanos.FromSeconds(1790236800),
                null,
                UnixNanos.FromSeconds(1790352000),
                CancellationToken.None));
        }

        Assert.Empty(server.Requests);
    }

    // ----- E7: the helper needs no node -----

    [Fact]
    public void Both_history_helpers_are_public_and_static()
    {
        // E7. A host that stores history has no node, so the helpers have to be callable with an http client and an
        // instrument and nothing else. A private one on a data client is how paging comes to be written twice.
        Type history = typeof(GateHistory);

        Assert.Contains(
            history.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            m => m.Name == nameof(GateHistory.FetchBarsAsync));

        Assert.Contains(
            history.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            m => m.Name == nameof(GateHistory.FetchFundingRatesAsync));
    }
}
