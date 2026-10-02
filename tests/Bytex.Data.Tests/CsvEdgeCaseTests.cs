using Bytex.Core.Model.Data;
using Bytex.Data;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// Why (R9.9): a line was split on the separator, so any field containing one shifted every column after it - and
// quoting a field is exactly what a writer does when it contains a separator. It did not fail: the row still parsed,
// into a price taken from something that was never a price.
//
// And a file whose first line is already data could not be read at all, which is most of what an exchange hands out
// of its own archive.
public sealed class CsvEdgeCaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_separator_inside_a_quoted_field_does_not_shift_the_columns()
    {
        // The failure this prevents is silent: without quoting honoured, "Bitcoin, spot" becomes two fields and the
        // close price is read from the volume column.
        string path = _temp.WriteLines(
            "bars.csv",
            "timestamp,name,open,high,low,close,volume",
            "2026-09-27T00:00:00Z,\"Bitcoin, spot\",100.0,110.0,90.0,105.0,12.5");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal(105.0m, bar.Close.Value);
        Assert.Equal(12.5m, bar.Volume.Value);
    }

    [Fact]
    public void A_doubled_quote_inside_a_quoted_field_is_one_quote()
    {
        // What every writer of these files means by it, and the reason trimming the outer quotes is not enough:
        // asserted through a loaded row rather than through the splitter, so it is the behaviour that is pinned.
        string path = _temp.WriteLines(
            "quoted.csv",
            "timestamp,name,open,high,low,close,volume",
            "2026-09-27T00:00:00Z,\"say \"\"hi\"\", and, again\",100.0,110.0,90.0,105.0,12.5");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal(105.0m, bar.Close.Value);
        Assert.Equal(12.5m, bar.Volume.Value);
    }

    [Fact]
    public void A_file_with_no_header_is_read_by_position()
    {
        // A headerless file has no names to match, so the columns are their own positions and the caller says which
        // is which. Inventing names would only move the guess somewhere less visible.
        string path = _temp.WriteLines(
            "headerless.csv",
            "2026-09-27T00:00:00Z,100.0,110.0,90.0,105.0,12.5",
            "2026-09-27T00:01:00Z,105.0,115.0,95.0,110.0,3.5");

        IReadOnlyList<Bar> bars = CsvLoader.LoadBars(
            path,
            TestInstruments.BtcUsdt(),
            Sample.BtcMinute,
            new CsvColumns
            {
                HasHeader = false,
                Timestamp = "0",
                Open = "1",
                High = "2",
                Low = "3",
                Close = "4",
                Volume = "5",
            });

        Assert.Equal(2, bars.Count);
        Assert.Equal(105.0m, bars[0].Close.Value);
        Assert.Equal(110.0m, bars[1].Close.Value);
    }

    [Fact]
    public void A_header_is_still_the_default()
    {
        // The change must not move ground under files that already work.
        string path = _temp.WriteLines(
            "bars.csv",
            "timestamp,open,high,low,close,volume",
            "2026-09-27T00:00:00Z,100.0,110.0,90.0,105.0,12.5");

        Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));
    }
}
