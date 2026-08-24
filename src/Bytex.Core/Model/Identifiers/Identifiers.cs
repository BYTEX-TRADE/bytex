namespace Bytex.Core.Model.Identifiers;

internal static class IdentifierGuard
{
    public static string Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} must not be empty.", name);
        }

        return value;
    }
}

/// <summary>
/// A trading venue or exchange identifier, e.g. "BINANCE".
/// </summary>
public readonly record struct Venue
{
    public Venue(string value)
    {
        Value = IdentifierGuard.Require(value, nameof(Venue));
        if (Value.Contains('.'))
        {
            throw new ArgumentException("Venue must not contain '.'.", nameof(value));
        }
    }

    public string Value { get; }

    public static readonly Venue Simulated = new("SIM");

    public override string ToString() => Value;

    public static implicit operator string(Venue venue) => venue.Value;
}

/// <summary>
/// A venue-native symbol, e.g. "BTCUSDT".
/// </summary>
public readonly record struct Symbol
{
    public Symbol(string value) => Value = IdentifierGuard.Require(value, nameof(Symbol));

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator string(Symbol symbol) => symbol.Value;
}

/// <summary>
/// Identifies an instrument at a venue, rendered as "SYMBOL.VENUE".
/// </summary>
public readonly record struct InstrumentId : IComparable<InstrumentId>
{
    public InstrumentId(Symbol symbol, Venue venue)
    {
        Symbol = symbol;
        Venue = venue;
        Value = symbol.Value + "." + venue.Value;
    }

    public Symbol Symbol { get; }

    public Venue Venue { get; }

    public string Value { get; }

    public static InstrumentId Parse(string value)
    {
        IdentifierGuard.Require(value, nameof(InstrumentId));
        int dot = value.LastIndexOf('.');
        if (dot <= 0 || dot == value.Length - 1)
        {
            throw new FormatException($"InstrumentId '{value}' must be in the form 'SYMBOL.VENUE'.");
        }

        return new InstrumentId(new Symbol(value.Substring(0, dot)), new Venue(value.Substring(dot + 1)));
    }

    public static bool TryParse(string? value, out InstrumentId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int dot = value.LastIndexOf('.');
        if (dot <= 0 || dot == value.Length - 1)
        {
            return false;
        }

        id = new InstrumentId(new Symbol(value.Substring(0, dot)), new Venue(value.Substring(dot + 1)));
        return true;
    }

    public int CompareTo(InstrumentId other) => string.CompareOrdinal(Value, other.Value);

    public bool Equals(InstrumentId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}

public readonly record struct TraderId
{
    public TraderId(string value)
    {
        Value = IdentifierGuard.Require(value, nameof(TraderId));
        int dash = Value.LastIndexOf('-');
        Tag = dash > 0 ? Value.Substring(dash + 1) : Value;
    }

    public string Value { get; }

    /// <summary>The segment after the last hyphen, used in generated order identifiers.</summary>
    public string Tag { get; }

    public override string ToString() => Value;
}

public readonly record struct StrategyId
{
    public StrategyId(string value)
    {
        Value = IdentifierGuard.Require(value, nameof(StrategyId));
        int dash = Value.LastIndexOf('-');
        Tag = dash > 0 ? Value.Substring(dash + 1) : Value;
    }

    public string Value { get; }

    public string Tag { get; }

    /// <summary>Identifier used for orders discovered at a venue that no strategy claims.</summary>
    public static readonly StrategyId External = new("EXTERNAL");

    public bool IsExternal => Value == External.Value;

    public override string ToString() => Value;
}

public readonly record struct ActorId
{
    public ActorId(string value) => Value = IdentifierGuard.Require(value, nameof(ActorId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ExecAlgorithmId
{
    public ExecAlgorithmId(string value) => Value = IdentifierGuard.Require(value, nameof(ExecAlgorithmId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ComponentId
{
    public ComponentId(string value) => Value = IdentifierGuard.Require(value, nameof(ComponentId));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Identifies a data or execution client. By convention the venue name, optionally suffixed ("BINANCE-SPOT").
/// </summary>
public readonly record struct ClientId
{
    public ClientId(string value) => Value = IdentifierGuard.Require(value, nameof(ClientId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct AccountId
{
    public AccountId(string value)
    {
        Value = IdentifierGuard.Require(value, nameof(AccountId));
        int dash = Value.IndexOf('-');
        if (dash <= 0 || dash == Value.Length - 1)
        {
            throw new ArgumentException("AccountId must be in the form 'VENUE-NUMBER'.", nameof(value));
        }

        Issuer = Value.Substring(0, dash);
        Number = Value.Substring(dash + 1);
    }

    public string Value { get; }

    public string Issuer { get; }

    public string Number { get; }

    public Venue Venue => new(Issuer);

    public override string ToString() => Value;
}

public readonly record struct ClientOrderId
{
    public ClientOrderId(string value) => Value = IdentifierGuard.Require(value, nameof(ClientOrderId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct VenueOrderId
{
    public VenueOrderId(string value) => Value = IdentifierGuard.Require(value, nameof(VenueOrderId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TradeId
{
    public TradeId(string value) => Value = IdentifierGuard.Require(value, nameof(TradeId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct PositionId
{
    public PositionId(string value) => Value = IdentifierGuard.Require(value, nameof(PositionId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct OrderListId
{
    public OrderListId(string value) => Value = IdentifierGuard.Require(value, nameof(OrderListId));

    public string Value { get; }

    public override string ToString() => Value;
}
