using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

public sealed class MarketValueObjectTests
{
    [Theory]
    [InlineData("XNYS", "XNYS")]
    [InlineData(" xnas ", "XNAS")]
    [InlineData("x1a2", "X1A2")]
    public void Mic_Create_NormalizesAValidCode(string input, string expected) =>
        Mic.Create(input).Value.Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("XNY")]
    [InlineData("XNYSX")]
    [InlineData("XN-S")]
    [InlineData("XÑYS")]
    public void Mic_Create_RefusesAnythingButFourLettersOrDigits(string? input) =>
        Mic.Create(input).Error!.Code.Should().Be("marketdata.mic.invalid");

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" JPY ", "JPY")]
    public void CurrencyCode_Create_NormalizesAValidCode(string input, string expected) =>
        CurrencyCode.Create(input).Value.Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("U5D")]
    public void CurrencyCode_Create_RefusesAnythingButThreeLetters(string? input) =>
        CurrencyCode.Create(input).Error!.Code.Should().Be("money.currency_invalid");

    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData(" BRK.B ", "BRK.B")]
    [InlineData("7203", "7203")]
    [InlineData("AAC.UN", "AAC.UN")]
    [InlineData("RDS-A", "RDS-A")]
    [InlineData("A_B", "A_B")]
    public void Ticker_Create_NormalizesAValidTicker(string input, string expected) =>
        Ticker.Create(input).Value.Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".A")]
    [InlineData("AA PL")]
    [InlineData("A/B")]
    [InlineData("A:B")]
    [InlineData("AAPL*")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    public void Ticker_Create_RefusesCharactersThatBreakKeysAndUrls(string? input) =>
        Ticker.Create(input).Error!.Code.Should().Be("marketdata.ticker.invalid");

    [Fact]
    public void Ticker_Create_AcceptsTheLongestAllowedTicker() =>
        Ticker.Create(new string('A', Ticker.MaxLength)).IsSuccess.Should().BeTrue();

    [Fact]
    public void ValueObjects_AreEqualByValue()
    {
        Mic.Parse("XNYS").Should().Be(Mic.Parse("xnys"));
        Ticker.Parse("aapl").Should().Be(Ticker.Parse("AAPL"));
        CurrencyCode.Parse("usd").Should().Be(CurrencyCode.Parse("USD"));
        Mic.Parse("XNYS").Should().NotBe(Mic.Parse("XNAS"));
    }

    [Fact]
    public void Parse_OfAnInvalidValue_Throws()
    {
        Action mic = () => Mic.Parse("nop");
        Action currency = () => CurrencyCode.Parse("nope");
        Action ticker = () => Ticker.Parse("no pe");

        mic.Should().Throw<ArgumentException>();
        currency.Should().Throw<ArgumentException>();
        ticker.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_ReturnsTheNormalizedValue()
    {
        Mic.Parse("xnys").ToString().Should().Be("XNYS");
        CurrencyCode.Parse("usd").ToString().Should().Be("USD");
        Ticker.Parse("aapl").ToString().Should().Be("AAPL");
    }

    [Fact]
    public void StockId_RoundTripsItsGuid()
    {
        var guid = Guid.CreateVersion7();

        var id = StockId.Create(guid);

        id.Value.Should().Be(guid);
        id.ToString().Should().Be(guid.ToString("D"));
    }
}
