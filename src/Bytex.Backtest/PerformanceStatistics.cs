using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest;

/// <summary>
/// What a statistic is worked out from: one currency's side of a run, with everything the engine already worked out.
/// </summary>
/// <param name="Currency">The settlement currency this is about.</param>
/// <param name="StartingBalance">What the account started with in it.</param>
/// <param name="EndingBalance">What it ended with.</param>
/// <param name="EquityCurve">Every point the run measured, oldest first.</param>
/// <param name="Positions">The positions that settled in this currency, open ones included.</param>
/// <param name="Built">
/// The statistics the engine works out itself. Passed in so a statistic can build on them rather than recomputing
/// them - a ratio of two figures already here is a line, and repeating the arithmetic is how two numbers in one report
/// come to disagree.
/// </param>
public sealed record StatisticInput(
    Currency Currency,
    decimal StartingBalance,
    decimal EndingBalance,
    IReadOnlyList<EquityPoint> EquityCurve,
    IReadOnlyList<Position> Positions,
    CurrencyStatistics Built)
{
    /// <summary>The closed positions, which is what most trade-level statistics are about.</summary>
    public IEnumerable<Position> Closed => Positions.Where(p => p.IsClosed);
}

/// <summary>
/// A performance figure a run reports beyond the ones built in (R8.21).
///
/// <para>
/// This exists because the built-in set is a judgement about what matters, and whose money it is decides that. A firm
/// that judges by Calmar, by ulcer index, by turnover, by the P&amp;L of a particular hour of the day, or by a measure of
/// its own, would otherwise have to recompute it outside the engine from the tables - and a figure computed twice from
/// two readings of the same run is how two numbers in one report come to disagree.
/// </para>
///
/// <para>
/// A statistic is asked once per settlement currency, and answers <c>null</c> where it has nothing to say - a currency
/// with no closed position, a run too short to measure. Null is reported as absent rather than as zero, because "no
/// answer" and "zero" are different facts.
/// </para>
/// </summary>
public interface IPerformanceStatistic
{
    /// <summary>What it is called in a result and in <c>result.json</c>. Two statistics cannot share a name.</summary>
    string Name { get; }

    decimal? Compute(StatisticInput input);
}

/// <summary>
/// A statistic from a function, for the common case where a name and a line of arithmetic are the whole of it.
/// </summary>
public sealed class PerformanceStatistic : IPerformanceStatistic
{
    private readonly Func<StatisticInput, decimal?> _compute;

    public PerformanceStatistic(string name, Func<StatisticInput, decimal?> compute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(compute);
        Name = name;
        _compute = compute;
    }

    public string Name { get; }

    public decimal? Compute(StatisticInput input) => _compute(input);
}

/// <summary>
/// Runs a set of statistics over one currency's side of a run.
///
/// <para>
/// A statistic that throws costs its own figure and nothing else: a run that measured something is not thrown away
/// because one of the measurements of it was written badly, and the figure is reported as absent, which is what it is.
/// </para>
/// </summary>
internal static class StatisticRunner
{
    public static IReadOnlyDictionary<string, decimal?> Run(
        IReadOnlyList<IPerformanceStatistic> statistics,
        StatisticInput input,
        Action<string, Exception>? onError = null)
    {
        if (statistics.Count == 0)
        {
            return new Dictionary<string, decimal?>(StringComparer.Ordinal);
        }

        Dictionary<string, decimal?> figures = new(StringComparer.Ordinal);
        foreach (IPerformanceStatistic statistic in statistics)
        {
            if (figures.ContainsKey(statistic.Name))
            {
                throw new InvalidOperationException(
                    $"Two statistics are called '{statistic.Name}'. A figure in a result has one meaning, so the names have to differ.");
            }

            try
            {
                figures[statistic.Name] = statistic.Compute(input);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                onError?.Invoke(statistic.Name, e);
                figures[statistic.Name] = null;
            }
        }

        return figures;
    }
}

/// <summary>
/// The figures a run worked out for itself, by name, as a value.
///
/// <para>
/// It is its own type for one reason: the records that carry it - a currency's statistics, a row of a batch - compare by
/// what is in them, and a plain dictionary compares by reference. Two runs of the same backtest would have come out
/// unequal for holding two dictionaries with the same contents, which is exactly the comparison the determinism guards
/// make. That was found by one of them.
/// </para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(StatisticFiguresConverter))]
public sealed class StatisticFigures : IReadOnlyDictionary<string, decimal?>, IEquatable<StatisticFigures>
{
    public static readonly StatisticFigures None = new(new Dictionary<string, decimal?>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, decimal?> _figures;

    public StatisticFigures(IReadOnlyDictionary<string, decimal?> figures) =>
        _figures = figures ?? throw new ArgumentNullException(nameof(figures));

    public decimal? this[string key] => _figures[key];

    public IEnumerable<string> Keys => _figures.Keys;

    public IEnumerable<decimal?> Values => _figures.Values;

    public int Count => _figures.Count;

    public bool ContainsKey(string key) => _figures.ContainsKey(key);

    public bool TryGetValue(string key, out decimal? value) => _figures.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, decimal?>> GetEnumerator() => _figures.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(StatisticFigures? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other) || ReferenceEquals(_figures, other._figures))
        {
            return true;
        }

        if (Count != other.Count)
        {
            return false;
        }

        foreach ((string name, decimal? value) in _figures)
        {
            if (!other.TryGetValue(name, out decimal? theirs) || theirs != value)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as StatisticFigures);

    public override int GetHashCode()
    {
        // Order-independent, because two dictionaries holding the same figures are the same figures however they were
        // filled.
        int hash = Count;
        foreach ((string name, decimal? value) in _figures)
        {
            hash ^= HashCode.Combine(name, value);
        }

        return hash;
    }

    public override string ToString() =>
        Count == 0 ? "none" : string.Join(", ", _figures.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}"));
}

/// <summary>
/// Reads and writes <see cref="StatisticFigures"/> as a plain object of name to figure.
///
/// <para>
/// It needs one because the type is read-only: it holds a dictionary it was given and offers no way to add to one,
/// which is what makes a run's figures unchangeable after the run - and what stops the serializer populating it.
/// Writing was never the problem. Reading was, the moment these figures started reaching <c>result.json</c>: a file
/// that cannot be read back into the type it came from is a file a program cannot use, and the test that reads one
/// back is the guard that caught this.
/// </para>
///
/// <para>
/// A null figure stays null on both sides. "Asked and unanswerable" is not "never asked", and a converter that
/// dropped nulls would erase the difference the whole feature exists to keep.
/// </para>
/// </summary>
internal sealed class StatisticFiguresConverter : System.Text.Json.Serialization.JsonConverter<StatisticFigures>
{
    public override StatisticFigures Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        Dictionary<string, decimal?> figures = new(StringComparer.Ordinal);
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null)
        {
            return StatisticFigures.None;
        }

        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
        {
            throw new System.Text.Json.JsonException("Statistic figures are an object of name to figure.");
        }

        while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            figures[name] = reader.TokenType == System.Text.Json.JsonTokenType.Null ? null : reader.GetDecimal();
        }

        return new StatisticFigures(figures);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, StatisticFigures value, System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach ((string name, decimal? figure) in value)
        {
            writer.WritePropertyName(name);
            if (figure is { } number)
            {
                writer.WriteNumberValue(number);
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        writer.WriteEndObject();
    }
}
