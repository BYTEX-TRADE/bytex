using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Serialization;

namespace Bytex.Cli.Tests;

// Why: the same defect shipped twice in one release and was found by somebody else both times. An identifier or a
// currency used as a DICTIONARY KEY needs ReadAsPropertyName and WriteAsPropertyName on its converter, and a
// converter that only handles values fails at runtime with a message naming the type rather than the option:
//
//   maxNotionalPerOrder  -> "InstrumentId is not a supported dictionary key"  (the only live example, unloadable)
//   result.json          -> "Currency is not a supported dictionary key"      (a result the engine could not re-read)
//
// Both were found by running the product, not by the 3,600 tests. So rather than add a third example, this walks the
// public surface of every shipped assembly, finds every dictionary-typed property, and demands that its key type is
// serialisable AS A KEY. A new option or result keyed by a new type fails here the day it is written.
public sealed class DictionaryKeyContractTests
{
    private static readonly Assembly[] _shipped =
    [
        typeof(BytexJson).Assembly,
        typeof(Bytex.Backtest.BacktestResult).Assembly,
        typeof(Bytex.Live.TradingNodeConfig).Assembly,
        typeof(Bytex.Documents.Schema.StrategyDocument).Assembly,
    ];

    /// <summary>
    /// Every key type of every dictionary-typed public property on a public type, with the property that introduced
    /// it so a failure names something a person can find.
    /// </summary>
    public static TheoryData<string, string> DictionaryKeys()
    {
        SortedDictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (Assembly assembly in _shipped)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (type.IsGenericTypeDefinition)
                {
                    continue;
                }

                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (KeyTypeOf(property.PropertyType) is not { } key || key.AssemblyQualifiedName is null)
                    {
                        continue;
                    }

                    string name = key.AssemblyQualifiedName;
                    if (!found.ContainsKey(name))
                    {
                        found[name] = $"{type.Name}.{property.Name}";
                    }
                }
            }
        }

        TheoryData<string, string> data = new();
        foreach ((string key, string where) in found)
        {
            data.Add(key, where);
        }

        return data;
    }

    private static Type? KeyTypeOf(Type type)
    {
        if (!type.IsGenericType || typeof(string) == type)
        {
            return null;
        }

        Type definition = type.GetGenericTypeDefinition();
        if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>)
            || definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(SortedDictionary<,>))
        {
            return type.GetGenericArguments()[0];
        }

        return null;
    }

    [Fact]
    public void There_are_dictionary_keys_to_check()
    {
        // A reflection walk that silently found nothing would pass every case below.
        Assert.NotEmpty(DictionaryKeys());
    }

    [Theory]
    [MemberData(nameof(DictionaryKeys))]
    public void Every_dictionary_key_type_in_the_public_surface_can_be_a_property_name(string keyTypeName, string where)
    {
        Type key = Type.GetType(keyTypeName, throwOnError: true)!;

        // What System.Text.Json supports without help: strings, enums and the primitives. Anything else needs a
        // converter that says how it becomes a property name.
        if (key == typeof(string) || key.IsEnum || key.IsPrimitive || key == typeof(Guid) || key == typeof(DateTime)
            || key == typeof(DateTimeOffset) || key == typeof(decimal) || key == typeof(Uri))
        {
            return;
        }

        JsonConverter converter = BytexJson.Options.GetConverter(key);
        Assert.NotNull(converter);

        Type declared = typeof(JsonConverter<>).MakeGenericType(key);
        MethodInfo? write = converter.GetType().GetMethod(
            "WriteAsPropertyName",
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(Utf8JsonWriter), key, typeof(JsonSerializerOptions)]);
        MethodInfo? read = converter.GetType().GetMethod(
            "ReadAsPropertyName",
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(Utf8JsonReader).MakeByRefType(), typeof(Type), typeof(JsonSerializerOptions)]);

        Assert.NotNull(write);
        Assert.NotNull(read);
        Assert.False(
            write!.DeclaringType == declared,
            $"{key.Name} is a dictionary key on {where}, and its converter does not override WriteAsPropertyName. "
            + "Serialising that dictionary throws at runtime naming the type and not the option, which is how "
            + "maxNotionalPerOrder made the only live example unloadable.");
        Assert.False(
            read!.DeclaringType == declared,
            $"{key.Name} is a dictionary key on {where}, and its converter does not override ReadAsPropertyName. "
            + "Reading that dictionary throws at runtime, which is how result.json became a file the engine could "
            + "write and could not read back.");
    }

    [Theory]
    [MemberData(nameof(DictionaryKeys))]
    public void Every_dictionary_key_type_survives_being_written_and_read_as_one(string keyTypeName, string where)
    {
        // The contract check above reads the converter; this one uses it. A converter can declare the methods and
        // still round-trip to something different, and a key that does not come back is a key that silently drops
        // whichever option it named.
        Type key = Type.GetType(keyTypeName, throwOnError: true)!;
        if (Sample(key) is not { } sample)
        {
            return;
        }

        Type dictionary = typeof(Dictionary<,>).MakeGenericType(key, typeof(int));
        IDictionary map = (IDictionary)Activator.CreateInstance(dictionary)!;
        map[sample] = 7;

        string json = JsonSerializer.Serialize(map, dictionary, BytexJson.Options);
        IDictionary? back = (IDictionary?)JsonSerializer.Deserialize(json, dictionary, BytexJson.Options);

        Assert.NotNull(back);
        Assert.True(back!.Contains(sample), $"a {key.Name} key on {where} did not come back from JSON: {json}");
        Assert.Equal(7, back[sample]);
    }

    /// <summary>One usable value of a key type, or null where this test cannot make one.</summary>
    private static object? Sample(Type key)
    {
        if (key == typeof(Bytex.Core.Model.Identifiers.InstrumentId))
        {
            return Bytex.Core.Model.Identifiers.InstrumentId.Parse("BTCUSDT-PERP.BYBIT");
        }

        if (key == typeof(Bytex.Core.Model.Primitives.Currency))
        {
            return Bytex.Core.Model.Primitives.Currency.FromCode("USDT");
        }

        if (key == typeof(Bytex.Core.Model.Data.BarType))
        {
            return Bytex.Core.Model.Data.BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        }

        if (key == typeof(string))
        {
            return "k";
        }

        // Every identifier that wraps a single string: the factory converter covers them, so the sample is free.
        if (key.IsValueType && key.GetConstructor([typeof(string)]) is { } ctor && key.GetProperty("Value")?.PropertyType == typeof(string))
        {
            return ctor.Invoke(["X-1"]);
        }

        return key.IsEnum ? Enum.GetValues(key).GetValue(0) : null;
    }
}
