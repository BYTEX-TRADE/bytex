using System.Globalization;
using System.IO.Compression;
using System.Text;
using Bytex.Adapters.Tardis;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Tardis;

// Why: the matcher has walked an order book since 0.6 and nothing could feed it real depth. The free archives of the
// venues themselves carry top-of-book or a band metric, so converting either would be inventing the levels - and this
// vendor publishes the first day of every month with no key at all, 25 levels a side. Loading it was the only missing
// piece: OrderBookDepth already carries arbitrary-length ladders, the book applies them multi-level, and the backtest
// engine routes them.
//
// What these hold is the reading of the file, which is where this can go wrong quietly. A row pads both sides to
// twenty-five, so a book eleven deep has fourteen empty pairs after it; read as zeros they become a bid at zero and a
// fill finds it. And the limit matters more here than anywhere else in this client - a day is about 1.5 million rows
// of fifty levels, which is why the deltas loader beside this one, which takes no limit, cannot be used at all.
public sealed class TardisBookSnapshotTests
{
    private static readonly MarketKey _btc = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT-PERP");

    private static CryptoPerpetual Contract() => new(new InstrumentSpec
    {
        Id = _btc,
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
    });

    /// <summary>
    /// The header these files carry: four fixed columns, then FOUR per level, interleaved - an ask pair and then a
    /// bid pair, level by level. The stub serves them as <c>text/csv</c> because that is what the vendor really
    /// sends, which is why the client checks the gzip marker rather than the declared type.
    ///
    /// <para>
    /// This builder said twenty-five asks and then twenty-five bids, matching the parser, so the pair round-tripped
    /// and agreed with each other about something the vendor does not do. That is why the first test below reads
    /// the vendor's own file instead.
    /// </para>
    /// </summary>
    private static string Header()
    {
        StringBuilder header = new("exchange,symbol,timestamp,local_timestamp");
        for (int i = 0; i < TardisDataClient.BookLevels; i++)
        {
            header.Append(CultureInfo.InvariantCulture, $",asks[{i}].price,asks[{i}].amount,bids[{i}].price,bids[{i}].amount");
        }

        return header.ToString();
    }

    /// <summary>
    /// One row, with <paramref name="askDepth"/> asks and <paramref name="bidDepth"/> bids and the rest of the
    /// columns left EMPTY, exactly as the vendor pads them.
    /// </summary>
    private static string Row(long micros, int askDepth, int bidDepth)
    {
        StringBuilder row = new(string.Create(CultureInfo.InvariantCulture, $"binance-futures,BTCUSDT,{micros},{micros + 400}"));
        for (int i = 0; i < TardisDataClient.BookLevels; i++)
        {
            // Interleaved, as the vendor writes it: this level's ask pair, then this level's bid pair.
            row.Append(i < askDepth ? string.Create(CultureInfo.InvariantCulture, $",{61208 + i}.0,{0.01m + i}") : ",,");
            row.Append(i < bidDepth ? string.Create(CultureInfo.InvariantCulture, $",{61207 - i}.9,{11.568m + i}") : ",,");
        }

        return row.ToString();
    }

    private static byte[] Gzip(params string[] lines)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(string.Join('\n', lines) + '\n'));
        }

        return output.ToArray();
    }

    /// <summary>
    /// The vendor's URL shape, so a wrong dataset name or a wrong date path is a 404 here rather than a silently
    /// empty answer: <c>{exchange}/{dataType}/{year}/{month}/{day}/{symbol}.csv.gz</c>.
    /// </summary>
    private static string UrlFor(DateOnly day) =>
        $"/binance-futures/{TardisDataClient.BookSnapshotDataType}/{day.Year}/{day.Month:00}/{day.Day:00}/BTCUSDT.csv.gz";

    private static (LoopbackServer Server, TardisDataClient Client, TestTradingRuntime TradingRuntime) Rig(
        DateOnly day, byte[] payload, string contentType = "text/csv", string? apiKey = null)
    {
        Routes routes = new();
        routes.On("GET", UrlFor(day), _ => new StubResponse(200, string.Empty, contentType, payload));
        LoopbackServer server = new(routes.Handle);
        TestTradingRuntime tradingRuntime = new();
        tradingRuntime.TradingRuntime.Cache.AddInstrument(Contract());
        TardisDataClient client = new(
            new ClientId("TARDIS"),
            new TardisDataClientConfig { ApiKey = apiKey, BaseUrl = server.HttpBase },
            tradingRuntime.Services);

        return (server, client, tradingRuntime);
    }

    /// <summary>
    /// <b>The vendor's own file, three rows of it, checked in.</b>
    ///
    /// <para>
    /// This is the test that had to exist. The parser read the columns as twenty-five asks followed by twenty-five
    /// bids; they are interleaved, four to a level. Both ladders came out crossed and non-monotonic - the ask
    /// ladder alternating with the bids beneath it, the bid ladder starting at the thirteenth bid - and nothing
    /// threw, because a price and its amount stay adjacent under either reading.
    /// </para>
    ///
    /// <para>
    /// A constructed fixture could not catch it, and did not: it was built from the same assumption as the parser,
    /// so the two agreed with each other about something the vendor does not do. Only its file disagreed. The
    /// assertions here are the ones a wrong layout cannot satisfy - a side that only ever moves away from the touch,
    /// and a best bid strictly below the best ask.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_vendors_own_rows_parse_into_ladders_that_are_ordered_and_uncrossed()
    {
        DateOnly day = new(2024, 3, 1);
        (LoopbackServer server, TardisDataClient client, TestTradingRuntime tradingRuntime) = Rig(day, Gzip(RealRows()));
        await using (server)
        {
            using (tradingRuntime)
            {
                IReadOnlyList<IData> depth = await client.LoadBookSnapshotsAsync(_btc, Start(day), End(day), 10, CancellationToken.None);

                Assert.Equal(3, depth.Count);
                foreach (OrderBookDepth book in depth.Cast<OrderBookDepth>())
                {
                    Assert.Equal(TardisDataClient.BookLevels, book.Asks.Count);
                    Assert.Equal(TardisDataClient.BookLevels, book.Bids.Count);

                    // Asks only ever rise and bids only ever fall, away from the touch.
                    for (int level = 1; level < book.Asks.Count; level++)
                    {
                        Assert.True(book.Asks[level].Price > book.Asks[level - 1].Price, $"asks are not ascending at level {level}: {book.Asks[level - 1].Price} then {book.Asks[level].Price}");
                        Assert.True(book.Bids[level].Price < book.Bids[level - 1].Price, $"bids are not descending at level {level}: {book.Bids[level - 1].Price} then {book.Bids[level].Price}");
                    }

                    // And the book is not crossed, which a ladder made of both sides cannot manage.
                    Assert.True(book.Bids[0].Price < book.Asks[0].Price, $"the book is crossed: bid {book.Bids[0].Price} is not below ask {book.Asks[0].Price}");
                }

                // The first row of 2024-03-01, read off the file by hand.
                OrderBookDepth first = Assert.IsType<OrderBookDepth>(depth[0]);
                Assert.Equal(new Price(61208.0m, 1), first.Asks[0].Price);
                Assert.Equal(new Quantity(0.01m, 3), first.Asks[0].Size);
                Assert.Equal(new Price(61207.9m, 1), first.Bids[0].Price);
                Assert.Equal(new Quantity(11.568m, 3), first.Bids[0].Size);
                Assert.Equal(new Price(61208.1m, 1), first.Asks[1].Price);
                Assert.Equal(new Price(61207.8m, 1), first.Bids[1].Price);
                Assert.True(first.Flags.HasFlag(RecordFlags.Snapshot));
            }
        }
    }

    /// <summary>
    /// <b>A key is not required.</b> The free samples need none, and requiring one made them unreachable through a
    /// client that needs nothing to read them.
    /// </summary>
    [Fact]
    public async Task The_client_is_built_without_a_key_and_sends_no_authorization()
    {
        DateOnly day = new(2024, 3, 1);
        (LoopbackServer server, TardisDataClient client, TestTradingRuntime tradingRuntime) = Rig(day, Gzip(RealRows()));
        await using (server)
        {
            using (tradingRuntime)
            {
                await client.LoadBookSnapshotsAsync(_btc, Start(day), End(day), 1, CancellationToken.None);

                Assert.Null(server.Requests[0].Header("Authorization"));
            }
        }
    }

    /// <summary>
    /// <b>The padding is not depth.</b> A book eleven deep has fourteen empty pairs after it; reading them as zeros
    /// puts a bid at zero in the ladder, and a fill would find it.
    /// </summary>
    [Fact]
    public async Task Empty_levels_end_the_ladder_rather_than_becoming_zeroes()
    {
        DateOnly day = new(2024, 3, 1);
        (LoopbackServer server, TardisDataClient client, TestTradingRuntime tradingRuntime) = Rig(day, Gzip(Header(), Row(1_709_251_200_407_000, askDepth: 3, bidDepth: 11)));
        await using (server)
        {
            using (tradingRuntime)
            {
                IReadOnlyList<IData> depth = await client.LoadBookSnapshotsAsync(_btc, Start(day), End(day), 10, CancellationToken.None);

                OrderBookDepth book = Assert.IsType<OrderBookDepth>(Assert.Single(depth));
                Assert.Equal(3, book.Asks.Count);
                Assert.Equal(11, book.Bids.Count);
                Assert.DoesNotContain(book.Bids, level => level.Price.Value == 0m);
                Assert.DoesNotContain(book.Asks, level => level.Size.Value == 0m);
            }
        }
    }

    /// <summary>
    /// <b>The limit stops the read.</b> A day is about 1.5 million rows of fifty levels, so a caller that cannot
    /// bound it cannot use this - which is exactly why the deltas loader, which takes no limit, is unusable.
    /// </summary>
    [Fact]
    public async Task The_limit_bounds_what_is_read()
    {
        DateOnly day = new(2024, 3, 1);
        string[] rows = [Header(), .. Enumerable.Range(0, 50).Select(i => Row(1_709_251_200_407_000 + (i * 1000), 25, 25))];
        (LoopbackServer server, TardisDataClient client, TestTradingRuntime tradingRuntime) = Rig(day, Gzip(rows));
        await using (server)
        {
            using (tradingRuntime)
            {
                IReadOnlyList<IData> depth = await client.LoadBookSnapshotsAsync(_btc, Start(day), End(day), 7, CancellationToken.None);

                Assert.Equal(7, depth.Count);
            }
        }
    }

    /// <summary>
    /// <b>A body that is not the file is said so.</b> A request this vendor dislikes can answer with an HTML error
    /// page under HTTP 200; that reached the decompressor and died as an InvalidDataException naming neither the
    /// request nor the reason.
    /// </summary>
    [Fact]
    public async Task An_html_page_under_a_200_is_refused_by_content_type()
    {
        DateOnly day = new(2024, 3, 1);
        (LoopbackServer server, TardisDataClient client, TestTradingRuntime tradingRuntime) = Rig(
            day, Encoding.UTF8.GetBytes("<html><body>Range requests are not supported</body></html>"), contentType: "text/html");

        await using (server)
        {
            using (tradingRuntime)
            {
                Exception refused = await Assert.ThrowsAnyAsync<Exception>(
                    () => client.LoadBookSnapshotsAsync(_btc, Start(day), End(day), 10, CancellationToken.None));

                Assert.Contains("text/html", refused.Message, StringComparison.Ordinal);
                Assert.Contains("not gzip", refused.Message, StringComparison.Ordinal);
                Assert.Contains("Range requests are not supported", refused.Message, StringComparison.Ordinal);
                Assert.IsNotType<InvalidDataException>(refused);
            }
        }
    }

    /// <summary>And a key IS sent when there is one, so the paid datasets still work.</summary>
    [Fact]
    public async Task A_key_is_sent_when_one_is_configured()
    {
        DateOnly day = new(2024, 3, 1);
        (LoopbackServer server, TardisDataClient client, TestTradingRuntime tradingRuntime) = Rig(
            day, Gzip(Header(), Row(1_709_251_200_407_000, 25, 25)), apiKey: "TM-secret");

        await using (server)
        {
            using (tradingRuntime)
            {
                await client.LoadBookSnapshotsAsync(_btc, Start(day), End(day), 1, CancellationToken.None);

                Assert.Equal("Bearer TM-secret", server.Requests[0].Header("Authorization"));
            }
        }
    }

    /// <summary>
    /// Three rows of the vendor's own <c>book_snapshot_25</c> for 2024-03-01, checked in beside these tests. A
    /// trimmed slice of the real thing, because a fixture this repository builds itself can only prove that it
    /// agrees with the code that reads it.
    /// </summary>
    private static string[] RealRows() =>
        File.ReadAllLines(Path.Combine(Repo.Root, "tests", "Bytex.Adapters.Tests", "Fixtures", "Tardis", "book_snapshot_25.binance-futures.BTCUSDT.2024-03-01.csv"))
            .Where(line => line.Length > 0)
            .ToArray();

    private static UnixNanos Start(DateOnly day) => UnixNanos.FromDateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private static UnixNanos End(DateOnly day) => UnixNanos.FromDateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc));
}
