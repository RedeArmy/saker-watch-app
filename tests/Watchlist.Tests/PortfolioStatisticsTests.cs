using AwesomeAssertions;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Tests;

public sealed class PortfolioStatisticsTests
{
    private static StockChanges Stock(string label, params (string Window, decimal? Change)[] changes) =>
        new(label, changes.ToDictionary(c => c.Window, c => c.Change));

    private static WindowStatistics Of(IReadOnlyList<WindowStatistics> all, string window) => all.Single(s => s.Window == window);

    [Fact]
    public void TheAverageAndTheMedian_AreOverTheStocksThatHaveANumber_AndNobodyCountsAsZero()
    {
        // 1D: +10 %, -5 %, +2 % and one without data. Hand-computed: (0.10 - 0.05 + 0.02) / 3 and the middle of [-0.05, 0.02, 0.10].
        IReadOnlyList<WindowStatistics> all = PortfolioStatistics.Aggregate(
        [
            Stock("XNAS:AAA", ("1D", 0.10m)),
            Stock("XNAS:BBB", ("1D", -0.05m)),
            Stock("XNAS:CCC", ("1D", 0.02m)),
            Stock("XNAS:DDD", ("1D", null)),
        ]);

        WindowStatistics day = Of(all, "1D");

        day.Count.Should().Be(3);
        day.Average.Should().Be(0.07m / 3m);
        day.Median.Should().Be(0.02m);
        day.Best.Should().Be(new WindowExtreme("XNAS:AAA", 0.10m));
        day.Worst.Should().Be(new WindowExtreme("XNAS:BBB", -0.05m));
    }

    [Fact]
    public void WithAnEvenCount_TheMedianIsTheMeanOfTheTwoMiddleValues()
    {
        IReadOnlyList<WindowStatistics> all = PortfolioStatistics.Aggregate(
        [
            Stock("A", ("1W", 0.01m)), Stock("B", ("1W", 0.03m)), Stock("C", ("1W", 0.40m)), Stock("D", ("1W", -0.20m)),
        ]);

        // Sorted: -0.20, 0.01, 0.03, 0.40: the middle two are 0.01 and 0.03.
        (Of(all, "1W").Median, Of(all, "1W").Average).Should().Be((0.02m, 0.06m));
    }

    [Fact]
    public void OneStock_IsItsOwnAverageMedianBestAndWorst()
    {
        WindowStatistics ytd = Of(PortfolioStatistics.Aggregate([Stock("ONLY", ("YTD", 0.375m))]), "YTD");

        (ytd.Count, ytd.Average, ytd.Median, ytd.Best, ytd.Worst).Should().Be((1, 0.375m, 0.375m, new WindowExtreme("ONLY", 0.375m), new WindowExtreme("ONLY", 0.375m)));
    }

    [Fact]
    public void WhenNoStockHasData_TheWindowSaysSo_WithZeroAndNoNumbers()
    {
        IReadOnlyList<WindowStatistics> none = PortfolioStatistics.Aggregate([]);
        IReadOnlyList<WindowStatistics> missing = PortfolioStatistics.Aggregate([Stock("X", ("1D", null))]);

        none.Select(s => s.Window).Should().Equal(PortfolioStatistics.Windows).And.HaveCount(8);
        none.Concat(missing).Should().OnlyContain(s => s.Count == 0 && s.Average == null && s.Median == null && s.Best == null && s.Worst == null);
    }

    [Fact]
    public void TiesGoToTheLowestLabel_ForTheBestAndForTheWorst_WhateverTheInputOrder()
    {
        StockChanges[] stocks =
        [
            Stock("XNYS:KO", ("1M", 0.05m)), Stock("XNAS:AAPL", ("1M", 0.05m)), Stock("XNAS:MSFT", ("1M", -0.01m)), Stock("XNAS:NVDA", ("1M", -0.01m)),
        ];

        WindowStatistics forward = Of(PortfolioStatistics.Aggregate(stocks), "1M");
        WindowStatistics backward = Of(PortfolioStatistics.Aggregate([.. stocks.Reverse()]), "1M");

        forward.Best!.Stock.Should().Be("XNAS:AAPL");
        forward.Worst!.Stock.Should().Be("XNAS:MSFT");
        backward.Should().Be(forward);
    }

    [Fact]
    public void AWindowTheStockDoesNotMention_CountsAsNoData()
    {
        IReadOnlyList<WindowStatistics> all = PortfolioStatistics.Aggregate([Stock("A", ("1D", 0.1m)), Stock("B")]);

        (Of(all, "1D").Count, Of(all, "5Y").Count).Should().Be((1, 0));
    }

    [Fact]
    public void TheArithmetic_IsExactDecimal_NotFloatingPoint()
    {
        // 0.1 + 0.2 is exactly 0.3 in decimal; in binary floating point it is not.
        WindowStatistics day = Of(PortfolioStatistics.Aggregate([Stock("A", ("1D", 0.1m)), Stock("B", ("1D", 0.2m))]), "1D");

        (day.Average, day.Median).Should().Be((0.15m, 0.15m));
        (0.1m + 0.2m).Should().Be(0.3m);
    }

    [Fact]
    public void ForAnySetOfStocks_TheFiguresAreConsistent_AndDoNotDependOnTheOrder()
    {
        var random = new Random(20261018);
        for (int round = 0; round < 300; round++)
        {
            int size = random.Next(1, 25);
            StockChanges[] stocks =
            [
                .. Enumerable.Range(0, size).Select(i => Stock($"M:T{i:D2}", ("3M", random.Next(5) == 0 ? null : Math.Round((decimal)(random.NextDouble() * 2 - 1), 6)))),
            ];
            decimal[] values = [.. stocks.Select(s => s.Changes["3M"]).Where(c => c.HasValue).Select(c => c!.Value)];

            WindowStatistics stat = Of(PortfolioStatistics.Aggregate(stocks), "3M");
            WindowStatistics shuffled = Of(PortfolioStatistics.Aggregate([.. stocks.OrderBy(_ => random.Next())]), "3M");

            stat.Count.Should().Be(values.Length);
            shuffled.Should().Be(stat);
            if (values.Length == 0)
            {
                stat.Average.Should().BeNull();
                continue;
            }

            stat.Best!.Change.Should().Be(values.Max());
            stat.Worst!.Change.Should().Be(values.Min());
            stat.Average.Should().BeInRange(values.Min(), values.Max());
            stat.Median.Should().BeInRange(values.Min(), values.Max());
            stat.Average.Should().BeApproximately(values.Sum() / values.Length, 0.0000000000000000000000001m);
            values.Count(v => v < stat.Median).Should().BeLessThanOrEqualTo(values.Length / 2);
            values.Count(v => v > stat.Median).Should().BeLessThanOrEqualTo(values.Length / 2);
        }
    }

    [Fact]
    public void TheWindowsAreAlwaysTheEightOfAdr0030_InOrder() =>
        PortfolioStatistics.Windows.Should().Equal("1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y");
}
