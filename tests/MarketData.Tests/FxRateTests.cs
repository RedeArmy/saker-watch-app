using AwesomeAssertions;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

public sealed class FxRateTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly DateOnly _day = new(2026, 10, 16);

    [Fact]
    public void Create_WithValidParts_KeepsThemExactly()
    {
        var result = FxRate.Create(_usd, _gtq, _day, 7.64572m, " banguat ");

        result.IsSuccess.Should().BeTrue();
        FxRate rate = result.Value;
        (rate.Base, rate.Quote, rate.AsOfDate, rate.Rate, rate.Source).Should().Be((_usd, _gtq, _day, 7.64572m, "banguat"));
    }

    [Fact]
    public void Create_ARateWithItself_IsRefused() =>
        FxRate.Create(_usd, _usd, _day, 1m, "banguat").Error!.Code.Should().Be("marketdata.fx.same_currency");

    [Theory]
    [InlineData(0)]
    [InlineData(-7.6)]
    public void Create_ARateThatIsNotPositive_IsRefused(double value) =>
        FxRate.Create(_usd, _gtq, _day, (decimal)value, "banguat").Error!.Code.Should().Be("marketdata.fx.rate_not_positive");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Banguat")]
    [InlineData("ban guat")]
    [InlineData("a234567890123456789012345678901234")]
    public void Create_AMalformedSource_IsRefused(string? source) =>
        FxRate.Create(_usd, _gtq, _day, 7.6m, source).Error!.Code.Should().Be("marketdata.fx.source_invalid");

    [Fact]
    public void Create_TwoRatesWithTheSameParts_AreEqual() =>
        FxRate.Create(_usd, _gtq, _day, 7.6m, "banguat").Value.Should().Be(FxRate.Create(_usd, _gtq, _day, 7.6m, "banguat").Value);
}
