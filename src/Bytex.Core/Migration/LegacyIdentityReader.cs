using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Core.Migration;

/// <summary>Reads BYTEX v1 identities only for explicit migration; never used by normal v2 parsers.</summary>
public static class LegacyIdentityReader
{
    public static MarketKey ReadMarket(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        int separator = text.LastIndexOf('.');
        if (separator <= 0 || separator == text.Length - 1)
        {
            throw new FormatException("The legacy market identity requires a symbol and venue.");
        }

        return new MarketKey(new Symbol(text[..separator]), new Venue(text[(separator + 1)..]));
    }

    public static CandleSeries ReadCandle(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string[] parts = text.Split('-');
        if (parts.Length < 5)
        {
            throw new FormatException("The legacy candle identity is incomplete.");
        }

        int count = parts.Length;
        SamplingRule rule = new(int.Parse(parts[count - 4], CultureInfo.InvariantCulture),
            SamplingRule.ParseAggregation(parts[count - 3]),
            Enum.Parse<PriceType>(parts[count - 2], true));
        CandleOrigin origin = parts[count - 1].ToUpperInvariant() switch
        {
            "EXTERNAL" => CandleOrigin.Provider,
            "INTERNAL" => CandleOrigin.Computed,
            _ => throw new FormatException("The legacy candle origin is unknown."),
        };
        if (rule.Step <= 0 || !Enum.IsDefined(rule.Aggregation) || !Enum.IsDefined(rule.PriceType))
        {
            throw new FormatException("The legacy sampling rule is invalid.");
        }

        return new CandleSeries(ReadMarket(string.Join('-', parts.Take(count - 4))), rule, origin);
    }
}
