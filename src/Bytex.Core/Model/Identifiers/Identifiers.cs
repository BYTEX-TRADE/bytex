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
/// Identifies a market using versioned, escaped venue and symbol segments.
/// </summary>
public readonly record struct MarketKey : IComparable<MarketKey>
{
    public MarketKey(Symbol symbol, Venue venue)
    {
        Symbol = symbol;
        Venue = venue;
        Value = $"bx-market:v2/{Uri.EscapeDataString(venue.Value)}/{Uri.EscapeDataString(symbol.Value)}";
    }

    public Symbol Symbol { get; }

    public Venue Venue { get; }

    public string Value { get; }

    public static MarketKey Parse(string value)
    {
        IdentifierGuard.Require(value, nameof(MarketKey));
        string[] parts = value.Split('/');
        if (parts.Length != 3 || parts[0] != "bx-market:v2" || parts[1].Length == 0 || parts[2].Length == 0)
        {
            throw new FormatException("Market keys use bx-market:v2/<escaped-venue>/<escaped-symbol>.");
        }

        MarketKey key = new(new Symbol(Uri.UnescapeDataString(parts[2])), new Venue(Uri.UnescapeDataString(parts[1])));
        if (!string.Equals(key.Value, value, StringComparison.Ordinal))
        {
            throw new FormatException("The market key is not canonically escaped.");
        }

        return key;
    }

    public static bool TryParse(string? value, out MarketKey id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            id = Parse(value);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public int CompareTo(MarketKey other) => string.CompareOrdinal(Value, other.Value);

    public bool Equals(MarketKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}

public readonly record struct ModuleHostId
{
    public ModuleHostId(string value)
    {
        Value = IdentifierGuard.Require(value, nameof(ModuleHostId));
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

    public bool IsProvider => Value == External.Value;

    public override string ToString() => Value;
}

public readonly record struct RuntimeModuleId
{
    public RuntimeModuleId(string value) => Value = IdentifierGuard.Require(value, nameof(RuntimeModuleId));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct OrderScheduleId
{
    public OrderScheduleId(string value) => Value = IdentifierGuard.Require(value, nameof(OrderScheduleId));

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
