using AwesomeAssertions;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// The fingerprint of the performance rules (ADR-0047) makes the cache keys change by themselves when the calculation does. It only does that if the
/// fixed inputs it runs reach every rule: these tests check that they do, and that the fingerprint is the same every time it is asked.
/// </summary>
public sealed class PerformanceRulesTests
{
    [Fact]
    public void TheFingerprint_IsStable_AndEightHexDigits()
    {
        PerformanceRules.Describe().Should().Be(PerformanceRules.Describe());
        PerformanceRules.Fingerprint.Should().MatchRegex("^[0-9a-f]{8}$");
        PerformanceRules.CacheTag.Should().Be($"c{PerformanceCalculator.Version}.{PerformanceRules.Fingerprint}");
        PerformanceRules.CacheTag.Should().MatchRegex("^[A-Za-z0-9.]+$", "a cache key segment is letters, digits and dots");
    }

    [Fact]
    public void TheFixedInputs_ReachEveryWindow_EveryReasonForNoChange_AndAChange()
    {
        string described = PerformanceRules.Describe();

        foreach (PerformanceWindow window in PerformanceCalculator.Windows)
        {
            described.Should().Contain($"{window}=", "every window is part of the fingerprint");
        }

        foreach (PerformanceGap gap in Enum.GetValues<PerformanceGap>())
        {
            described.Should().Contain($"={gap}@", $"the reason {gap} is one the fingerprint depends on");
        }

        described.Should().MatchRegex(@"=0\.2@\d{4}-\d{2}-\d{2}", "at least one window has a change, so how a change is computed is part of it");
    }

    [Fact]
    public void TheFixedInputs_ReachTheTargetDatesAroundAHolidayAndTheNewYear()
    {
        string described = PerformanceRules.Describe();

        described.Should().Contain("OneDay=0.2@2026-11-25", "the day after Thanksgiving looks back to the Wednesday");
        described.Should().Contain("YearToDate=0.2@2026-12-31", "the first trading day of 2027 starts the year from the last day of 2026");
    }

    [Fact]
    public void TheKeyTag_HoldsTheVersion_TheRulesAndTheStatementsThatReadTheCloses()
    {
        PerformanceReader.QueriesFingerprint.Should().MatchRegex("^[0-9a-f]{8}$");

        RedisPerformanceCache.KeyTag.Should().Be($"{PerformanceRules.CacheTag}.{PerformanceReader.QueriesFingerprint}");
        RedisPerformanceCache.KeyTag.Should().MatchRegex("^c[0-9]+\\.[0-9a-f]{8}\\.[0-9a-f]{8}$");
    }
}
