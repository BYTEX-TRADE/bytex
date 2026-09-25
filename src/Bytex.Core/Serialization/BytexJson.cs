using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Serialization;

/// <summary>
/// JSON options with converters for engine value types and identifiers.
/// </summary>
public static class BytexJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create(bool indented = false)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        options.Converters.Add(new PriceConverter());
        options.Converters.Add(new QuantityConverter());
        options.Converters.Add(new MoneyConverter());
        options.Converters.Add(new CurrencyConverter());
        options.Converters.Add(new UnixNanosConverter());
        options.Converters.Add(new BarTypeConverter());
        options.Converters.Add(new BarSpecificationConverter());
        options.Converters.Add(new InstrumentIdConverter());
        options.Converters.Add(new StringIdentifierConverterFactory());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private sealed class PriceConverter : JsonConverter<Price>
    {
        public override Price Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number ? Price.FromDecimal(reader.GetDecimal()) : Price.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Price value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class QuantityConverter : JsonConverter<Quantity>
    {
        public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number ? Quantity.FromDecimal(reader.GetDecimal()) : Quantity.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Money.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class CurrencyConverter : JsonConverter<Currency>
    {
        public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Currency.FromCode(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) => writer.WriteStringValue(value.Code);

        public override Currency ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Currency.FromCode(reader.GetString()!);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.Code);
    }

    private sealed class UnixNanosConverter : JsonConverter<UnixNanos>
    {
        public override UnixNanos Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number ? new UnixNanos(reader.GetInt64()) : UnixNanos.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, UnixNanos value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }

    private sealed class BarTypeConverter : JsonConverter<BarType>
    {
        public override BarType ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            BarType.Parse(reader.GetString()!);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, BarType value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.ToString());

        public override BarType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => BarType.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, BarType value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class BarSpecificationConverter : JsonConverter<BarSpecification>
    {
        public override BarSpecification Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => BarSpecification.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, BarSpecification value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class InstrumentIdConverter : JsonConverter<InstrumentId>
    {
        public override InstrumentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => InstrumentId.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, InstrumentId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);

        public override InstrumentId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            InstrumentId.Parse(reader.GetString()!);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, InstrumentId value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.Value);
    }

    /// <summary>
    /// Handles every identifier struct that wraps a single string (constructor(string) + Value property).
    /// </summary>
    private sealed class StringIdentifierConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsValueType
            && typeToConvert.Namespace == typeof(Venue).Namespace
            && typeToConvert.GetConstructor([typeof(string)]) is not null
            && typeToConvert.GetProperty("Value")?.PropertyType == typeof(string);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(StringIdentifierConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class StringIdentifierConverter<T> : JsonConverter<T> where T : struct
    {
        private static readonly ConstructorInfo Ctor = typeof(T).GetConstructor([typeof(string)])!;
        private static readonly PropertyInfo ValueProperty = typeof(T).GetProperty("Value")!;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => (T)Ctor.Invoke([reader.GetString()!]);

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue((string)ValueProperty.GetValue(value)!);

        // Every one of these wraps a single string, so every one of them is a usable property name as well as a
        // usable value. A configuration keyed by a strategy or a venue would otherwise fail the same way an
        // instrument-keyed one did.
        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (T)Ctor.Invoke([reader.GetString()!]);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WritePropertyName((string)ValueProperty.GetValue(value)!);
    }

    public static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
