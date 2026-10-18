using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Web.Json;

namespace SakerWatch.Web.Tests;

public sealed class DecimalJsonTests
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { Converters = { new DecimalAsStringConverter() } };

    private sealed record Money(decimal Amount, decimal? Optional);

    [Theory]
    [InlineData("180.5", "180.5")]
    [InlineData("180.50000000", "180.5")]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("100", "100")]
    [InlineData("-7.64572", "-7.64572")]
    [InlineData("0", "0")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void ADecimal_IsWrittenAsAPlainString(string value, string written)
    {
        string json = JsonSerializer.Serialize(new Money(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), null), _options);

        json.Should().Be($"{{\"amount\":\"{written}\",\"optional\":null}}");
    }

    [Fact]
    public void ADecimalString_IsReadBack_ExactlyAndWithADotWhateverTheCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-GT");
        try
        {
            Money? money = JsonSerializer.Deserialize<Money>("{\"amount\":\"1234.56789012\",\"optional\":\"0.1\"}", _options);

            money.Should().Be(new Money(1234.56789012m, 0.1m));
            JsonSerializer.Serialize(new Money(0.1m, null), _options).Should().Contain("\"0.1\"");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("{\"amount\": 12.5}")]
    [InlineData("{\"amount\": \"1e3\"}")]
    [InlineData("{\"amount\": \"1,5\"}")]
    [InlineData("{\"amount\": \"abc\"}")]
    [InlineData("{\"amount\": \"\"}")]
    [InlineData("{\"amount\": null}")]
    [InlineData("{\"amount\": true}")]
    public void ANumberOrAnythingThatIsNotAPlainDecimalString_IsRefused(string json)
    {
        Action act = () => JsonSerializer.Deserialize<Money>(json, _options);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ANullableDecimal_ReadsNullAndAString()
    {
        JsonSerializer.Deserialize<Money>("{\"amount\":\"1\",\"optional\":null}", _options)!.Optional.Should().BeNull();
        JsonSerializer.Deserialize<Money>("{\"amount\":\"1\"}", _options)!.Optional.Should().BeNull();
    }
}
