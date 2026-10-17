using AwesomeAssertions;
using SakerWatch.MarketData.Domain.Prices;

namespace SakerWatch.MarketData.Tests;

public sealed class PricePointTests
{
    private static readonly DailyBar _bar = DailyBar.Create(new DateOnly(2024, 6, 7), 1204m, 1211m, 1198m, 1208.88m, 438392000).Value;

    [Fact]
    public void Create_KeepsTheRawBarTheAdjustedCloseAndTheFinality()
    {
        PricePoint point = PricePoint.Create(_bar, 120.54137m, isFinal: true).Value;

        point.Raw.Should().Be(_bar);
        point.AdjustedClose.Should().Be(120.54137m);
        point.IsFinal.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RefusesAnAdjustedCloseThatIsNotPositive(double adjusted) =>
        PricePoint.Create(_bar, (decimal)adjusted, true).Error!.Code.Should().Be("marketdata.price.non_positive_adjusted_close");

    [Fact]
    public void Create_RefusesANullBar()
    {
        Func<object> act = () => PricePoint.Create(null!, 1m, true);

        act.Should().Throw<ArgumentNullException>();
    }
}
