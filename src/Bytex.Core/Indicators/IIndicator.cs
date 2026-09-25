using Bytex.Core.Model.Data;

namespace Bytex.Core.Indicators;

/// <summary>
/// A technical indicator that can be fed bars, ticks, or raw values.
/// </summary>
public interface IIndicator
{
    string Name { get; }

    bool HasInputs { get; }

    bool IsInitialized { get; }

    void Update(Bar bar);

    void Update(QuoteTick tick);

    void Update(TradeTick tick);

    void Reset();
}

/// <summary>
/// Creates indicators by name with string parameters, for configuration-driven setups.
/// </summary>
public interface IIndicatorFactory
{
    IReadOnlyList<string> Names { get; }

    IIndicator Create(string name, IReadOnlyDictionary<string, string> parameters);
}
