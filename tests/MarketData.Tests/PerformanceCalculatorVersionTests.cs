using AwesomeAssertions;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Performance;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// Pins what <see cref="PerformanceCalculator.Version"/> stands for. The performance cache keeps reports under a key that holds that version (ADR-0041,
/// ADR-0047); a change of the rules that does not raise it would leave yesterday's numbers served as today's for up to the life of the keys. These
/// fixtures cover the target dates around a holiday and a weekend, the gap rule, a missing baseline and a market without a calendar.
/// </summary>
public sealed class PerformanceCalculatorVersionTests
{
    private const string _instruction = "The calculation of the performance windows changed. If the change is intended, raise PerformanceCalculator.Version (it is part of the cache keys, "
        + "so reports made by the old rules are not served) and replace the expected text below with the new fingerprint.";

    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));

    private static PriceSample Sample(DateOnly date, decimal close) => PriceSample.Create(date, close).Value;

    // The baseline of each window is the target date itself, or the given number of days before it, with a close that makes the change exact.
    private static string Fingerprint(DateOnly latestDate, IExchangeCalendar? calendar, int maxGapDays, Func<PerformanceWindow, int?> daysBefore)
    {
        PriceSample latest = Sample(latestDate, 120m);
        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = PerformanceCalculator.TargetDates(latestDate, calendar);
        var baselines = new Dictionary<DateOnly, PriceSample?>();
        foreach ((PerformanceWindow window, DateOnly target) in targets)
        {
            baselines[target] = daysBefore(window) is { } days ? Sample(target.AddDays(-days), 100m) : null;
        }

        PerformanceReport report = PerformanceCalculator.Calculate(latest, baselines, calendar, maxGapDays);
        return string.Join(" ", report.Windows.Select(window =>
            $"{window.Window}={(window.Change is { } change ? change.Fraction.ToString(System.Globalization.CultureInfo.InvariantCulture) : window.Gap!.ToString())}"
            + $"@{window.Baseline?.Date:yyyy-MM-dd}"));
    }

    [Fact]
    public void TheRulesTheVersionStandsFor_GiveTheSameReportsAsWhenItWasSet()
    {
        // 2026-11-27 is the Friday after Thanksgiving: the previous trading day is Wednesday the 25th, and the last day of 2025 was a Wednesday.
        string holiday = Fingerprint(new DateOnly(2026, 11, 27), _calendar, PerformanceCalculator.DefaultMaxGapDays, _ => 0);
        // Every baseline three days early (inside the gap rule except for the one-day window, which allows none), and one window with nothing stored.
        string early = Fingerprint(new DateOnly(2026, 10, 16), _calendar, PerformanceCalculator.DefaultMaxGapDays, window => window == PerformanceWindow.FiveYears ? null : 3);
        // Baselines eleven days early, one more than the default gap, and a market with no calendar.
        string tooEarly = Fingerprint(new DateOnly(2026, 10, 16), calendar: null, PerformanceCalculator.DefaultMaxGapDays, _ => 11);

        string fingerprint = string.Join("\n", holiday, early, tooEarly);

        fingerprint.Should().Be(ExpectedFingerprint, _instruction);
    }

    [Fact]
    public void TheVersion_IsAPositiveNumber() => PerformanceCalculator.Version.Should().BePositive();

    private const string ExpectedFingerprint =
        "OneDay=0.2@2026-11-25 OneWeek=0.2@2026-11-20 OneMonth=0.2@2026-10-27 ThreeMonths=0.2@2026-08-27 SixMonths=0.2@2026-05-27 YearToDate=0.2@2025-12-31 OneYear=0.2@2025-11-27 FiveYears=0.2@2021-11-27" +
        "\n" +
        "OneDay=HistoryGap@ OneWeek=0.2@2026-10-06 OneMonth=0.2@2026-09-13 ThreeMonths=0.2@2026-07-13 SixMonths=0.2@2026-04-13 YearToDate=0.2@2025-12-28 OneYear=0.2@2025-10-13 FiveYears=InsufficientHistory@" +
        "\n" +
        "OneDay=CalendarUnavailable@ OneWeek=HistoryGap@ OneMonth=HistoryGap@ ThreeMonths=HistoryGap@ SixMonths=HistoryGap@ YearToDate=HistoryGap@ OneYear=HistoryGap@ FiveYears=HistoryGap@";
}
