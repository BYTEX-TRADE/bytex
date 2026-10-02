namespace Bytex.Core.Serialization;

/// <summary>Reversible filesystem-safe segments shared by archives and their consumers.</summary>
public static class ObjectKeyCodec
{
    public static string Encode(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("/", "%2F", StringComparison.Ordinal)
        .Replace("\\", "%5C", StringComparison.Ordinal)
        .Replace(":", "%3A", StringComparison.Ordinal);

    public static string Decode(string value) => value
        .Replace("%2F", "/", StringComparison.Ordinal)
        .Replace("%5C", "\\", StringComparison.Ordinal)
        .Replace("%3A", ":", StringComparison.Ordinal)
        .Replace("%25", "%", StringComparison.Ordinal);
}
