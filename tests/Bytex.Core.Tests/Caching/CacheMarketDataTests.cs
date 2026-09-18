using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Caching;

// Why: R6.1 and R6.3 - strategies read prices from the cache with "index 0 is the most recent", windows are
// bounded, and cross-currency valuation depends on the exchange-rate lookup. Rates below are hand computed.
public class CacheMarketDataTests
{
    private static UnixNanos Sec(long s) => UnixNanos.FromSeconds(s);

    private static QuoteTick Quote(InstrumentId id, string bid, string ask, long ts = 0) =>
        new(id, Price.Parse(bid), Price.Parse(ask), Quantity.Parse("1.000"), Quantity.Parse("1.000"), Sec(ts), Sec(ts));

    private static TradeTick Trade(InstrumentId id, string price, long ts = 0) =>
        new(id, Price.Parse(price), Quantity.Parse("1.000"), AggressorSide.Buyer, new TradeId($"T-{ts}"), Sec(ts), Sec(ts));

    private static Bar MinuteBar(BarType type, string close, long ts, bool revision = false) =>
        new(type, Price.Parse(close), Price.Parse(close), Price.Parse(close), Price.Parse(close), Quantity.Parse("1.000"), Sec(ts), Sec(ts), revision);

    private static BarType BtcMinute => BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");

    [Fact]
    public void Instruments_are_looked_up_by_id_and_listed_by_venue()
    {
        Cache cache = new();
        CurrencyPair btc = TestInstruments.BtcUsdt();
        cache.AddInstrument(btc);
        cache.AddInstrument(TestInstruments.EthUsdt());
        cache.AddInstrument(TestInstruments.BtcPerp());

        Assert.Same(btc, cache.Instrument(TestIds.BtcUsdt));
        Assert.Null(cache.Instrument(TestIds.EthBtc));
        Assert.Equal(3, cache.Instruments().Count);
        Assert.Equal([TestIds.BtcUsdt, TestIds.EthUsdt], cache.InstrumentIds(TestIds.Binance).Order());
        Assert.Equal([TestIds.BtcPerp], cache.InstrumentIds(TestIds.Bybit));
    }

    [Fact]
    public void Adding_an_instrument_again_replaces_the_definition()
    {
        Cache cache = new();
        cache.AddInstrument(TestInstruments.BtcUsdt());
        CurrencyPair updated = TestInstruments.BtcUsdt(maxQuantity: Quantity.Parse("5.000"));

        cache.AddInstrument(updated);

        Assert.Same(updated, cache.Instrument(TestIds.BtcUsdt));
        Assert.Single(cache.Instruments());
    }

    [Fact]
    public void Quote_window_is_most_recent_first_and_bounded_by_the_tick_capacity()
    {
        Cache cache = new(new CacheConfig { TickCapacity = 3 });
        for (int i = 1; i <= 5; i++)
        {
            cache.AddQuoteTick(Quote(TestIds.BtcUsdt, $"{i}.00", $"{i}.10", i));
        }

        Assert.Equal(3, cache.QuoteTickCount(TestIds.BtcUsdt));
        Assert.Equal(Price.Parse("5.00"), cache.QuoteTick(TestIds.BtcUsdt)!.Value.Bid);
        Assert.Equal(Price.Parse("3.00"), cache.QuoteTick(TestIds.BtcUsdt, 2)!.Value.Bid);
        Assert.Null(cache.QuoteTick(TestIds.BtcUsdt, 3));
        Assert.Equal(["5.00", "4.00", "3.00"], cache.QuoteTicks(TestIds.BtcUsdt).Select(q => q.Bid.ToString()));
        Assert.True(cache.HasQuoteTicks(TestIds.BtcUsdt));
    }

    [Fact]
    public void Trade_window_is_most_recent_first_and_bounded_by_the_tick_capacity()
    {
        Cache cache = new(new CacheConfig { TickCapacity = 2 });

        cache.AddTradeTicks([Trade(TestIds.BtcUsdt, "1.00", 1), Trade(TestIds.BtcUsdt, "2.00", 2), Trade(TestIds.BtcUsdt, "3.00", 3)]);

        Assert.Equal(["3.00", "2.00"], cache.TradeTicks(TestIds.BtcUsdt).Select(t => t.Price.ToString()));
        Assert.Equal(2, cache.TradeTickCount(TestIds.BtcUsdt));
        Assert.Null(cache.TradeTick(TestIds.BtcUsdt, 2));
    }

    [Fact]
    public void Bar_window_is_kept_per_bar_type_and_bounded_by_the_bar_capacity()
    {
        Cache cache = new(new CacheConfig { BarCapacity = 2 });
        BarType fiveMinute = BarType.Parse("BTCUSDT.BINANCE-5-MINUTE-LAST-EXTERNAL");
        BarType eth = BarType.Parse("ETHUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");

        cache.AddBars([MinuteBar(BtcMinute, "1.00", 60), MinuteBar(BtcMinute, "2.00", 120), MinuteBar(BtcMinute, "3.00", 180)]);
        cache.AddBar(MinuteBar(fiveMinute, "9.00", 300));
        cache.AddBar(MinuteBar(eth, "7.00", 60));

        Assert.Equal(["3.00", "2.00"], cache.Bars(BtcMinute).Select(b => b.Close.ToString()));
        Assert.Equal(1, cache.BarCount(fiveMinute));
        Assert.Equal(
            ["BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL", "BTCUSDT.BINANCE-5-MINUTE-LAST-EXTERNAL"],
            cache.BarTypes(TestIds.BtcUsdt).Select(b => b.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(3, cache.BarTypes().Count);
        Assert.False(cache.HasBars(BarType.Parse("ETHUSDT.BINANCE-5-MINUTE-LAST-EXTERNAL")));
    }

    [Fact]
    public void Windows_of_different_instruments_do_not_mix()
    {
        Cache cache = new();
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "50000.00", "50001.00"));
        cache.AddQuoteTick(Quote(TestIds.EthUsdt, "2500.00", "2501.00"));

        Assert.Equal(1, cache.QuoteTickCount(TestIds.BtcUsdt));
        Assert.Equal(Price.Parse("2500.00"), cache.QuoteTick(TestIds.EthUsdt)!.Value.Bid);
        Assert.Empty(cache.QuoteTicks(TestIds.EthBtc));
    }

    [Fact]
    public void Revised_bar_replaces_the_latest_bar_only_when_the_timestamps_match()
    {
        Cache cache = new();
        cache.AddBar(MinuteBar(BtcMinute, "1.00", 60));
        cache.AddBar(MinuteBar(BtcMinute, "2.00", 120));

        cache.AddBar(MinuteBar(BtcMinute, "2.50", 120, revision: true));
        cache.AddBar(MinuteBar(BtcMinute, "3.00", 180, revision: true));

        Assert.Equal(["3.00", "2.50", "1.00"], cache.Bars(BtcMinute).Select(b => b.Close.ToString()));
    }

    [Theory]
    [InlineData(PriceType.Bid, "49990.00")]
    [InlineData(PriceType.Ask, "50010.00")]
    [InlineData(PriceType.Mid, "50000.000")] // (49,990 + 50,010) / 2, one extra decimal
    [InlineData(PriceType.Last, "50005.00")]
    [InlineData(PriceType.Mark, "50002.00")]
    public void Price_comes_from_the_source_that_matches_the_price_type(PriceType type, string expected)
    {
        Cache cache = new();
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "49990.00", "50010.00"));
        cache.AddTradeTick(Trade(TestIds.BtcUsdt, "50005.00"));
        cache.AddMarkPrice(new MarkPriceUpdate(TestIds.BtcUsdt, Price.Parse("50002.00"), Sec(0), Sec(0)));

        Assert.Equal(Price.Parse(expected), cache.Price(TestIds.BtcUsdt, type));
    }

    [Fact]
    public void Price_falls_back_to_the_latest_bar_close_and_then_to_the_last_trade()
    {
        Cache onlyBars = new();
        onlyBars.AddBar(MinuteBar(BtcMinute, "42.00", 60));
        Cache onlyTrades = new();
        onlyTrades.AddTradeTick(Trade(TestIds.BtcUsdt, "43.00"));

        Assert.Equal(Price.Parse("42.00"), onlyBars.Price(TestIds.BtcUsdt, PriceType.Bid));
        Assert.Equal(Price.Parse("42.00"), onlyBars.Price(TestIds.BtcUsdt, PriceType.Last));
        Assert.Equal(Price.Parse("43.00"), onlyTrades.Price(TestIds.BtcUsdt, PriceType.Ask));
        Assert.Null(new Cache().Price(TestIds.BtcUsdt, PriceType.Mid));
    }

    [Fact]
    public void Exchange_rate_of_a_currency_to_itself_is_one_without_any_data()
    {
        Assert.Equal(1m, new Cache().ExchangeRate(Currencies.USDT, Currencies.USDT));
    }

    [Fact]
    public void Exchange_rate_uses_a_direct_pair_and_its_inverse()
    {
        Cache cache = new();
        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "49990.00", "50010.00"));

        // mid = 50,000; inverse = 1 / 50,000 = 0.00002
        Assert.Equal(50_000m, cache.ExchangeRate(Currencies.BTC, Currencies.USDT));
        Assert.Equal(0.00002m, cache.ExchangeRate(Currencies.USDT, Currencies.BTC));
        Assert.Equal(49_990m, cache.ExchangeRate(Currencies.BTC, Currencies.USDT, PriceType.Bid));
        Assert.Equal(50_010m, cache.ExchangeRate(Currencies.BTC, Currencies.USDT, PriceType.Ask));
    }

    [Fact]
    public void Exchange_rate_bridges_two_pairs_through_their_common_quote_currency()
    {
        Cache cache = new();
        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddInstrument(TestInstruments.EthUsdt());
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "49990.00", "50010.00")); // mid 50,000
        cache.AddQuoteTick(Quote(TestIds.EthUsdt, "2499.00", "2501.00")); // mid 2,500

        // ETH -> USDT -> BTC: 2,500 / 50,000 = 0.05; BTC -> ETH: 50,000 / 2,500 = 20
        Assert.Equal(0.05m, cache.ExchangeRate(Currencies.ETH, Currencies.BTC));
        Assert.Equal(20m, cache.ExchangeRate(Currencies.BTC, Currencies.ETH));
    }

    [Fact]
    public void Exchange_rate_prefers_a_direct_pair_over_a_bridge()
    {
        Cache cache = new();
        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddInstrument(TestInstruments.EthUsdt());
        cache.AddInstrument(TestInstruments.EthBtc());
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "49990.00", "50010.00"));
        cache.AddQuoteTick(Quote(TestIds.EthUsdt, "2499.00", "2501.00"));
        cache.AddQuoteTick(new QuoteTick(TestIds.EthBtc, Price.Parse("0.05190"), Price.Parse("0.05210"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), Sec(0), Sec(0)));

        // The ETHBTC book says 0.052 although the USDT bridge implies 0.05.
        Assert.Equal(0.052m, cache.ExchangeRate(Currencies.ETH, Currencies.BTC));
    }

    [Fact]
    public void Exchange_rate_is_unknown_without_a_priced_path()
    {
        Cache cache = new();
        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddInstrument(TestInstruments.EthUsdt());
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "49990.00", "50010.00"));

        Assert.Null(cache.ExchangeRate(Currencies.ETH, Currencies.BTC)); // ETHUSDT has no price yet
        Assert.Null(cache.ExchangeRate(Currencies.BTC, Currencies.EUR)); // no instrument at all
    }

    [Fact]
    public void Order_book_is_created_once_per_instrument()
    {
        Cache cache = new();

        OrderBook first = cache.GetOrCreateOrderBook(TestIds.BtcUsdt, BookType.L2);
        OrderBook second = cache.GetOrCreateOrderBook(TestIds.BtcUsdt, BookType.L3);

        Assert.Same(first, second);
        Assert.Same(first, cache.OrderBook(TestIds.BtcUsdt));
        Assert.Equal(BookType.L2, second.BookType);
        Assert.Null(cache.OrderBook(TestIds.EthUsdt));
    }

    [Fact]
    public void Reset_clears_market_data_but_keeps_instruments_and_flush_clears_both()
    {
        Cache cache = new();
        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddQuoteTick(Quote(TestIds.BtcUsdt, "1.00", "1.10"));
        cache.AddTradeTick(Trade(TestIds.BtcUsdt, "1.05"));
        cache.AddBar(MinuteBar(BtcMinute, "1.05", 60));
        cache.GetOrCreateOrderBook(TestIds.BtcUsdt, BookType.L2);

        cache.Reset();

        Assert.False(cache.HasQuoteTicks(TestIds.BtcUsdt));
        Assert.False(cache.HasTradeTicks(TestIds.BtcUsdt));
        Assert.False(cache.HasBars(BtcMinute));
        Assert.Null(cache.OrderBook(TestIds.BtcUsdt));
        Assert.NotNull(cache.Instrument(TestIds.BtcUsdt));

        cache.Flush();

        Assert.Empty(cache.Instruments());
    }
}
