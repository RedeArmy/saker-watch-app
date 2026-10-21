using AwesomeAssertions;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Prices;

namespace SakerWatch.MarketData.Tests;

/// <summary>Where a chart range starts: the baseline of the performance window of the same name (ADR-0030, ADR-0038).</summary>
public sealed class PriceRangeTests
{
    private static readonly DateOnly _friday = new(2026, 10, 16);

    [Theory]
    [InlineData("1W", 2026, 10, 9)]
    [InlineData("1M", 2026, 9, 16)]
    [InlineData("3M", 2026, 7, 16)]
    [InlineData("6M", 2026, 4, 16)]
    [InlineData("YTD", 2025, 12, 31)]
    [InlineData("1Y", 2025, 10, 16)]
    [InlineData("5Y", 2021, 10, 16)]
    public void EachRange_StartsWhereItsWindowDoes(string range, int year, int month, int day) =>
        PriceRange.StartOf(range, _friday).Should().Be(new DateOnly(year, month, day));

    [Fact]
    public void MaxHasNoStart_AndNeitherDoesACodeThatIsNotARange()
    {
        PriceRange.StartOf("MAX", _friday).Should().BeNull();
        PriceRange.StartOf("2Y", _friday).Should().BeNull();
    }

    [Fact]
    public void AMonthIsClampedToTheLastDayOfAShorterOne()
    {
        PriceRange.StartOf("1M", new DateOnly(2026, 3, 31)).Should().Be(new DateOnly(2026, 2, 28));
        PriceRange.StartOf("1Y", new DateOnly(2028, 2, 29)).Should().Be(new DateOnly(2027, 2, 28));
    }

    [Fact]
    public void EveryRangeOfTheContract_IsKnown()
    {
        foreach (string range in PriceRanges.All.Where(range => range != PriceRanges.Max))
        {
            PriceRange.StartOf(range, _friday).Should().NotBeNull(range);
        }
    }

    [Theory]
    [InlineData("1W", "day")]
    [InlineData("1M", "day")]
    [InlineData("YTD", "day")]
    [InlineData("1Y", "day")]
    [InlineData("5Y", "week")]
    [InlineData("MAX", "month")]
    public void TheAutomaticInterval_GrowsWithTheRange(string range, string interval) =>
        PriceRange.DefaultInterval(range).Should().Be(interval);
}
