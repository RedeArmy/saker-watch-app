using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Performance;

namespace SakerWatch.MarketData.Tests;

public sealed class PerformanceCalculatorTests
{
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));

    private static PriceSample Sample(int y, int m, int d, decimal close) => PriceSample.Create(new DateOnly(y, m, d), close).Value;

    private static Dictionary<DateOnly, PriceSample?> Baselines(IExchangeCalendar calendar, DateOnly latest, params (PerformanceWindow Window, PriceSample? Sample)[] given)
    {
        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = PerformanceCalculator.TargetDates(latest, calendar);
        return given.ToDictionary(g => targets[g.Window], g => g.Sample);
    }

    private static WindowPerformance Window(PerformanceReport report, PerformanceWindow window) => report.Windows.Single(w => w.Window == window);

    // Friday 2026-10-16, adjusted close 110. Every baseline is chosen so that the change is exact by hand.
    [Fact]
    public void Calculate_AFullHistory_GivesEveryWindowItsHandComputedChange()
    {
        PriceSample latest = Sample(2026, 10, 16, 110m);
        Dictionary<DateOnly, PriceSample?> baselines = Baselines(_calendar, latest.Date,
            (PerformanceWindow.OneDay, Sample(2026, 10, 15, 100m)), // +10 %
            (PerformanceWindow.OneWeek, Sample(2026, 10, 9, 110m)), // 0 %
            (PerformanceWindow.OneMonth, Sample(2026, 9, 16, 88m)), // +25 %
            (PerformanceWindow.ThreeMonths, Sample(2026, 7, 16, 200m)), // -45 %
            (PerformanceWindow.SixMonths, Sample(2026, 4, 16, 50m)), // +120 %
            (PerformanceWindow.YearToDate, Sample(2025, 12, 31, 80m)), // +37.5 %
            (PerformanceWindow.OneYear, Sample(2025, 10, 16, 160m)), // -31.25 %
            (PerformanceWindow.FiveYears, Sample(2021, 10, 15, 55m))); // +100 %: the 16th was a Saturday, so the Friday before it

        PerformanceReport report = PerformanceCalculator.Calculate(latest, baselines, _calendar);

        report.Windows.Select(w => w.Window).Should().Equal(PerformanceCalculator.Windows);
        report.Windows.Select(w => w.Change?.Fraction).Should().Equal(0.10m, 0m, 0.25m, -0.45m, 1.2m, 0.375m, -0.3125m, 1m);
        report.Windows.Should().OnlyContain(w => w.Gap == null && w.Baseline != null);
        Window(report, PerformanceWindow.FiveYears).Baseline!.Date.Should().Be(new DateOnly(2021, 10, 15));
    }

    [Fact]
    public void TargetDates_FollowTheRulesOfAdr0030()
    {
        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = PerformanceCalculator.TargetDates(new DateOnly(2026, 10, 16), _calendar);

        targets.Should().BeEquivalentTo(new Dictionary<PerformanceWindow, DateOnly>
        {
            [PerformanceWindow.OneDay] = new(2026, 10, 15),
            [PerformanceWindow.OneWeek] = new(2026, 10, 9),
            [PerformanceWindow.OneMonth] = new(2026, 9, 16),
            [PerformanceWindow.ThreeMonths] = new(2026, 7, 16),
            [PerformanceWindow.SixMonths] = new(2026, 4, 16),
            [PerformanceWindow.YearToDate] = new(2025, 12, 31),
            [PerformanceWindow.OneYear] = new(2025, 10, 16),
            [PerformanceWindow.FiveYears] = new(2021, 10, 16),
        });
    }

    [Theory]
    [InlineData(2026, 3, 31, PerformanceWindow.OneMonth, 2026, 2, 28)]
    [InlineData(2026, 5, 31, PerformanceWindow.ThreeMonths, 2026, 2, 28)]
    [InlineData(2026, 8, 31, PerformanceWindow.SixMonths, 2026, 2, 28)]
    [InlineData(2024, 2, 29, PerformanceWindow.OneYear, 2023, 2, 28)]
    [InlineData(2024, 2, 29, PerformanceWindow.FiveYears, 2019, 2, 28)]
    [InlineData(2026, 1, 2, PerformanceWindow.YearToDate, 2025, 12, 31)]
    [InlineData(2026, 12, 31, PerformanceWindow.YearToDate, 2025, 12, 31)]
    public void TargetDates_ClampToTheEndOfAShorterMonth_AndYtdIsThePreviousYearEnd(int y, int m, int d, PerformanceWindow window, int ey, int em, int ed) =>
        PerformanceCalculator.TargetDates(new DateOnly(y, m, d), _calendar)[window].Should().Be(new DateOnly(ey, em, ed));

    [Fact]
    public void TargetDates_OneDay_SkipsTheWeekendAndTheHolidays()
    {
        // Monday 2026-10-19 looks back to Friday; Tuesday 2026-01-20 looks back over the Monday holiday (Martin Luther King Jr. Day) to Friday.
        PerformanceCalculator.TargetDates(new DateOnly(2026, 10, 19), _calendar)[PerformanceWindow.OneDay].Should().Be(new DateOnly(2026, 10, 16));
        PerformanceCalculator.TargetDates(new DateOnly(2026, 1, 20), _calendar)[PerformanceWindow.OneDay].Should().Be(new DateOnly(2026, 1, 16));
    }

    [Fact]
    public void Calculate_ASeriesWithASplit_UsesTheAdjustedCloseOnly()
    {
        // A 4:1 split on 2026-10-14: the raw close fell from 400 to 100 (-75 %), the adjusted series is continuous at 100 -> 101.
        PriceSample latest = Sample(2026, 10, 16, 102m);
        Dictionary<DateOnly, PriceSample?> baselines = Baselines(_calendar, latest.Date,
            (PerformanceWindow.OneDay, Sample(2026, 10, 15, 101m)),
            (PerformanceWindow.OneWeek, Sample(2026, 10, 9, 100m)));

        PerformanceReport report = PerformanceCalculator.Calculate(latest, baselines, _calendar);

        Window(report, PerformanceWindow.OneWeek).Change!.Fraction.Should().Be(0.02m, "102 / 100 - 1, not 102 / 400 - 1");
        Window(report, PerformanceWindow.OneDay).Change!.Fraction.Should().BeApproximately(0.00990099009900990099m, 0.000000000000001m);
    }

    [Fact]
    public void Calculate_WithoutACalendar_GivesEveryWindowButTheOneDay()
    {
        PriceSample latest = Sample(2026, 10, 16, 110m);
        Dictionary<DateOnly, PriceSample?> baselines = Baselines(_calendar, latest.Date, (PerformanceWindow.OneWeek, Sample(2026, 10, 9, 100m)));

        PerformanceReport report = PerformanceCalculator.Calculate(latest, baselines, calendar: null);

        Window(report, PerformanceWindow.OneDay).Gap.Should().Be(PerformanceGap.CalendarUnavailable);
        Window(report, PerformanceWindow.OneWeek).Change!.Fraction.Should().Be(0.1m);
        report.Windows.Where(w => w.Window is not (PerformanceWindow.OneDay or PerformanceWindow.OneWeek)).Should().OnlyContain(w => w.Gap == PerformanceGap.InsufficientHistory);
        report.Windows.Should().HaveCount(8);
    }

    [Fact]
    public void TargetDates_WithoutACalendar_LeavesOutTheOneDay() =>
        PerformanceCalculator.TargetDates(new DateOnly(2026, 10, 16), calendar: null).Keys.Should().BeEquivalentTo(
            PerformanceCalculator.Windows.Where(w => w != PerformanceWindow.OneDay));

    [Fact]
    public void Calculate_AListingWithNoPrice_HasNoDataInEveryWindow()
    {
        PerformanceReport report = PerformanceCalculator.Calculate(null, new Dictionary<DateOnly, PriceSample?>(), _calendar);

        report.Latest.Should().BeNull();
        report.Windows.Should().HaveCount(8).And.OnlyContain(w => w.Gap == PerformanceGap.NoData && w.Change == null && w.Baseline == null);
    }

    [Fact]
    public void Calculate_AYoungListing_ReportsInsufficientHistoryOnlyForTheWindowsItCannotFill()
    {
        // Listed on Monday 2026-10-05: eleven days of prices. 1D and 1W have a baseline; everything longer predates the listing.
        PriceSample latest = Sample(2026, 10, 16, 21m);
        Dictionary<DateOnly, PriceSample?> baselines = Baselines(_calendar, latest.Date,
            (PerformanceWindow.OneDay, Sample(2026, 10, 15, 20m)),
            (PerformanceWindow.OneWeek, Sample(2026, 10, 9, 15m)));

        PerformanceReport report = PerformanceCalculator.Calculate(latest, baselines, _calendar);

        Window(report, PerformanceWindow.OneDay).Change!.Fraction.Should().Be(0.05m);
        Window(report, PerformanceWindow.OneWeek).Change!.Fraction.Should().Be(0.4m);
        report.Windows.Skip(2).Should().OnlyContain(w => w.Gap == PerformanceGap.InsufficientHistory && w.Change == null);
    }

    [Fact]
    public void Calculate_AListingOfTheCurrentYear_HasNoYearToDate()
    {
        PerformanceReport report = PerformanceCalculator.Calculate(
            Sample(2026, 10, 16, 50m), Baselines(_calendar, new DateOnly(2026, 10, 16), (PerformanceWindow.YearToDate, null)), _calendar);

        Window(report, PerformanceWindow.YearToDate).Gap.Should().Be(PerformanceGap.InsufficientHistory);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Calculate_ABaselineTooFarBeforeItsTarget_IsAHistoryGap(int daysBefore, bool accepted)
    {
        PriceSample latest = Sample(2026, 10, 16, 110m);
        var target = new DateOnly(2026, 9, 16);
        PriceSample baseline = PriceSample.Create(target.AddDays(-daysBefore), 100m).Value;

        PerformanceReport report = PerformanceCalculator.Calculate(
            latest, new Dictionary<DateOnly, PriceSample?> { [target] = baseline }, _calendar);

        WindowPerformance month = Window(report, PerformanceWindow.OneMonth);
        (month.Gap is null).Should().Be(accepted);
        if (!accepted)
        {
            month.Gap.Should().Be(PerformanceGap.HistoryGap);
            month.Change.Should().BeNull();
        }
    }

    [Fact]
    public void Calculate_OneDay_NeedsExactlyThePreviousSession()
    {
        // The previous session is Thursday the 15th, but the newest earlier price is Wednesday the 14th: a missing day, not a day of change.
        PriceSample latest = Sample(2026, 10, 16, 110m);

        PerformanceReport report = PerformanceCalculator.Calculate(
            latest, Baselines(_calendar, latest.Date, (PerformanceWindow.OneDay, Sample(2026, 10, 14, 100m))), _calendar);

        Window(report, PerformanceWindow.OneDay).Gap.Should().Be(PerformanceGap.HistoryGap);
    }

    [Fact]
    public void Calculate_AGapToleranceOfZero_AcceptsOnlyExactTargets()
    {
        PriceSample latest = Sample(2026, 10, 16, 110m);
        var target = new DateOnly(2026, 9, 16);
        var baselines = new Dictionary<DateOnly, PriceSample?> { [target] = Sample(2026, 9, 15, 100m) };

        PerformanceCalculator.Calculate(latest, baselines, _calendar, maxGapDays: 0).Windows.Single(w => w.Window == PerformanceWindow.OneMonth).Gap
            .Should().Be(PerformanceGap.HistoryGap);
    }

    [Fact]
    public void Calculate_ANegativeTolerance_IsRefused()
    {
        Action act = () => PerformanceCalculator.Calculate(Sample(2026, 10, 16, 1m), new Dictionary<DateOnly, PriceSample?>(), _calendar, maxGapDays: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calculate_AFallToAThird_IsMinusSixtySixPointSix()
    {
        PriceSample latest = Sample(2026, 10, 16, 1m);

        PerformanceReport report = PerformanceCalculator.Calculate(
            latest, Baselines(_calendar, latest.Date, (PerformanceWindow.OneDay, Sample(2026, 10, 15, 3m))), _calendar);

        Window(report, PerformanceWindow.OneDay).Change!.Fraction.Should().BeApproximately(-0.666666666666667m, 0.000000000000001m);
    }

    [Fact]
    public void PriceSample_WithACloseThatIsNotPositive_IsRefused()
    {
        PriceSample.Create(new DateOnly(2026, 10, 16), 0m).Error!.Code.Should().Be("marketdata.performance.non_positive_close");
        PriceSample.Create(new DateOnly(2026, 10, 16), -1m).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void WindowPerformance_IsAChangeOrAReason_NeverBoth()
    {
        PriceSample baseline = Sample(2026, 10, 15, 100m);

        var change = WindowPerformance.Of(PerformanceWindow.OneDay, RelativeChange.Between(100m, 110m).Value, baseline);
        var missing = WindowPerformance.Missing(PerformanceWindow.OneDay, PerformanceGap.HistoryGap);

        (change.Change?.Fraction, change.Gap).Should().Be((0.1m, (PerformanceGap?)null));
        (missing.Change, missing.Baseline, missing.Gap).Should().Be(((RelativeChange?)null, (PriceSample?)null, (PerformanceGap?)PerformanceGap.HistoryGap));
    }
}
