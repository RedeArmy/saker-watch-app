using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Infrastructure;

namespace SakerWatch.MarketData.Tests;

public sealed class MarketDataSchedulerTests
{
    private sealed class RecordingEnqueuer(bool fail = false) : IJobEnqueuer
    {
        public List<(string Key, object Payload)> Queued { get; } = [];

        public Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
            where TPayload : notnull
        {
            if (fail)
            {
                throw new InvalidOperationException("database not ready");
            }

            bool created = Queued.TrueForAll(q => q.Key != jobKey);
            if (created)
            {
                Queued.Add((jobKey, payload));
            }

            return Task.FromResult(created);
        }
    }

    private sealed class NoProvider : IMarketDataProvider
    {
        public string Name => "fake";

        public Task<SakerWatch.BuildingBlocks.Kernel.Results.Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SakerWatch.BuildingBlocks.Kernel.Results.Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static (MarketDataScheduler Scheduler, RecordingEnqueuer Jobs, FakeTimeProvider Clock) Build(
        DateTimeOffset now, string[] markets, bool failQueue = false, bool enabled = true)
    {
        var jobs = new RecordingEnqueuer(failQueue);
        ServiceProvider provider = new ServiceCollection().AddSingleton<IJobEnqueuer>(jobs).BuildServiceProvider();
        var clock = new FakeTimeProvider(now);
        var router = new MarketDataProviderRouter(
            [new NoProvider()],
            Options.Create(new MarketDataProvidersOptions { Markets = markets.ToDictionary(m => m, _ => "fake") }));
        var scheduler = new MarketDataScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            router,
            ExchangeCalendars.Default(),
            Options.Create(new IngestionOptions { Enabled = enabled }),
            clock,
            NullLogger<MarketDataScheduler>.Instance);
        return (scheduler, jobs, clock);
    }

    [Fact]
    public async Task EnsureJobs_QueuesTheSettledSessionOfEveryEnabledMarket()
    {
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, _) = Build(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), markets: ["XNAS", "XNYS"]);

        int failures = await scheduler.EnsureJobsAsync(CancellationToken.None);

        failures.Should().Be(0);
        jobs.Queued.Select(q => q.Key).Should().BeEquivalentTo("marketdata.ingest-daily:XNAS:20261016", "marketdata.ingest-daily:XNYS:20261016");
        jobs.Queued.Select(q => q.Payload).Should().AllBeOfType<IngestMarketDayJob>();
    }

    [Fact]
    public async Task EnsureJobs_AskingAgainForTheSameDay_QueuesNothingNew()
    {
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, _) = Build(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), markets: ["XNAS"]);

        await scheduler.EnsureJobsAsync(CancellationToken.None);
        await scheduler.EnsureJobsAsync(CancellationToken.None);

        jobs.Queued.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureJobs_WhenTheSessionSettlesLater_QueuesThatDayWithoutRepeatingTheEarlierOne()
    {
        // Friday 21:00 UTC: the session closed an hour ago and has not settled, so Thursday is the settled one.
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, FakeTimeProvider clock) = Build(new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero), markets: ["XNAS"]);
        await scheduler.EnsureJobsAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(2));
        await scheduler.EnsureJobsAsync(CancellationToken.None);

        jobs.Queued.Select(q => q.Key).Should().Equal("marketdata.ingest-daily:XNAS:20261015", "marketdata.ingest-daily:XNAS:20261016");
    }

    [Fact]
    public async Task EnsureJobs_AMarketWithAProviderButNoCalendar_IsSkippedWithoutStoppingTheOthers()
    {
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, _) = Build(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), markets: ["XTSE", "XNAS"]);

        int failures = await scheduler.EnsureJobsAsync(CancellationToken.None);

        failures.Should().Be(0);
        jobs.Queued.Should().ContainSingle().Which.Key.Should().Be("marketdata.ingest-daily:XNAS:20261016");
    }

    [Fact]
    public async Task EnsureJobs_WhenTheQueueFails_CountsTheFailureAndDoesNotThrow()
    {
        (MarketDataScheduler scheduler, _, _) = Build(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), markets: ["XNAS", "XNYS"], failQueue: true);

        int failures = await scheduler.EnsureJobsAsync(CancellationToken.None);

        failures.Should().Be(2);
    }

    [Fact]
    public async Task EnsureJobs_WithNoEnabledMarket_DoesNothing()
    {
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, _) = Build(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), markets: []);

        (await scheduler.EnsureJobsAsync(CancellationToken.None)).Should().Be(0);
        jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLoop_QueuesAtStartAndAgainWhenAnotherSessionSettles()
    {
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, FakeTimeProvider clock) = Build(new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero), markets: ["XNAS"]);

        await scheduler.StartAsync(CancellationToken.None);
        await WaitForAsync(() => jobs.Queued.Count >= 1);
        clock.Advance(TimeSpan.FromHours(2));
        await WaitForAsync(() => jobs.Queued.Count >= 2);
        await scheduler.StopAsync(CancellationToken.None);

        jobs.Queued.Select(q => q.Key).Should().Equal("marketdata.ingest-daily:XNAS:20261015", "marketdata.ingest-daily:XNAS:20261016");
    }

    [Fact]
    public async Task TheLoop_WhenDisabled_QueuesNothing()
    {
        (MarketDataScheduler scheduler, RecordingEnqueuer jobs, _) = Build(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), markets: ["XNAS"], enabled: false);

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await scheduler.StopAsync(CancellationToken.None);

        jobs.Queued.Should().BeEmpty();
    }

    // The loop runs on a background thread: wait for the condition with a generous real-time bound, never for a fixed pause.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, bound.Token);
        }
    }
}
