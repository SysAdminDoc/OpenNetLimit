using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenNetLimit.Core;

/// <summary>
/// Reads an enum from its name (any case) or its number and writes it as a number, so
/// "action": "Limit" and "action": 0 both work while responses keep the numeric form
/// existing clients already read.
/// </summary>
public sealed class EnumNameOrNumberConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return (T)Enum.ToObject(typeof(T), reader.GetInt64());

            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var value) &&
                    Enum.IsDefined(value))
                    return value;
                throw new JsonException(
                    $"'{text}' is not a valid {typeof(T).Name}. Use one of: {string.Join(", ", Enum.GetNames<T>())}.");
            }

            throw new JsonException($"Expected a name or number for {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Convert.ToInt64(value));
    }
}
