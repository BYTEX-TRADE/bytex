using System.Globalization;
using Bytex.Data;

namespace Bytex.Cli;

/// <summary>
/// Turns what a caller typed into a <see cref="CsvColumns"/> (R9.9).
///
/// <para>
/// The loader has read headerless files and honoured quoted fields since the edge cases were worked; what it could
/// not do was be asked for either from the command line, which is most of what somebody importing an exchange's own
/// archive has. The syntax is one option rather than fourteen: a file's columns are one decision, and naming them
/// one flag at a time would make the command unreadable before it made it usable.
/// </para>
/// </summary>
internal static class CsvImport
{
    /// <summary>Every field a CSV may map, by the name this command takes for it.</summary>
    private static readonly string[] _fields =
    [
        "timestamp", "open", "high", "low", "close", "volume",
        "bid", "ask", "bidsize", "asksize",
        "price", "size", "side", "tradeid",
    ];

    public static CsvColumns Columns(string timestampFormat, bool noHeader, string? mapping, string separator)
    {
        CsvColumns columns = new()
        {
            TimestampFormat = timestampFormat,
            HasHeader = !noHeader,
            Separator = Separator(separator),
        };

        if (noHeader && string.IsNullOrWhiteSpace(mapping))
        {
            // A headerless file has no names to match, so there is nothing to guess from and guessing is the one
            // thing that must not happen: a column read from the wrong position parses into a plausible price.
            throw new ArgumentException(
                "--no-header needs --columns as well: a file with no header has no names, so each field has to be given its position, for example --columns timestamp=0,open=1,high=2,low=3,close=4,volume=5.",
                nameof(noHeader));
        }

        if (string.IsNullOrWhiteSpace(mapping))
        {
            return columns;
        }

        foreach (string pair in mapping.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                throw new ArgumentException($"'{pair}' is not a field=source pair. Write --columns close=4 or --columns close=last.", nameof(mapping));
            }

            string field = parts[0].ToLowerInvariant();
            if (!_fields.Contains(field, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"'{parts[0]}' is not a field a CSV can be mapped to. The fields are: {string.Join(", ", _fields)}.",
                    nameof(mapping));
            }

            if (noHeader && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new ArgumentException(
                    $"'{parts[1]}' is a column name, and a file with no header has none: give {field} its position instead, counting from 0.",
                    nameof(mapping));
            }

            columns = With(columns, field, parts[1]);
        }

        return columns;
    }

    private static char Separator(string separator) => separator switch
    {
        "tab" or "TAB" or "Tab" or @"\t" => '\t',
        { Length: 1 } one => one[0],
        _ => throw new ArgumentException($"'{separator}' is not a separator: give one character, or 'tab'.", nameof(separator)),
    };

    private static CsvColumns With(CsvColumns columns, string field, string source) => field switch
    {
        "timestamp" => columns with { Timestamp = source },
        "open" => columns with { Open = source },
        "high" => columns with { High = source },
        "low" => columns with { Low = source },
        "close" => columns with { Close = source },
        "volume" => columns with { Volume = source },
        "bid" => columns with { Bid = source },
        "ask" => columns with { Ask = source },
        "bidsize" => columns with { BidSize = source },
        "asksize" => columns with { AskSize = source },
        "price" => columns with { Price = source },
        "size" => columns with { Size = source },
        "side" => columns with { Side = source },
        _ => columns with { TradeId = source },
    };
}
