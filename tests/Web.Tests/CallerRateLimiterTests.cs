using System.Diagnostics;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Web.RateLimiting;

namespace SakerWatch.Web.Tests;

/// <summary>The limiter of one caller (ADR-0048) counting in memory, on a clock the test moves: no waiting for a window to pass.</summary>
public sealed class CallerRateLimiterTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero); // a window of 60 s starts at :00

    private static CallerRateLimiter Limiter(FakeTimeProvider clock, int limit = 10) =>
        new(redis: null, countInRedis: () => false, new LimiterPolicy("global", limit, TimeSpan.FromSeconds(60)), partitionKey: "user:ana", clock);

    // A guard that the answer comes at once: the built-in fixed window limiter, asked for zero permits on a full window, does not answer until the next one.
    private static Task<RateLimitLease> AskAsync(RateLimiter limiter, int permits) => Task.Run(async () => await limiter.AcquireAsync(permits)).WaitAsync(TimeSpan.FromSeconds(5));

    private static bool Take(RateLimiter limiter, int permits = 1) => limiter.AttemptAcquire(permits).IsAcquired;

    [Fact]
    public void LetsTheLimitThrough_ThenDenies_WithAWaitInsideTheWindow()
    {
        var clock = new FakeTimeProvider(_start.AddSeconds(10));
        using CallerRateLimiter limiter = Limiter(clock, limit: 3);

        bool[] results = [Take(limiter), Take(limiter), Take(limiter), Take(limiter)];
        RateLimitLease denied = limiter.AttemptAcquire(1);

        results.Should().Equal(true, true, true, false);
        denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromSeconds(50), "the window ends in fifty seconds");
    }

    [Fact]
    public void ABurstAtTheEndOfAWindow_DoesNotGetAFreshLimitAtTheStartOfTheNext()
    {
        var clock = new FakeTimeProvider(_start.AddSeconds(59));
        using CallerRateLimiter limiter = Limiter(clock, limit: 10);
        for (int i = 0; i < 10; i++)
        {
            Take(limiter).Should().BeTrue();
        }

        clock.Advance(TimeSpan.FromSeconds(2)); // the next window began a second ago
        bool oneSecondInto = Take(limiter);
        clock.Advance(TimeSpan.FromSeconds(29)); // half of the old window still counts: five permits are left
        bool[] later = [.. Enumerable.Range(0, 6).Select(_ => Take(limiter))];

        oneSecondInto.Should().BeFalse("with a fixed window the whole limit would be back here");
        later.Should().Equal(true, true, true, true, true, false);
    }

    [Fact]
    public void TheAllowanceIsBackOnceAWholeWindowHasPassedWithNothingCounted()
    {
        var clock = new FakeTimeProvider(_start);
        using CallerRateLimiter limiter = Limiter(clock, limit: 2);
        Take(limiter);
        Take(limiter);
        Take(limiter).Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(121));

        Take(limiter).Should().BeTrue();
    }

    [Fact]
    public async Task AskingWithZeroPermits_AnswersAtOnce_YesWhileThereIsRoom_NoWhenThereIsNone_AndTakesNothing()
    {
        var clock = new FakeTimeProvider(_start.AddSeconds(5));
        using CallerRateLimiter limiter = Limiter(clock, limit: 2);
        var timer = Stopwatch.StartNew();

        bool fresh = (await AskAsync(limiter, 0)).IsAcquired;
        bool askedAgain = (await AskAsync(limiter, 0)).IsAcquired;
        Take(limiter);
        Take(limiter);
        // The built-in fixed window limiter, asked for zero permits here, waits for the next window. This one says no, now.
        bool full = (await AskAsync(limiter, 0)).IsAcquired;

        (fresh, askedAgain, full).Should().Be((true, true, false));
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireAsync_InMemory_NeverWaits_WhateverIsAsked()
    {
        var clock = new FakeTimeProvider(_start);
        using CallerRateLimiter limiter = Limiter(clock, limit: 1);

        RateLimitLease first = await AskAsync(limiter, 1);
        RateLimitLease second = await AskAsync(limiter, 1);

        (first.IsAcquired, second.IsAcquired).Should().Be((true, false));
    }

    [Fact]
    public void TheLimiter_SaysHowLongItHasBeenIdle_SoThePartitionCanBeDropped_AndCountsItsLeases()
    {
        var clock = new FakeTimeProvider(_start);
        using CallerRateLimiter limiter = Limiter(clock, limit: 1);
        Take(limiter);
        Take(limiter);

        clock.Advance(TimeSpan.FromMinutes(3));
        TimeSpan? idle = limiter.IdleDuration;
        RateLimiterStatistics statistics = limiter.GetStatistics()!;
        Take(limiter);

        idle.Should().Be(TimeSpan.FromMinutes(3));
        limiter.IdleDuration.Should().Be(TimeSpan.Zero);
        (statistics.TotalSuccessfulLeases, statistics.TotalFailedLeases, statistics.CurrentAvailablePermits).Should().Be((1L, 1L, 1L));
    }

    [Fact]
    public async Task ARedisThatIsNotThere_MeansMemory_WhateverTheCallbackSays()
    {
        var clock = new FakeTimeProvider(_start);
        using var limiter = new CallerRateLimiter(null, () => true, new LimiterPolicy("global", 1, TimeSpan.FromSeconds(60)), "user:ana", clock);

        RateLimitLease first = await limiter.AcquireAsync(1);
        RateLimitLease second = await limiter.AcquireAsync(1);

        (first.IsAcquired, second.IsAcquired).Should().Be((true, false));
    }
}
