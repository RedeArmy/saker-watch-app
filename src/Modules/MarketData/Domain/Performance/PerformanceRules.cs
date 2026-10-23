using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Domain.Performance;

/// <summary>
/// A fingerprint of what the performance calculation does, computed from the calculation itself (ADR-0047). The performance cache keeps reports under
/// a key that holds it, so a change of the rules (the target dates, the gap rule, how a change is computed, a calendar's previous trading day) makes new
/// keys by itself and no one has to remember to raise <see cref="PerformanceCalculator.Version"/>: the fixed inputs below are run through the calculator
/// and the text of the reports is hashed. What it cannot see are the rules that live outside the calculator (how the last close on or before a date is
/// found in the database); a change there still raises <see cref="PerformanceCalculator.Version"/>.
/// </summary>
public static class PerformanceRules
{
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));

    /// <summary>The first eight hex digits of the hash of <see cref="Describe"/>.</summary>
    public static string Fingerprint { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Describe())))[..8];

    /// <summary>The segment of a cache key that stands for the rules: <c>c{version}.{fingerprint}</c>, letters, digits and dots only.</summary>
    public static string CacheTag { get; } = $"c{PerformanceCalculator.Version.ToString(CultureInfo.InvariantCulture)}.{Fingerprint}";

    /// <summary>
    /// The reports of the fixed inputs, one line each: a week with a holiday and the first trading days of a year (target dates), baselines a few days
    /// early, one that is missing and one that is eleven days early (the gap rule), a market without a calendar, and a listing with no price.
    /// </summary>
    public static string Describe() => string.Join(
        "\n",
        Scenario(new DateOnly(2026, 11, 27), _calendar, PerformanceCalculator.DefaultMaxGapDays, _ => 0),
        Scenario(new DateOnly(2027, 1, 4), _calendar, PerformanceCalculator.DefaultMaxGapDays, _ => 0),
        Scenario(new DateOnly(2026, 10, 16), _calendar, PerformanceCalculator.DefaultMaxGapDays, window => window == PerformanceWindow.FiveYears ? null : 3),
        Scenario(new DateOnly(2026, 10, 16), calendar: null, PerformanceCalculator.DefaultMaxGapDays, _ => 11),
        Scenario(new DateOnly(2026, 10, 16), _calendar, maxGapDays: 20, _ => 11),
        Text(PerformanceCalculator.Calculate(null, new Dictionary<DateOnly, PriceSample?>(), _calendar)));

    // The baseline of each window is its target date or the given number of days before it, with a close that makes the change exact.
    private static string Scenario(DateOnly latestDate, IExchangeCalendar? calendar, int maxGapDays, Func<PerformanceWindow, int?> daysBefore)
    {
        PriceSample latest = PriceSample.Create(latestDate, 120m).Value;
        var baselines = new Dictionary<DateOnly, PriceSample?>();
        foreach ((PerformanceWindow window, DateOnly target) in PerformanceCalculator.TargetDates(latestDate, calendar))
        {
            baselines[target] = daysBefore(window) is { } days ? PriceSample.Create(target.AddDays(-days), 100m).Value : null;
        }

        return Text(PerformanceCalculator.Calculate(latest, baselines, calendar, maxGapDays));
    }

    private static string Text(PerformanceReport report) => string.Join(
        " ",
        report.Windows.Select(window =>
            $"{window.Window}={(window.Change is { } change ? change.Fraction.ToString(CultureInfo.InvariantCulture) : window.Gap!.ToString())}@{window.Baseline?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
}
