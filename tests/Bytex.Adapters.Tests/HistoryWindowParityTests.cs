using System.Reflection;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests;

// Why: the same request has to give the same bars whichever venue answers, and the two rules that decide it were
// written once per adapter. That is the shape that drifts - and it already had: one copy defaulted to a single page
// when a START was given, which turned "the last six months" into "the last thousand bars" and answered a different
// question successfully.
//
// The subtler half is that Binance and Bybit were taking the FIRST n bars of what they collected, which is correct
// only because they collect exactly n. Their correctness rested on the fetch rather than on the rule, so anything
// that later ADDED a bar would have silently returned the oldest n instead of the newest - and KuCoin does exactly
// that, filling the intervals a venue leaves out. Two venues right by construction and one right on purpose is not
// a shared rule.
//
// Both rules live in BarWindow now. This file is what stops an adapter growing its own copy again.
public sealed class HistoryWindowParityTests
{
    private static readonly BarType _barType = BarType.Parse("BTCUSDT.SIM-1-MINUTE-LAST-EXTERNAL");
    private static readonly UnixNanos _t0 = UnixNanos.FromSeconds(1_700_000_000);

    private static Instrument Instrument() => new CurrencyPair(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT.SIM"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
    });

    /// <summary>Bars a minute apart, close-stamped, the way every adapter returns them.</summary>
    private static List<Bar> Bars(int count)
    {
        Instrument instrument = Instrument();
        List<Bar> bars = new();
        for (int i = 0; i < count; i++)
        {
            UnixNanos close = new(_t0.Value + ((i + 1) * 60L * UnixNanos.NanosPerSecond));
            Price price = instrument.MakePrice(100m + i);
            bars.Add(new Bar(_barType, price, price, price, price, instrument.MakeQuantity(1m), close, close));
        }

        return bars;
    }

    // ----- how many a request is for -----

    [Theory]
    [InlineData(50, false, 50)]
    [InlineData(50, true, 50)]
    [InlineData(null, false, 500)]
    public void A_request_with_no_start_and_no_limit_is_one_page(int? limit, bool hasStart, int expected)
    {
        UnixNanos? start = hasStart ? _t0 : null;
        Assert.Equal(expected, BarWindow.Wanted(limit, start, 500));
    }

    [Fact]
    public void A_request_with_a_start_and_no_limit_is_the_whole_window_not_a_page()
    {
        // The defect this rule had once. A start means the caller named a window and wants it, so defaulting to a
        // page here answers "the last thousand bars" to a question that asked for six months - successfully, which
        // is what made it hard to see.
        Assert.Equal(int.MaxValue, BarWindow.Wanted(null, _t0, 500));
    }

    // ----- which end a limit is counted from -----

    [Fact]
    public void With_a_start_a_limit_counts_forwards_from_it()
    {
        List<Bar> bars = Bars(10);
        IReadOnlyList<Bar> capped = BarWindow.Capped(bars, _t0, 3);

        Assert.Equal(3, capped.Count);
        Assert.Equal(bars[0].TsEvent, capped[0].TsEvent);
        Assert.Equal(bars[2].TsEvent, capped[^1].TsEvent);
    }

    [Fact]
    public void With_no_start_a_limit_counts_backwards_from_the_newest()
    {
        // Nothing anchors the front of the window, so "give me 3" means the three most recent. Taking the first
        // three would hand back the oldest bars the helper happened to have collected.
        List<Bar> bars = Bars(10);
        IReadOnlyList<Bar> capped = BarWindow.Capped(bars, null, 3);

        Assert.Equal(3, capped.Count);
        Assert.Equal(bars[7].TsEvent, capped[0].TsEvent);
        Assert.Equal(bars[^1].TsEvent, capped[^1].TsEvent);
    }

    [Fact]
    public void A_whole_window_is_not_trimmed()
    {
        // The two halves of the seam, joined: a start and no limit is a whole window, and a whole window keeps every
        // bar whichever end it would otherwise be counted from. Asserting the pair together is the point - each half
        // is right on its own in both the broken and the fixed version, and it was their disagreement that turned
        // "the last six months" into "the last thousand bars".
        List<Bar> bars = Bars(10);
        int wanted = BarWindow.Wanted(null, _t0, 500);

        Assert.Same(bars, BarWindow.Capped(bars, _t0, wanted));
        Assert.Same(bars, BarWindow.Capped(bars, null, wanted));
    }

    [Theory]
    [InlineData(3, 10)]  // fewer than asked for
    [InlineData(3, 3)]   // and exactly as many, which is still not too many
    public void No_more_bars_than_the_limit_means_nothing_to_trim(int count, int limit)
    {
        // The boundary, named. Trimming an exact-length window would still return the right bars, so only the
        // untouched collection itself shows that the limit was not applied - and a copy taken here is a copy taken
        // on every bar of every history request.
        List<Bar> bars = Bars(count);
        Assert.Same(bars, BarWindow.Capped(bars, null, limit));
        Assert.Same(bars, BarWindow.Capped(bars, _t0, limit));
    }

    // ----- and no adapter keeps its own copy -----

    [Fact]
    public void No_adapter_decides_the_window_or_the_limit_for_itself()
    {
        // The half that matters. Both rules were written once per adapter and drifted; a venue that grows its own
        // copy again fails here, naming the line, rather than being found when two venues answer one request
        // differently.
        foreach (string venue in Repo.ShippedVenues())
        {
            foreach (string file in Repo.SourceFiles(venue))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    // The "no limit means a page" rule, in the shape every copy of it had.
                    Assert.False(
                        line.Contains("?? (start is null ?", StringComparison.Ordinal)
                        || line.Contains("?? (startAt is null ?", StringComparison.Ordinal),
                        $"{Path.GetFileName(file)}:{i + 1} decides for itself how many bars a request is for. "
                        + $"{nameof(BarWindow)}.{nameof(BarWindow.Wanted)} is that rule.");

                    // Trimming a finished window by hand, which is the rule about which end a limit counts from.
                    Assert.False(
                        line.Contains("BarWindow.Closed", StringComparison.Ordinal)
                        && (line.Contains(".Take(", StringComparison.Ordinal) || line.Contains(".TakeLast(", StringComparison.Ordinal)),
                        $"{Path.GetFileName(file)}:{i + 1} trims a window itself. "
                        + $"{nameof(BarWindow)}.{nameof(BarWindow.Capped)} counts a limit from the end the caller anchored.");
                }
            }
        }
    }

    [Fact]
    public void Every_history_helper_goes_through_the_shared_window()
    {
        // The other direction: a helper that returns bars without bounding them to closed ones would report a
        // candle that is still forming as though it had closed, which is the one thing a stored bar must never be.
        foreach (string venue in Repo.ShippedVenues())
        {
            Type? history = Assembly.Load("Bytex.Adapters." + venue)
                .GetType($"Bytex.Adapters.{venue}.{venue}History", throwOnError: false);

            if (history is null)
            {
                continue;
            }

            string source = File.ReadAllText(Repo.SourceFiles(venue).Single(f => Path.GetFileNameWithoutExtension(f) == history.Name));
            Assert.Contains($"{nameof(BarWindow)}.{nameof(BarWindow.Closed)}", source, StringComparison.Ordinal);
            Assert.Contains($"{nameof(BarWindow)}.{nameof(BarWindow.Capped)}", source, StringComparison.Ordinal);
            Assert.Contains($"{nameof(BarWindow)}.{nameof(BarWindow.Wanted)}", source, StringComparison.Ordinal);
        }
    }
}
