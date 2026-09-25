using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Documents.Annotations;

public enum AnnotationSeverity
{
    Info = 0,
    Notice = 1,
    Warning = 2,
    Critical = 3,
}

public enum ScopeLevel
{
    Global,
    AssetClass,
    Currency,
    Venue,
    Instrument,
}

public enum CurrencyRole
{
    Any,
    Base,
    Quote,
}

/// <summary>One scope target of an annotation. An annotation with several scopes shows wherever any of them matches.</summary>
public sealed record AnnotationScope(ScopeLevel Level, string? Target = null, CurrencyRole Role = CurrencyRole.Any)
{
    public static AnnotationScope Global { get; } = new(ScopeLevel.Global);

    public static AnnotationScope ForAssetClass(AssetClass assetClass) => new(ScopeLevel.AssetClass, assetClass.ToString());

    public static AnnotationScope ForCurrency(string code, CurrencyRole role = CurrencyRole.Any) => new(ScopeLevel.Currency, code.ToUpperInvariant(), role);

    public static AnnotationScope ForVenue(Venue venue) => new(ScopeLevel.Venue, venue.Value);

    public static AnnotationScope ForInstrument(InstrumentId id) => new(ScopeLevel.Instrument, id.Value);

    /// <summary>Whether this scope applies to the instrument.</summary>
    public bool Matches(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        switch (Level)
        {
            case ScopeLevel.Global:
                return true;
            case ScopeLevel.AssetClass:
                return string.Equals(Target, instrument.AssetClass.ToString(), StringComparison.OrdinalIgnoreCase);
            case ScopeLevel.Venue:
                return string.Equals(Target, instrument.Venue.Value, StringComparison.OrdinalIgnoreCase);
            case ScopeLevel.Instrument:
                return string.Equals(Target, instrument.Id.Value, StringComparison.OrdinalIgnoreCase);
            case ScopeLevel.Currency:
                {
                    bool baseMatch = instrument.BaseCurrency is { } b && string.Equals(b.Code, Target, StringComparison.OrdinalIgnoreCase);
                    bool quoteMatch = string.Equals(instrument.QuoteCurrency.Code, Target, StringComparison.OrdinalIgnoreCase);
                    return Role switch
                    {
                        CurrencyRole.Base => baseMatch,
                        CurrencyRole.Quote => quoteMatch,
                        _ => baseMatch || quoteMatch,
                    };
                }

            default:
                return false;
        }
    }

    public override string ToString() => Level switch
    {
        ScopeLevel.Global => "global",
        ScopeLevel.Currency => $"currency:{Target}:{Role.ToString().ToLowerInvariant()}",
        _ => $"{Level.ToString().ToLowerInvariant()}:{Target}",
    };
}

/// <summary>
/// A chart annotation: an event (news, calendar item, venue status) with a time, a scope, and a provenance.
/// It is engine data, so it replays in backtests and strategies can subscribe to it.
/// </summary>
public sealed record Annotation(
    Guid Id,
    UnixNanos TsEvent,
    UnixNanos TsInit,
    UnixNanos? TsEnd,
    IReadOnlyList<AnnotationScope> Scopes,
    string Category,
    AnnotationSeverity Severity,
    string Title,
    string? Summary,
    string? Source,
    string Provenance,
    decimal? Confidence = null,
    bool UserOverridden = false) : CustomData(TsEvent, TsInit)
{
    public const string ProvenanceAiPrefix = "ai:";

    /// <summary>True when a model produced the classification rather than a person or a venue.</summary>
    public bool IsAiClassified => Provenance.StartsWith(ProvenanceAiPrefix, StringComparison.OrdinalIgnoreCase);

    public bool IsWindow => TsEnd is not null;

    public bool AppliesTo(Instrument instrument) => Scopes.Any(s => s.Matches(instrument));

    /// <summary>The subscription key used for annotations on the message bus.</summary>
    public static DataType DataTypeFor(string? category = null) =>
        category is null ? DataType.Of<Annotation>() : DataType.Of<Annotation>(new Dictionary<string, string>(StringComparer.Ordinal) { ["category"] = category });
}

/// <summary>Resolves which scopes apply to an instrument, for chart queries and store lookups.</summary>
public static class ScopeResolver
{
    public static IReadOnlyList<AnnotationScope> For(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        List<AnnotationScope> scopes = new()
        {
            AnnotationScope.Global,
            AnnotationScope.ForAssetClass(instrument.AssetClass),
            AnnotationScope.ForVenue(instrument.Venue),
            AnnotationScope.ForInstrument(instrument.Id),
            AnnotationScope.ForCurrency(instrument.QuoteCurrency.Code, CurrencyRole.Quote),
        };
        if (instrument.BaseCurrency is { } b)
        {
            scopes.Add(AnnotationScope.ForCurrency(b.Code, CurrencyRole.Base));
        }

        return scopes;
    }
}
