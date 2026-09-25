using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Model;

// Why: three venues answered the same question three ways - one filtered by a candle's open, one by its close, one in
// seconds and one in milliseconds, and one returned the candle it was still building. A caller asking three venues
// for the same period got three different periods, and a catalog filled from them disagreed with itself at the edges.
// An adapter does whatever its venue needs; what a window MEANS is decided here, once, and this is that decision.
public sealed class BarWindowTests
{
    private static readonly Instrument _instrument = TestInstruments.BtcUsdt();
    private static readonly BarType _barType = new(_instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));
    private const long Minute = 60_000_000_000L;

    /// <summary>A bar closing at the given minute, stamped at its close as every adapter stamps it.</summary>
    private static Bar At(long minute)
    {
        UnixNanos close = new(minute * Minute);
        return new Bar(_barType, _instrument.MakePrice(1m), _instrument.MakePrice(1m), _instrument.MakePrice(1m), _instrument.MakePrice(1m), _instrument.MakeQuantity(1m), close, close);
    }

    private static long Minutes(long n) => n * Minute;

    [Fact]
    public void The_bar_that_closes_exactly_at_the_start_is_inside_the_window()
    {
        // It covers the window's first moment, so the period asked for is covered from its beginning. This is the one
        // bar the venues disagreed about.
        IReadOnlyList<Bar> window = BarWindow.Closed([At(9), At(10), At(11)], _barType, new UnixNanos(Minutes(10)), new UnixNanos(Minutes(20)), new UnixNanos(Minutes(100)));

        Assert.Equal([Minutes(10), Minutes(11)], window.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public void A_bar_that_closed_before_the_window_is_not_in_it()
    {
        IReadOnlyList<Bar> window = BarWindow.Closed([At(8), At(9)], _barType, new UnixNanos(Minutes(10)), new UnixNanos(Minutes(20)), new UnixNanos(Minutes(100)));

        Assert.Equal([Minutes(10)], window.Select(b => b.TsEvent.Value).DefaultIfEmpty(Minutes(10)));
        Assert.Empty(window);
    }

    [Fact]
    public void A_bar_that_opens_after_the_end_is_not_in_it()
    {
        // The bar stamped one minute past the end opened at the end, so it is in; the next one opened after it.
        IReadOnlyList<Bar> window = BarWindow.Closed([At(20), At(21), At(22)], _barType, new UnixNanos(Minutes(10)), new UnixNanos(Minutes(20)), new UnixNanos(Minutes(100)));

        Assert.Equal([Minutes(20), Minutes(21)], window.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public void The_bar_still_forming_is_never_in_it()
    {
        // A history that carries it says a candle closed when it has not, and the next run of the same period would
        // disagree with this one.
        IReadOnlyList<Bar> window = BarWindow.Closed([At(9), At(10), At(11)], _barType, null, new UnixNanos(Minutes(30)), new UnixNanos(Minutes(10) + 1));

        Assert.Equal([Minutes(9), Minutes(10)], window.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public void An_end_beyond_now_is_bounded_by_now()
    {
        IReadOnlyList<Bar> window = BarWindow.Closed([At(10), At(11), At(12)], _barType, null, new UnixNanos(Minutes(100)), new UnixNanos(Minutes(11)));

        Assert.Equal([Minutes(10), Minutes(11)], window.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public void The_same_bar_twice_comes_back_once_and_in_order()
    {
        // Paging overlaps: a venue asked for two windows that touch answers the boundary bar in both pages.
        IReadOnlyList<Bar> window = BarWindow.Closed([At(12), At(10), At(11), At(10)], _barType, null, null, new UnixNanos(Minutes(100)));

        Assert.Equal([Minutes(10), Minutes(11), Minutes(12)], window.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public void A_window_with_no_bounds_is_every_bar_that_has_closed()
    {
        IReadOnlyList<Bar> window = BarWindow.Closed([At(10), At(11)], _barType, null, null, new UnixNanos(Minutes(100)));

        Assert.Equal([Minutes(10), Minutes(11)], window.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public void A_bar_type_with_no_length_has_no_window()
    {
        // A window is bounded by where each bar opens, which is its close less its length. A specification carrying
        // no length - nothing validates one, it is a plain record - would silently make every bar's open equal its
        // close and quietly change which bars a window holds, so it is refused instead.
        BarType lengthless = new(_instrument.Id, new BarSpecification(0, BarAggregation.Minute, PriceType.Last));

        ArgumentException error = Assert.Throws<ArgumentException>(() => BarWindow.Closed([], lengthless, null, null, new UnixNanos(Minutes(1))));

        Assert.Contains("no window to bound", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bar_type_that_is_not_time_aggregated_is_refused_before_this_is_asked()
    {
        // Tick and volume bars have no length at all, and the specification itself says so - the window rule never
        // sees them.
        BarType ticks = new(_instrument.Id, new BarSpecification(100, BarAggregation.Tick, PriceType.Last));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => BarWindow.Closed([], ticks, null, null, new UnixNanos(Minutes(1))));

        Assert.Contains("not time based", error.Message, StringComparison.Ordinal);
    }
}
