using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// What goes into the catalog must come out bit for bit: the market-data types are value records, so a single
// Assert.Equal covers every field - prices and sizes WITH their precision, both timestamps to the nanosecond,
// enums, ids, flags and sequence numbers. The catalog is re-opened before reading to rule out in-memory state.
public sealed class CatalogRoundTripTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Quote_ticks_round_trip_exactly()
    {
        QuoteTick[] quotes =
        [
            Sample.Quote(0, bid: 42000.10m),
            Sample.Quote(1, bid: 42000.11m),
            Sample.Quote(1_000_000_000, bid: 0.01m),
        ];

        await new ParquetDataCatalog(_temp.Root).WriteQuoteTicksAsync(quotes);
        IReadOnlyList<QuoteTick> restored = await new ParquetDataCatalog(_temp.Root).QuoteTicksAsync(Sample.Btc);

        Assert.Equal(quotes, restored);
        Assert.Equal(Sample.T0 - 7, restored[0].TsEvent.Value);
        Assert.Equal(Sample.T0, restored[0].TsInit.Value);
        Assert.Equal((byte)2, restored[0].Bid.Precision);
        Assert.Equal((byte)5, restored[0].AskSize.Precision);
        Assert.Equal(0.00001m, restored[0].AskSize.Value);
    }

    [Fact]
    public async Task Trade_ticks_round_trip_exactly_for_every_aggressor_side()
    {
        TradeTick[] trades =
        [
            Sample.Trade(0, "1001", AggressorSide.Buyer),
            Sample.Trade(10, "1002", AggressorSide.Seller),
            Sample.Trade(20, "a3f0-über/42", AggressorSide.None),
        ];

        await new ParquetDataCatalog(_temp.Root).WriteTradeTicksAsync(trades);
        IReadOnlyList<TradeTick> restored = await new ParquetDataCatalog(_temp.Root).TradeTicksAsync(Sample.Btc);

        Assert.Equal(trades, restored);
        Assert.Equal(new[] { AggressorSide.Buyer, AggressorSide.Seller, AggressorSide.None }, restored.Select(t => t.Aggressor));
        Assert.Equal(trades[2].TradeId.Value, restored[2].TradeId.Value);
    }

    [Fact]
    public async Task Bars_round_trip_exactly()
    {
        Bar[] bars = [Sample.Bar(0, 42010.00m), Sample.Bar(60_000_000_000, 41990.50m), Sample.Bar(120_000_000_000, 42100.25m)];

        await new ParquetDataCatalog(_temp.Root).WriteBarsAsync(bars);
        IReadOnlyList<Bar> restored = await new ParquetDataCatalog(_temp.Root).BarsAsync(Sample.BtcMinute);

        Assert.Equal(bars, restored);
        Assert.Equal(Sample.BtcMinute, restored[0].BarType);
        Assert.Equal(new Price(42015.55m, 2), restored[0].High);
        Assert.Equal(new Quantity(123.45678m, 5), restored[0].Volume);
    }

    [Fact]
    public async Task A_revision_bar_keeps_its_revision_flag()
    {
        Bar revision = Sample.Bar(0) with { IsRevision = true };

        await new ParquetDataCatalog(_temp.Root).WriteBarsAsync([revision]);
        IReadOnlyList<Bar> restored = await new ParquetDataCatalog(_temp.Root).BarsAsync(Sample.BtcMinute);

        Assert.True(Assert.Single(restored).IsRevision);
    }

    [Fact]
    public async Task Order_book_deltas_round_trip_exactly_for_every_action_side_and_flag()
    {
        OrderBookDelta[] deltas =
        [
            OrderBookDelta.Clear(Sample.Btc, 100, Sample.At(-5), Sample.At(0)),
            Sample.Delta(1, 101, BookAction.Add, OrderSide.Buy, orderId: 1, flags: RecordFlags.Snapshot),
            Sample.Delta(2, 102, BookAction.Add, OrderSide.Sell, orderId: 2, flags: RecordFlags.Snapshot | RecordFlags.Last),
            Sample.Delta(3, 103, BookAction.Update, OrderSide.Buy, orderId: 1, flags: RecordFlags.MarketByOrder),
            Sample.Delta(4, 104, BookAction.Delete, OrderSide.Sell, orderId: 2, flags: RecordFlags.TopOfBook | RecordFlags.Last),
        ];

        await new ParquetDataCatalog(_temp.Root).WriteOrderBookDeltasAsync(deltas);
        IReadOnlyList<OrderBookDelta> restored = await new ParquetDataCatalog(_temp.Root).OrderBookDeltasAsync(Sample.Btc);

        Assert.Equal(deltas, restored);
        Assert.Equal(new[] { BookAction.Clear, BookAction.Add, BookAction.Add, BookAction.Update, BookAction.Delete }, restored.Select(d => d.Action));
        Assert.Equal(RecordFlags.Snapshot | RecordFlags.Last, restored[2].Flags);
    }

    [Fact]
    public async Task Order_ids_and_sequences_above_the_signed_64_bit_range_survive()
    {
        // Parquet has no unsigned 64-bit column in the row model; the value must not be clamped or fail on the way.
        OrderBookDelta delta = Sample.Delta(0, sequence: ulong.MaxValue, orderId: ulong.MaxValue - 1);

        await new ParquetDataCatalog(_temp.Root).WriteOrderBookDeltasAsync([delta]);
        OrderBookDelta restored = Assert.Single(await new ParquetDataCatalog(_temp.Root).OrderBookDeltasAsync(Sample.Btc));

        Assert.Equal(ulong.MaxValue, restored.Sequence);
        Assert.Equal(ulong.MaxValue - 1, restored.Order.OrderId);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42000", 0)]
    [InlineData("0.5", 1)]
    [InlineData("1.10", 2)]
    [InlineData("0.00000001", 8)]
    [InlineData("0.000000000000000001", 18)]
    [InlineData("12345678901.123456789012345678", 18)]
    [InlineData("-37.63", 2)] // prices may be negative (spreads, the 2020 WTI front month)
    public async Task Prices_keep_value_and_precision_across_the_whole_supported_range(string text, byte precision)
    {
        Price price = new(decimal.Parse(text, CultureInfo.InvariantCulture), precision);
        TradeTick trade = new(Sample.Btc, price, new Quantity(1m, 0), AggressorSide.Buyer, new TradeId("p"), Sample.At(0), Sample.At(0));

        await new ParquetDataCatalog(_temp.Root).WriteTradeTicksAsync([trade]);
        TradeTick restored = Assert.Single(await new ParquetDataCatalog(_temp.Root).TradeTicksAsync(Sample.Btc));

        Assert.Equal(price.Value, restored.Price.Value);
        Assert.Equal(precision, restored.Price.Precision);
        Assert.Equal(price.ToString(), restored.Price.ToString());
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.001", 3)]
    [InlineData("1000000000", 0)]
    [InlineData("0.000000000000000001", 18)]
    public async Task Sizes_keep_value_and_precision(string text, byte precision)
    {
        Quantity size = new(decimal.Parse(text, CultureInfo.InvariantCulture), precision);
        TradeTick trade = new(Sample.Btc, new Price(1m, 0), size, AggressorSide.Seller, new TradeId("q"), Sample.At(0), Sample.At(0));

        await new ParquetDataCatalog(_temp.Root).WriteTradeTicksAsync([trade]);
        TradeTick restored = Assert.Single(await new ParquetDataCatalog(_temp.Root).TradeTicksAsync(Sample.Btc));

        Assert.Equal(size, restored.Size);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(1_704_067_200_123_456_789L)]
    [InlineData(long.MaxValue)]
    public async Task Timestamps_are_stored_as_whole_nanoseconds(long nanos)
    {
        QuoteTick quote = Sample.Quote(0) with { TsEvent = new UnixNanos(nanos), TsInit = new UnixNanos(nanos) };

        await new ParquetDataCatalog(_temp.Root).WriteQuoteTicksAsync([quote]);
        QuoteTick restored = Assert.Single(await new ParquetDataCatalog(_temp.Root).QuoteTicksAsync(Sample.Btc));

        Assert.Equal(nanos, restored.TsEvent.Value);
        Assert.Equal(nanos, restored.TsInit.Value);
    }

    [Fact]
    public async Task Event_and_init_timestamps_are_kept_apart()
    {
        QuoteTick quote = Sample.Quote(0) with { TsEvent = new UnixNanos(111), TsInit = new UnixNanos(999) };

        await new ParquetDataCatalog(_temp.Root).WriteQuoteTicksAsync([quote]);
        QuoteTick restored = Assert.Single(await new ParquetDataCatalog(_temp.Root).QuoteTicksAsync(Sample.Btc));

        Assert.Equal(111, restored.TsEvent.Value);
        Assert.Equal(999, restored.TsInit.Value);
    }

    [Fact]
    public async Task A_mixed_stream_is_dispatched_to_the_right_stores()
    {
        IData[] mixed =
        [
            Sample.Quote(0),
            Sample.Trade(1),
            Sample.Bar(2),
            Sample.Delta(3, 1),
            Sample.Quote(4, id: Sample.Eth),
            Sample.Bar(5, barType: Sample.BtcHour),
            new FundingRateUpdate(Sample.Btc, 0.0001m, null, new UnixNanos(6L), new UnixNanos(6L)),
        ];
        ParquetDataCatalog catalog = new(_temp.Root);

        await catalog.WriteAsync(mixed);

        Assert.Equal(Sample.Quote(0), Assert.Single(await catalog.QuoteTicksAsync(Sample.Btc)));
        Assert.Equal(Sample.Quote(4, id: Sample.Eth), Assert.Single(await catalog.QuoteTicksAsync(Sample.Eth)));
        Assert.Equal(Sample.Trade(1), Assert.Single(await catalog.TradeTicksAsync(Sample.Btc)));
        Assert.Equal(Sample.Bar(2), Assert.Single(await catalog.BarsAsync(Sample.BtcMinute)));
        Assert.Equal(Sample.Bar(5, barType: Sample.BtcHour), Assert.Single(await catalog.BarsAsync(Sample.BtcHour)));
        Assert.Equal(Sample.Delta(3, 1), Assert.Single(await catalog.OrderBookDeltasAsync(Sample.Btc)));
        Assert.Equal(0.0001m, Assert.Single(await catalog.FundingRatesAsync(Sample.Btc)).Rate);
        Assert.Empty(await catalog.TradeTicksAsync(Sample.Eth));
    }

    [Fact]
    public async Task Funding_rates_round_trip_exactly()
    {
        // R8.15: a perpetual backtest is only as honest as the rates it pays, so the rate, both timestamps and the
        // next funding time have to survive the catalog exactly. A venue that did not say when the next one is comes
        // back as not having said it, rather than as the beginning of 1970.
        FundingRateUpdate[] rates =
        [
            new(Sample.Btc, 0.0001m, new UnixNanos(8L * UnixNanos.NanosPerHour), new UnixNanos(1L), new UnixNanos(2L)),
            new(Sample.Btc, -0.000375m, null, new UnixNanos(3L), new UnixNanos(4L)),
        ];
        ParquetDataCatalog catalog = new(_temp.Root);

        await catalog.WriteFundingRatesAsync(rates);

        IReadOnlyList<FundingRateUpdate> read = await new ParquetDataCatalog(_temp.Root).FundingRatesAsync(Sample.Btc);
        Assert.Equal(rates, read);
        Assert.Null(read[1].NextFundingTime);
    }

    [Fact]
    public async Task A_symbol_containing_a_slash_round_trips_under_its_real_id()
    {
        InstrumentId eurUsd = InstrumentId.Parse("EUR/USD.SIM");
        QuoteTick quote = Sample.Quote(0, bid: 1.08m, id: eurUsd);
        ParquetDataCatalog catalog = new(_temp.Root);

        await catalog.WriteQuoteTicksAsync([quote]);

        Assert.Equal(quote, Assert.Single(await catalog.QuoteTicksAsync(eurUsd)));
    }

    [Fact]
    public async Task Data_written_under_a_comma_decimal_culture_reads_back_identically()
    {
        Bar[] bars = [Sample.Bar(0, 42010.07m), Sample.Bar(60_000_000_000, 41990.53m)];

        using (new CommaDecimalCulture())
        {
            Assert.Equal("0,5", 0.5m.ToString()); // guard: the culture switch is really in effect
            await new ParquetDataCatalog(_temp.Root).WriteBarsAsync(bars);
        }

        Assert.Equal(bars, await new ParquetDataCatalog(_temp.Root).BarsAsync(Sample.BtcMinute));
        using (new CommaDecimalCulture())
        {
            Assert.Equal(bars, await new ParquetDataCatalog(_temp.Root).BarsAsync(Sample.BtcMinute));
        }
    }

    [Fact]
    public async Task Writing_nothing_creates_nothing()
    {
        ParquetDataCatalog catalog = new(_temp.Root);

        await catalog.WriteBarsAsync([]);
        await catalog.WriteAsync([]);

        Assert.Empty(catalog.Entries());
        Assert.Empty(catalog.BarTypes());
    }
}
