using AwesomeAssertions;
using SakerWatch.MarketData.Domain.Prices;

namespace SakerWatch.MarketData.Tests;

public sealed class DailyBarTests
{
    private static readonly DateOnly _day = new(2024, 6, 3);

    [Fact]
    public void Create_WithAConsistentDay_KeepsEveryValue()
    {
        DailyBar bar = DailyBar.Create(_day, 113.29521m, 114.67025m, 111.68185m, 114.67025m, 438392000).Value;

        bar.Date.Should().Be(_day);
        bar.Open.Should().Be(113.29521m);
        bar.High.Should().Be(114.67025m);
        bar.Low.Should().Be(111.68185m);
        bar.Close.Should().Be(114.67025m);
        bar.Volume.Should().Be(438392000);
    }

    [Fact]
    public void Create_AFlatDay_IsValid() =>
        DailyBar.Create(_day, 10m, 10m, 10m, 10m, 0).IsSuccess.Should().BeTrue("a halted or illiquid stock can open, high, low and close at one price with no volume");

    [Theory]
    [InlineData(0, 10, 9, 10)]
    [InlineData(10, 0, 9, 10)]
    [InlineData(10, 10, 0, 10)]
    [InlineData(10, 10, 9, 0)]
    [InlineData(-1, 10, 9, 10)]
    public void Create_WithAPriceThatIsNotPositive_IsRefused(double open, double high, double low, double close) =>
        DailyBar.Create(_day, (decimal)open, (decimal)high, (decimal)low, (decimal)close, 1).Error!.Code.Should().Be("marketdata.bar.non_positive_price");

    [Theory]
    [InlineData(10, 9, 8, 9)] // the high is below the open
    [InlineData(10, 11, 8, 12)] // the high is below the close
    [InlineData(10, 11, 12, 10)] // the low is above the high
    [InlineData(8, 11, 9, 10)] // the low is above the open
    [InlineData(10, 11, 9.5, 9)] // the low is above the close
    public void Create_WithAnInconsistentRange_IsRefused(double open, double high, double low, double close) =>
        DailyBar.Create(_day, (decimal)open, (decimal)high, (decimal)low, (decimal)close, 1).Error!.Code.Should().Be("marketdata.bar.inconsistent_range");

    [Fact]
    public void Create_WithANegativeVolume_IsRefused() =>
        DailyBar.Create(_day, 10m, 11m, 9m, 10m, -1).Error!.Code.Should().Be("marketdata.bar.negative_volume");

    [Fact]
    public void TwoBarsWithTheSameValues_AreEqual() =>
        DailyBar.Create(_day, 10m, 11m, 9m, 10m, 5).Value.Should().Be(DailyBar.Create(_day, 10m, 11m, 9m, 10m, 5).Value);
}
