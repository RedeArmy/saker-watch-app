using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SakerWatch.BuildingBlocks.Web.Json;

/// <summary>
/// Reads and writes every <see cref="decimal"/> of the API as a JSON string (<c>"180.5"</c>), as the API guidelines require: a generated client then
/// never parses money, prices or rates through a floating point number. A JSON number in a request is refused, not rounded; the text written
/// has no trailing zeros, no exponent and always uses a dot.
/// </summary>
public sealed class DecimalAsStringConverter : JsonConverter<decimal>
{
    /// <inheritdoc />
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A decimal is written as a JSON string, for example \"12.5\".");
        }

        string? text = reader.GetString();
        if (text is null || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
        {
            throw new JsonException("The decimal is not a plain number such as \"12.5\".");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }

    /// <summary>The text of <paramref name="value"/> without trailing zeros (<c>180.50000000</c> is <c>180.5</c>).</summary>
    public static string Format(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
