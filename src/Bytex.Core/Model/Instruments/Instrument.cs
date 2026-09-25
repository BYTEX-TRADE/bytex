using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Instruments;

/// <summary>
/// Base definition of a tradable instrument: identity, currencies, precision, limits, fees, and margins.
/// </summary>
public abstract class Instrument
{
    /// <summary>
    /// The spec a subclass hands to this constructor, with the classification that subclass always carries. A
    /// constructor cannot check its argument before the base call, and "spec with { ... }" on a missing spec throws
    /// a NullReferenceException that names nothing - so the check belongs here, where every subclass passes through.
    /// </summary>
    private protected static InstrumentSpec Classify(InstrumentSpec spec, AssetClass? assetClass = null, InstrumentClass? instrumentClass = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec with
        {
            AssetClass = assetClass ?? spec.AssetClass,
            InstrumentClass = instrumentClass ?? spec.InstrumentClass,
        };
    }

    protected Instrument(InstrumentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.QuoteCurrency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spec.PricePrecision, Price.MaxPrecision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spec.SizePrecision, Quantity.MaxPrecision);
        if (spec.PriceIncrement.Value <= 0m)
        {
            throw new ArgumentException("PriceIncrement must be positive.", nameof(spec));
        }

        if (spec.SizeIncrement.Value <= 0m)
        {
            throw new ArgumentException("SizeIncrement must be positive.", nameof(spec));
        }

        Id = spec.Id;
        RawSymbol = spec.RawSymbol ?? spec.Id.Symbol;
        AssetClass = spec.AssetClass;
        InstrumentClass = spec.InstrumentClass;
        QuoteCurrency = spec.QuoteCurrency;
        BaseCurrency = spec.BaseCurrency;
        SettlementCurrency = spec.SettlementCurrency ?? spec.QuoteCurrency;
        IsInverse = spec.IsInverse;
        PricePrecision = spec.PricePrecision;
        SizePrecision = spec.SizePrecision;
        PriceIncrement = spec.PriceIncrement;
        SizeIncrement = spec.SizeIncrement;
        Multiplier = spec.Multiplier ?? new Quantity(1m, 0);
        LotSize = spec.LotSize ?? spec.SizeIncrement;
        MaxQuantity = spec.MaxQuantity;
        MinQuantity = spec.MinQuantity;
        MaxNotional = spec.MaxNotional;
        MinNotional = spec.MinNotional;
        MaxPrice = spec.MaxPrice;
        MinPrice = spec.MinPrice;
        MarginInit = spec.MarginInit;
        MarginMaint = spec.MarginMaint;
        MakerFee = spec.MakerFee;
        TakerFee = spec.TakerFee;
        TsEvent = spec.TsEvent;
        TsInit = spec.TsInit;
        Info = spec.Info ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public InstrumentId Id { get; }

    public Symbol RawSymbol { get; }

    public Venue Venue => Id.Venue;

    public AssetClass AssetClass { get; }

    public InstrumentClass InstrumentClass { get; }

    public Currency QuoteCurrency { get; }

    public Currency? BaseCurrency { get; }

    public Currency SettlementCurrency { get; }

    public bool IsInverse { get; }

    public byte PricePrecision { get; }

    public byte SizePrecision { get; }

    public Price PriceIncrement { get; }

    public Quantity SizeIncrement { get; }

    public Quantity Multiplier { get; }

    public Quantity LotSize { get; }

    public Quantity? MaxQuantity { get; }

    public Quantity? MinQuantity { get; }

    public Money? MaxNotional { get; }

    public Money? MinNotional { get; }

    public Price? MaxPrice { get; }

    public Price? MinPrice { get; }

    /// <summary>Initial margin requirement as a fraction of notional (0.1 = 10%).</summary>
    public decimal MarginInit { get; }

    /// <summary>Maintenance margin requirement as a fraction of notional.</summary>
    public decimal MarginMaint { get; }

    /// <summary>
    /// The share of a position's notional that has to be posted to open it at that leverage. Leverage is what the
    /// holder chose and <see cref="MarginInit"/> is the least this venue will take, so the answer is whichever is
    /// larger: leverage of 1 posts the whole notional, leverage of 10 a tenth, and leverage past the venue's floor
    /// buys nothing further. Multiplying the two - which is what this engine used to do - meant leverage of 1 posting
    /// a twentieth of the notional on a 5% instrument, so an account with no leverage at all carried twenty times
    /// what it could pay for.
    /// </summary>
    public decimal InitialMarginRate(decimal leverage) => Math.Max(leverage <= 0m ? 1m : 1m / leverage, MarginInit);

    /// <summary>
    /// The share of a position's notional that has to stay covered to keep holding it. It does not depend on
    /// leverage: what the holder chose decides what they had to post to open, not what they must keep. Dividing this
    /// by leverage - which is what this engine used to do - made an account at 50x need a fiftieth of the
    /// maintenance margin of the same position, so the more leverage was set the later liquidation came.
    /// </summary>
    public decimal MaintenanceMarginRate => MarginMaint;

    /// <summary>Maker fee as a fraction of notional (0.0002 = 2 bps).</summary>
    public decimal MakerFee { get; }

    /// <summary>Taker fee as a fraction of notional.</summary>
    public decimal TakerFee { get; }

    public UnixNanos TsEvent { get; }

    public UnixNanos TsInit { get; }

    public IReadOnlyDictionary<string, string> Info { get; }

    /// <summary>
    /// Currency that notional values are expressed in (quote currency, or base currency for inverse instruments).
    /// </summary>
    public Currency CostCurrency => IsInverse ? BaseCurrency ?? QuoteCurrency : QuoteCurrency;

    /// <summary>
    /// Creates a price rounded to the nearest valid price increment.
    /// </summary>
    public Price MakePrice(decimal value)
    {
        decimal increment = PriceIncrement.Value;
        decimal rounded = Math.Round(value / increment, 0, MidpointRounding.ToEven) * increment;
        return new Price(rounded, PricePrecision);
    }

    /// <summary>
    /// Creates a quantity rounded down to the nearest valid size increment.
    /// </summary>
    public Quantity MakeQuantity(decimal value, bool roundDown = true)
    {
        decimal increment = SizeIncrement.Value;
        decimal steps = value / increment;
        decimal rounded = (roundDown ? Math.Floor(steps) : Math.Round(steps, 0, MidpointRounding.ToEven)) * increment;
        if (rounded < 0m)
        {
            rounded = 0m;
        }

        return new Quantity(rounded, SizePrecision);
    }

    /// <summary>
    /// Notional value of a quantity at a price, in the instrument's cost currency.
    /// </summary>
    public virtual Money NotionalValue(Quantity quantity, Price price, bool useQuoteForInverse = false)
    {
        if (IsInverse)
        {
            if (useQuoteForInverse)
            {
                return new Money(quantity.Value * Multiplier.Value, QuoteCurrency);
            }

            return new Money(quantity.Value * Multiplier.Value * (1m / price.Value), BaseCurrency ?? QuoteCurrency);
        }

        return new Money(quantity.Value * Multiplier.Value * price.Value, QuoteCurrency);
    }

    public Money CalculateInitialMargin(Quantity quantity, Price price, bool useQuoteForInverse = false)
    {
        Money notional = NotionalValue(quantity, price, useQuoteForInverse);
        return new Money(notional.Amount * MarginInit, notional.Currency);
    }

    public Money CalculateMaintenanceMargin(Quantity quantity, Price price, bool useQuoteForInverse = false)
    {
        Money notional = NotionalValue(quantity, price, useQuoteForInverse);
        return new Money(notional.Amount * MarginMaint, notional.Currency);
    }

    public Money CalculateCommission(Quantity quantity, Price price, LiquiditySide liquiditySide, bool useQuoteForInverse = false)
    {
        Money notional = NotionalValue(quantity, price, useQuoteForInverse);
        decimal rate = liquiditySide == LiquiditySide.Maker ? MakerFee : TakerFee;
        return new Money(notional.Amount * rate, notional.Currency);
    }

    public override string ToString() => $"{GetType().Name}({Id})";
}

/// <summary>
/// Construction parameters shared by all instrument types.
/// </summary>
public sealed record InstrumentSpec
{
    public required InstrumentId Id { get; init; }

    public Symbol? RawSymbol { get; init; }

    public required AssetClass AssetClass { get; init; }

    public required InstrumentClass InstrumentClass { get; init; }

    public required Currency QuoteCurrency { get; init; }

    public Currency? BaseCurrency { get; init; }

    public Currency? SettlementCurrency { get; init; }

    public bool IsInverse { get; init; }

    public required byte PricePrecision { get; init; }

    public required byte SizePrecision { get; init; }

    public required Price PriceIncrement { get; init; }

    public required Quantity SizeIncrement { get; init; }

    public Quantity? Multiplier { get; init; }

    public Quantity? LotSize { get; init; }

    public Quantity? MaxQuantity { get; init; }

    public Quantity? MinQuantity { get; init; }

    public Money? MaxNotional { get; init; }

    public Money? MinNotional { get; init; }

    public Price? MaxPrice { get; init; }

    public Price? MinPrice { get; init; }

    public decimal MarginInit { get; init; }

    public decimal MarginMaint { get; init; }

    public decimal MakerFee { get; init; }

    public decimal TakerFee { get; init; }

    public UnixNanos TsEvent { get; init; }

    public UnixNanos TsInit { get; init; }

    public IReadOnlyDictionary<string, string>? Info { get; init; }
}

/// <summary>
/// A spot currency pair (fiat or crypto), e.g. BTC/USDT.
/// </summary>
public sealed class CurrencyPair : Instrument
{
    public CurrencyPair(InstrumentSpec spec)
        : base(Classify(spec, instrumentClass: InstrumentClass.Spot))
    {
        if (spec.BaseCurrency is null)
        {
            throw new ArgumentException("CurrencyPair requires a base currency.", nameof(spec));
        }
    }

    public new Currency BaseCurrency => base.BaseCurrency!;
}

/// <summary>
/// A perpetual swap contract, linear (settled in quote) or inverse (settled in base).
/// </summary>
public sealed class CryptoPerpetual : Instrument
{
    public CryptoPerpetual(InstrumentSpec spec)
        : base(Classify(spec, AssetClass.Crypto, InstrumentClass.Swap))
    {
        if (spec.BaseCurrency is null)
        {
            throw new ArgumentException("CryptoPerpetual requires a base currency.", nameof(spec));
        }
    }

    public new Currency BaseCurrency => base.BaseCurrency!;
}

/// <summary>
/// A dated crypto futures contract.
/// </summary>
public sealed class CryptoFuture : Instrument
{
    public CryptoFuture(InstrumentSpec spec, Currency underlying, UnixNanos activation, UnixNanos expiration)
        : base(Classify(spec, AssetClass.Crypto, InstrumentClass.Future))
    {
        Underlying = underlying;
        Activation = activation;
        Expiration = expiration;
    }

    public Currency Underlying { get; }

    public UnixNanos Activation { get; }

    public UnixNanos Expiration { get; }
}

/// <summary>
/// An equity (stock) instrument.
/// </summary>
public sealed class Equity : Instrument
{
    public Equity(InstrumentSpec spec, string? isin = null)
        : base(Classify(spec, AssetClass.Equity, InstrumentClass.Spot))
    {
        Isin = isin;
    }

    public string? Isin { get; }
}

/// <summary>
/// An exchange-traded futures contract.
/// </summary>
public sealed class FuturesContract : Instrument
{
    public FuturesContract(InstrumentSpec spec, string underlying, UnixNanos activation, UnixNanos expiration, string? exchange = null)
        : base(Classify(spec, instrumentClass: InstrumentClass.Future))
    {
        Underlying = underlying;
        Activation = activation;
        Expiration = expiration;
        Exchange = exchange;
    }

    public string Underlying { get; }

    public UnixNanos Activation { get; }

    public UnixNanos Expiration { get; }

    public string? Exchange { get; }
}

/// <summary>
/// An option contract.
/// </summary>
public sealed class OptionContract : Instrument
{
    public OptionContract(InstrumentSpec spec, string underlying, OptionKind kind, Price strikePrice, UnixNanos activation, UnixNanos expiration, string? exchange = null)
        : base(Classify(spec, instrumentClass: InstrumentClass.Option))
    {
        Underlying = underlying;
        Kind = kind;
        StrikePrice = strikePrice;
        Activation = activation;
        Expiration = expiration;
        Exchange = exchange;
    }

    public string Underlying { get; }

    public OptionKind Kind { get; }

    public Price StrikePrice { get; }

    public UnixNanos Activation { get; }

    public UnixNanos Expiration { get; }

    public string? Exchange { get; }
}
