using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Adapters;

/// <summary>
/// How a product family decides what a position in it is collateralised in.
///
/// <para>
/// Every member is a case measured in a shipped adapter. The distinction exists because a venue's families are told
/// apart by their collateral and by nothing else a host can read: Binance's coin-margined family and its USDⓈ-M one
/// both hold swaps and dated futures, Bybit's linear and inverse families both hold swaps and dated futures, and
/// Bitget's two perpetual families both hold swaps. A host asking "which family of this venue holds a perpetual" gets
/// the first one declared and has no way to ask for the other.
/// </para>
/// </summary>
public enum CollateralKind
{
    /// <summary>Nothing is posted, because nothing is borrowed. Spot on every venue.</summary>
    None,

    /// <summary>
    /// Whatever the instrument is priced in - a linear contract, where notional is quantity times price and the
    /// settlement currency is the quote currency. A family declaring this holds NO coin-margined contracts, which is
    /// a fact about what the adapter offers as much as about the venue: OKX and KuCoin both list inverse markets that
    /// their adapters here deliberately leave out, and this is where a host can see that.
    /// </summary>
    QuoteCurrency,

    /// <summary>
    /// The instrument's own base currency - an inverse contract, sized in quote-currency contracts and settled in the
    /// coin, where notional divides by the price instead of multiplying. Binance coin-margined and Bybit inverse.
    /// </summary>
    BaseCurrency,

    /// <summary>
    /// A set the venue fixes for the whole family, whatever an instrument is quoted in, and the only member that
    /// carries currencies with it. Bitget is why it exists: its two perpetual families are USDT- and USDC-margined,
    /// every contract in each listing exactly one margin coin, and nothing else distinguishes them.
    /// </summary>
    Currencies,
}

/// <summary>
/// What a family's positions are collateralised in, as the fact that tells two families of one venue apart.
///
/// <para>
/// It is a kind plus, for one kind only, the currencies. Kept as one field rather than two so that a reader cannot
/// find a list without the rule for reading it, and validated on construction so that a family cannot declare
/// currencies it does not use or a fixed set with nothing in it.
/// </para>
/// </summary>
public sealed record VenueCollateral
{
    /// <summary>
    /// Public, and annotated, because a host reads this declaration out of <c>bytex venues --json</c> rather than by
    /// referencing the engine - so the type has to come back from its own JSON, and a fact that only survives in
    /// process is a fact for callers who never needed a declaration. Deserialising runs the same validation as
    /// constructing, which is deliberate: JSON claiming a fixed set with no currencies is as wrong as code doing it,
    /// and finding out at the boundary beats carrying it inward.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public VenueCollateral(CollateralKind kind, IReadOnlyList<Currency> currencies)
    {
        ArgumentNullException.ThrowIfNull(currencies);

        if (kind == CollateralKind.Currencies && currencies.Count == 0)
        {
            throw new ArgumentException("A family collateralised in a fixed set has to name it.", nameof(currencies));
        }

        if (kind != CollateralKind.Currencies && currencies.Count > 0)
        {
            throw new ArgumentException($"Collateral of {kind} is decided per instrument, so it carries no currencies.", nameof(currencies));
        }

        Kind = kind;
        Currencies = currencies;
    }

    public CollateralKind Kind { get; }

    /// <summary>The fixed set, and empty for every other kind - where the instrument's own currencies decide.</summary>
    public IReadOnlyList<Currency> Currencies { get; }

    /// <summary>Nothing is borrowed here.</summary>
    public static VenueCollateral None { get; } = new(CollateralKind.None, []);

    /// <summary>Linear: collateralised in whatever the instrument is priced in.</summary>
    public static VenueCollateral Quote { get; } = new(CollateralKind.QuoteCurrency, []);

    /// <summary>Inverse: collateralised in the instrument's own base currency.</summary>
    public static VenueCollateral Base { get; } = new(CollateralKind.BaseCurrency, []);

    /// <summary>Collateralised in a set this venue fixes for the whole family.</summary>
    public static VenueCollateral In(params Currency[] currencies) =>
        new(CollateralKind.Currencies, [.. currencies ?? throw new ArgumentNullException(nameof(currencies))]);

    /// <summary>
    /// Whether this instrument's settlement currency is what the family says it would be. The declaration and the
    /// instruments have to agree, and only the instrument knows its own currencies - so this is the question asked of
    /// both, and <c>CollateralTests</c> asks it of every instrument the recorded payloads produce.
    /// </summary>
    public bool Holds(Currency settlement, Currency? baseCurrency, Currency quoteCurrency) => Kind switch
    {
        CollateralKind.None => true,
        CollateralKind.QuoteCurrency => settlement == quoteCurrency,
        CollateralKind.BaseCurrency => baseCurrency is not null && settlement == baseCurrency,
        CollateralKind.Currencies => Currencies.Contains(settlement),
        _ => false,
    };
}
