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
/// An indicator that reads the order book (R7.8).
///
/// <para>
/// Separate from <see cref="IIndicator"/> rather than another method on it, because a book is not a price: an indicator
/// that took one and ignored it would compile, and one that needed one would have no way of saying so. A registration
/// asks for this type, so an indicator that cannot read a book cannot be registered for one.
/// </para>
/// </summary>
public interface IOrderBookIndicator : IIndicator
{
    void Update(OrderBook book);
}

/// <summary>
/// Creates indicators by name with string parameters, for configuration-driven setups.
/// </summary>
public interface IIndicatorFactory
{
    IReadOnlyList<string> Names { get; }

    IIndicator Create(string name, IReadOnlyDictionary<string, string> parameters);
}
