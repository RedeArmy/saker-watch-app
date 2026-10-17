using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;

namespace SakerWatch.MarketData.Tests;

public sealed class FollowUpJobsTests
{
    private sealed class Enqueuer : IJobEnqueuer
    {
        public List<(string Key, object Payload, JobEnqueueOptions? Options)> Queued { get; } = [];

        public Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
            where TPayload : notnull
        {
            Queued.Add((jobKey, payload, options));
            return Task.FromResult(true);
        }
    }

    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static (FollowUpJobs Waiter, Enqueuer Jobs) Build(IngestionOptions? options = null)
    {
        var jobs = new Enqueuer();
        return (new FollowUpJobs(jobs, Options.Create(options ?? new IngestionOptions()), new FakeTimeProvider(_now)), jobs);
    }

    [Theory]
    [InlineData("marketdata.backfill:abc", 1, "marketdata.backfill:abc:w1")]
    [InlineData("marketdata.backfill:abc:w1", 2, "marketdata.backfill:abc:w2")]
    [InlineData("marketdata.refresh:abc:20261016:w41", 42, "marketdata.refresh:abc:20261016:w42")]
    public void ContinuationKey_ReplacesTheCounterInsteadOfStackingIt(string current, int waits, string expected) =>
        FollowUpJobs.ContinuationKey(current, waits).Should().Be(expected);

    [Fact]
    public void ContinuationKey_WithoutAKey_Throws()
    {
        Action act = () => FollowUpJobs.ContinuationKey(" ", 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task TryWait_WaitsWhatTheProviderAskedPlusASpreadOfAtMostTheJitter()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = Build();

        bool waited = await waiter.TryWaitAsync("p", 0, "k", ProviderErrors.RateLimited("fake", TimeSpan.FromSeconds(20)), 8, CancellationToken.None);

        waited.Should().BeTrue();
        (string key, object payload, JobEnqueueOptions? options) = jobs.Queued.Should().ContainSingle().Subject;
        key.Should().Be("k:w1");
        payload.Should().Be("p");
        options!.MaxAttempts.Should().Be(8);
        options.AvailableAt.Should().BeOnOrAfter(_now.AddSeconds(20)).And.BeOnOrBefore(_now.AddSeconds(20 + 30));
    }

    [Fact]
    public async Task TryWait_WithoutAHintFromTheProvider_WaitsAMinute()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = Build(new IngestionOptions { RateLimitJitterSeconds = 0 });

        await waiter.TryWaitAsync("p", 0, "k", ProviderErrors.RateLimited("fake"), 8, CancellationToken.None);

        jobs.Queued.Single().Options!.AvailableAt.Should().Be(_now.AddSeconds(60));
    }

    [Fact]
    public async Task TryWait_TheSameJobAlwaysWaitsTheSame()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = Build();

        await waiter.TryWaitAsync("p", 0, "k", ProviderErrors.RateLimited("fake"), 8, CancellationToken.None);
        await waiter.TryWaitAsync("p", 0, "k", ProviderErrors.RateLimited("fake"), 8, CancellationToken.None);

        jobs.Queued[0].Options!.AvailableAt.Should().Be(jobs.Queued[1].Options!.AvailableAt);
    }

    [Fact]
    public async Task TryWait_AfterTheAllowedWaits_QueuesNothingAndSaysSo()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = Build(new IngestionOptions { MaxRateLimitWaits = 3 });

        bool waited = await waiter.TryWaitAsync("p", 3, "k", ProviderErrors.RateLimited("fake"), 8, CancellationToken.None);

        waited.Should().BeFalse();
        jobs.Queued.Should().BeEmpty();
    }
}
