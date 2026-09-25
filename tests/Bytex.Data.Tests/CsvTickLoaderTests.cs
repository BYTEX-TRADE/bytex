using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// CsvLoader.LoadQuoteTicks / LoadTradeTicks: same header and precision rules as for bars;
// quote sizes default to 1, the trade side is optional and free-form, missing trade ids are numbered from 1.
public sealed class CsvTickLoaderTests : IDisposable
{
    private const long Jan1 = 1_704_067_200_000_000_000L;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Default_quote_columns_map_to_every_quote_field()
    {
        string path = _temp.WriteLines(
            "quotes.csv",
            "timestamp,bid,ask,bid_size,ask_size",
            "2024-01-01T00:00:00.123456712Z,42000.10,42000.11,1.5,0.00025"); // sub-tick digits "12": clear of the rounding bug pinned in CsvTimestampTests

        QuoteTick quote = Assert.Single(CsvLoader.LoadQuoteTicks(path, TestInstruments.BtcUsdt()));

        QuoteTick expected = new(
            Sample.Btc,
            new Price(42000.10m, 2),
            new Price(42000.11m, 2),
            new Quantity(1.5m, 5),
            new Quantity(0.00025m, 5),
            new UnixNanos(Jan1 + 123_456_712),
            new UnixNanos(Jan1 + 123_456_712));
        Assert.Equal(expected, quote);
    }

    [Fact]
    public void Missing_quote_sizes_default_to_one()
    {
        string path = _temp.WriteLines("quotes.csv", "timestamp,bid,ask", "2024-01-01T00:00:00Z,1.08,1.09");

        QuoteTick quote = Assert.Single(CsvLoader.LoadQuoteTicks(path, TestInstruments.BtcUsdt()));

        Assert.Equal(new Quantity(1m, 5), quote.BidSize);
        Assert.Equal(new Quantity(1m, 5), quote.AskSize);
    }

    [Fact]
    public void Custom_quote_columns_are_honoured_and_rows_are_sorted()
    {
        string path = _temp.WriteLines(
            "quotes.tsv",
            "ts\tb\ta\tbs\tas",
            "1704067200000000002\t11\t12\t3\t4",
            "1704067200000000001\t10\t11\t1\t2");
        CsvColumns columns = new() { Timestamp = "ts", Bid = "b", Ask = "a", BidSize = "bs", AskSize = "as", Separator = '\t', TimestampFormat = "unix_ns" };

        IReadOnlyList<QuoteTick> quotes = CsvLoader.LoadQuoteTicks(path, TestInstruments.BtcUsdt(), columns);

        Assert.Equal(new[] { Jan1 + 1, Jan1 + 2 }, quotes.Select(q => q.TsInit.Value));
        Assert.Equal((10m, 11m, 1m, 2m), (quotes[0].Bid.Value, quotes[0].Ask.Value, quotes[0].BidSize.Value, quotes[0].AskSize.Value));
    }

    [Fact]
    public void Quotes_take_the_instrument_id_and_precisions_from_the_instrument()
    {
        string path = _temp.WriteLines("quotes.csv", "timestamp,bid,ask", "2024-01-01T00:00:00Z,1.08123,1.08131");

        QuoteTick quote = Assert.Single(CsvLoader.LoadQuoteTicks(path, TestInstruments.EurUsd()));

        Assert.Equal(InstrumentId.Parse("EUR/USD.SIM"), quote.InstrumentId);
        Assert.Equal((byte)5, quote.Bid.Precision);
        Assert.Equal((byte)0, quote.BidSize.Precision);
    }

    [Theory]
    [InlineData("timestamp,ask", "2024-01-01T00:00:00Z,1.09", "bid")]
    [InlineData("timestamp,bid", "2024-01-01T00:00:00Z,1.08", "ask")]
    public void A_quote_file_without_bid_or_ask_is_rejected(string header, string row, string missing)
    {
        string path = _temp.WriteLines("quotes.csv", header, row);

        FormatException error = Assert.Throws<FormatException>(() => CsvLoader.LoadQuoteTicks(path, TestInstruments.BtcUsdt()));

        Assert.Contains($"'{missing}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_trade_columns_map_to_every_trade_field()
    {
        string path = _temp.WriteLines(
            "trades.csv",
            "timestamp,price,size,side,trade_id",
            "2024-01-01T00:00:00.000000001Z,42000.55,0.12345,buy,987654321");

        TradeTick trade = Assert.Single(CsvLoader.LoadTradeTicks(path, TestInstruments.BtcUsdt()));

        TradeTick expected = new(
            Sample.Btc,
            new Price(42000.55m, 2),
            new Quantity(0.12345m, 5),
            AggressorSide.Buyer,
            new TradeId("987654321"),
            new UnixNanos(Jan1 + 1),
            new UnixNanos(Jan1 + 1));
        Assert.Equal(expected, trade);
    }

    [Theory]
    [InlineData("buy", AggressorSide.Buyer)]
    [InlineData("BUY", AggressorSide.Buyer)]
    [InlineData("Buyer", AggressorSide.Buyer)]
    [InlineData("b", AggressorSide.Buyer)]
    [InlineData("1", AggressorSide.Buyer)]
    [InlineData("sell", AggressorSide.Seller)]
    [InlineData("SELLER", AggressorSide.Seller)]
    [InlineData("s", AggressorSide.Seller)]
    [InlineData("2", AggressorSide.Seller)]
    [InlineData(" sell ", AggressorSide.Seller)]
    [InlineData("", AggressorSide.None)]
    [InlineData("unknown", AggressorSide.None)]
    public void The_side_column_is_mapped_to_the_aggressor(string side, AggressorSide expected)
    {
        string path = _temp.WriteLines("trades.csv", "timestamp,price,size,side", $"2024-01-01T00:00:00Z,1,1,{side}");

        TradeTick trade = Assert.Single(CsvLoader.LoadTradeTicks(path, TestInstruments.BtcUsdt()));

        Assert.Equal(expected, trade.Aggressor);
    }

    [Fact]
    public void Without_side_and_trade_id_columns_the_side_is_none_and_ids_are_numbered_in_file_order()
    {
        string path = _temp.WriteLines(
            "trades.csv",
            "timestamp,price,size",
            "2024-01-01T00:00:02Z,3,1",
            "2024-01-01T00:00:00Z,1,1",
            "2024-01-01T00:00:01Z,2,1");

        IReadOnlyList<TradeTick> trades = CsvLoader.LoadTradeTicks(path, TestInstruments.BtcUsdt());

        Assert.All(trades, t => Assert.Equal(AggressorSide.None, t.Aggressor));
        Assert.Equal(new[] { 1m, 2m, 3m }, trades.Select(t => t.Price.Value)); // sorted by time
        Assert.Equal(new[] { "2", "3", "1" }, trades.Select(t => t.TradeId.Value)); // numbered as read
    }

    [Fact]
    public void Custom_trade_columns_are_honoured()
    {
        string path = _temp.WriteLines("trades.csv", "T|P|Q|Taker|Id", "1704067200123|100.5|2|S|abc-1");
        CsvColumns columns = new() { Timestamp = "t", Price = "p", Size = "q", Side = "taker", TradeId = "id", Separator = '|', TimestampFormat = "unix_ms" };

        TradeTick trade = Assert.Single(CsvLoader.LoadTradeTicks(path, TestInstruments.BtcUsdt(), columns));

        Assert.Equal(Jan1 + 123_000_000, trade.TsEvent.Value);
        Assert.Equal((100.5m, 2m, AggressorSide.Seller, "abc-1"), (trade.Price.Value, trade.Size.Value, trade.Aggressor, trade.TradeId.Value));
    }

    [Theory]
    [InlineData("timestamp,size", "2024-01-01T00:00:00Z,1", "price")]
    [InlineData("timestamp,price", "2024-01-01T00:00:00Z,1", "size")]
    public void A_trade_file_without_price_or_size_is_rejected(string header, string row, string missing)
    {
        string path = _temp.WriteLines("trades.csv", header, row);

        FormatException error = Assert.Throws<FormatException>(() => CsvLoader.LoadTradeTicks(path, TestInstruments.BtcUsdt()));

        Assert.Contains($"'{missing}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ticks_are_parsed_correctly_under_a_comma_decimal_culture()
    {
        string trades = _temp.WriteLines("trades.csv", "timestamp,price,size", "2024-01-01T00:00:00.5Z,42000.55,0.5");
        string quotes = _temp.WriteLines("quotes.csv", "timestamp,bid,ask,bid_size,ask_size", "2024-01-01T00:00:00.5Z,42000.55,42000.56,1.5,2.5");

        using (new CommaDecimalCulture())
        {
            Assert.Equal("0,5", 0.5m.ToString()); // guard: the culture switch is really in effect

            TradeTick trade = Assert.Single(CsvLoader.LoadTradeTicks(trades, TestInstruments.BtcUsdt()));
            QuoteTick quote = Assert.Single(CsvLoader.LoadQuoteTicks(quotes, TestInstruments.BtcUsdt()));

            Assert.Equal((42000.55m, 0.5m, Jan1 + 500_000_000), (trade.Price.Value, trade.Size.Value, trade.TsInit.Value));
            Assert.Equal((42000.55m, 42000.56m, 1.5m, 2.5m), (quote.Bid.Value, quote.Ask.Value, quote.BidSize.Value, quote.AskSize.Value));
        }
    }

    [Fact]
    public void Empty_tick_files_give_empty_lists()
    {
        string empty = _temp.WriteText("empty.csv", string.Empty);
        string headerOnly = _temp.WriteLines("header.csv", "timestamp,price,size,bid,ask");

        Assert.Empty(CsvLoader.LoadQuoteTicks(empty, TestInstruments.BtcUsdt()));
        Assert.Empty(CsvLoader.LoadTradeTicks(empty, TestInstruments.BtcUsdt()));
        Assert.Empty(CsvLoader.LoadQuoteTicks(headerOnly, TestInstruments.BtcUsdt()));
        Assert.Empty(CsvLoader.LoadTradeTicks(headerOnly, TestInstruments.BtcUsdt()));
    }

    [Fact]
    public void Trades_sharing_a_timestamp_keep_their_file_order()
    {
        List<string> lines = ["timestamp,price,size,trade_id"];
        for (int i = 0; i < 200; i++)
        {
            lines.Add($"2024-01-01T00:00:00Z,{100 + i},1,{i}");
        }

        string path = _temp.WriteLines("burst.csv", [.. lines]);

        IReadOnlyList<TradeTick> trades = CsvLoader.LoadTradeTicks(path, TestInstruments.BtcUsdt());

        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString(CultureInfo.InvariantCulture)), trades.Select(t => t.TradeId.Value));
    }
}
