using System.Globalization;
using System.Text.Json;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Runtime;

public sealed class NodeParams
{
    private readonly JsonElement _root;
    private readonly IReadOnlyDictionary<string, decimal> _parameters;
    private readonly IReadOnlyDictionary<string, string> _texts;

    public NodeParams(JsonElement? root, IReadOnlyDictionary<string, decimal> parameters, IReadOnlyDictionary<string, string>? texts = null)
    {
        _root = root ?? default;
        _parameters = parameters;
        _texts = texts ?? EmptyTexts;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTexts = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Has(string name) => _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out JsonElement e) && e.ValueKind != JsonValueKind.Null;

    public JsonElement? Raw(string name) => Has(name) ? _root.GetProperty(name) : null;

    public NodeParams Obj(string name) => new(Raw(name), _parameters, _texts);

    public decimal Dec(string name, decimal @default)
    {
        JsonElement? e = Raw(name);
        if (e is null)
        {
            return @default;
        }

        ParamValue? v = DocumentJson.ReadParamValue(e.Value);
        return v?.Resolve(_parameters) ?? @default;
    }

    public decimal? DecOrNull(string name)
    {
        JsonElement? e = Raw(name);
        if (e is null)
        {
            return null;
        }

        ParamValue? v = DocumentJson.ReadParamValue(e.Value);
        return v?.Resolve(_parameters);
    }

    public int Int(string name, int @default) => (int)Math.Round(Dec(name, @default), MidpointRounding.ToEven);

    public bool Bool(string name, bool @default)
    {
        JsonElement? e = Raw(name);
        return e is { ValueKind: JsonValueKind.True } ? true : e is { ValueKind: JsonValueKind.False } ? false : @default;
    }

    public string Str(string name, string @default) => StrOrNull(name) ?? @default;

    /// <summary>The text of a string or enum param, following a <c>{"$param": "name"}</c> reference to the document parameter.</summary>
    public string? StrOrNull(string name)
    {
        JsonElement? e = Raw(name);
        if (e is { ValueKind: JsonValueKind.String } s)
        {
            return s.GetString();
        }

        return e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("$param", out JsonElement p) && p.ValueKind == JsonValueKind.String && _texts.TryGetValue(p.GetString()!, out string? text)
            ? text
            : null;
    }

    public TimeOnly Time(string name, TimeOnly @default)
    {
        string? s = StrOrNull(name);
        return s is not null && TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t) ? t : @default;
    }

    public IReadOnlyList<string> StrList(string name)
    {
        JsonElement? e = Raw(name);
        if (e is not { ValueKind: JsonValueKind.Array } arr)
        {
            return [];
        }

        List<string> list = new();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }

        return list;
    }
}
