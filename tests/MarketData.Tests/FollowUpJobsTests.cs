using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

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

    private static (FollowUpJobs Waiter, Enqueuer Jobs) BuildWithCache(PerformanceCacheOptions cache)
    {
        var jobs = new Enqueuer();
        return (new FollowUpJobs(jobs, Options.Create(new IngestionOptions()), new FakeTimeProvider(_now), Options.Create(cache)), jobs);
    }

    private static readonly Mic _nasdaq = Mic.Parse("XNAS");

    [Fact]
    public async Task QueueWarm_LoadsOfAMarketInTheSameSliceShareOneJob_ThatBecomesDueAMinuteAfterTheSlice()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = BuildWithCache(new PerformanceCacheOptions());
        DateTimeOffset firstLoad = new(2026, 10, 17, 3, 2, 0, TimeSpan.Zero);

        await waiter.QueueWarmAsync(_nasdaq, firstLoad, CancellationToken.None);
        await waiter.QueueWarmAsync(_nasdaq, firstLoad.AddMinutes(11), CancellationToken.None); // 03:13, same slice of 15 minutes
        await waiter.QueueWarmAsync(_nasdaq, firstLoad.AddMinutes(14), CancellationToken.None); // 03:16, the next one

        jobs.Queued.Select(job => job.Key).Should().Equal(
            "marketdata.warm-performance:XNAS:202610170300", "marketdata.warm-performance:XNAS:202610170300", "marketdata.warm-performance:XNAS:202610170315");
        jobs.Queued[0].Payload.Should().Be(new WarmPerformanceJob("XNAS", new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 17, 3, 15, 0, TimeSpan.Zero)));
        jobs.Queued[0].Options.Should().Be(new JobEnqueueOptions(AvailableAt: new DateTimeOffset(2026, 10, 17, 3, 16, 0, TimeSpan.Zero), MaxAttempts: 3));
    }

    [Fact]
    public async Task QueueWarm_AnotherMarketHasItsOwnJob()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = BuildWithCache(new PerformanceCacheOptions());

        await waiter.QueueWarmAsync(_nasdaq, _now, CancellationToken.None);
        await waiter.QueueWarmAsync(Mic.Parse("XNYS"), _now, CancellationToken.None);

        jobs.Queued.Select(job => job.Key).Distinct().Should().HaveCount(2);
    }

    [Theory]
    [InlineData(15, "2026-10-17T12:00:00", "2026-10-17T12:15:00")]
    [InlineData(60, "2026-10-17T12:00:00", "2026-10-17T13:00:00")]
    [InlineData(10, "2026-10-17T12:00:00", "2026-10-17T12:10:00")]
    public void SliceOf_CutsTheClockInSlices(int minutes, string from, string to)
    {
        (DateTimeOffset start, DateTimeOffset end) = WarmPerformanceJob.SliceOf(new DateTimeOffset(2026, 10, 17, 12, 4, 30, TimeSpan.Zero), minutes);

        (start, end).Should().Be((DateTimeOffset.Parse(from + "Z", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(to + "Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task QueueWarm_QueuesNothing_WhenTheCacheOrTheWarmingIsOff(bool enabled, bool warm)
    {
        (FollowUpJobs waiter, Enqueuer jobs) = BuildWithCache(new PerformanceCacheOptions { Enabled = enabled, WarmAfterLoad = warm });

        bool queued = await waiter.QueueWarmAsync(_nasdaq, _now, CancellationToken.None);

        queued.Should().BeFalse();
        jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task QueueWarm_WithNoCacheOptions_WarmsNothing()
    {
        (FollowUpJobs waiter, Enqueuer jobs) = Build();

        (await waiter.QueueWarmAsync(_nasdaq, _now, CancellationToken.None)).Should().BeFalse();
        jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task WarmPerformanceHandler_ReadsEveryListingThatLoadedInTheSlice_InOneBatch()
    {
        var one = StockId.Create(Guid.CreateVersion7());
        var two = StockId.Create(Guid.CreateVersion7());
        var late = StockId.Create(Guid.CreateVersion7());
        DateTimeOffset from = new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);
        var stamps = new SliceStamps(new Dictionary<StockId, DateTimeOffset> { [one] = from.AddMinutes(1), [two] = from.AddMinutes(14), [late] = from.AddMinutes(15) });
        var reader = new RecordingReader(new Dictionary<StockId, PerformanceReport> { [one] = PerformanceCalculator.NoData(), [two] = PerformanceCalculator.NoData() });

        await new WarmPerformanceHandler(reader, stamps).HandleAsync(
            new WarmPerformanceJob("XNAS", from, from.AddMinutes(15)), new JobContext(Guid.CreateVersion7(), "k", 1, 3, null), CancellationToken.None);

        reader.Asked.Should().ContainSingle().Which.Should().BeEquivalentTo([one, two], "the load at 03:15 belongs to the next slice");
    }

    [Fact]
    public async Task WarmPerformanceHandler_WhenNothingLoadedInTheSlice_ReadsNothing()
    {
        var reader = new RecordingReader(new Dictionary<StockId, PerformanceReport>());
        DateTimeOffset from = new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);

        await new WarmPerformanceHandler(reader, new SliceStamps([])).HandleAsync(
            new WarmPerformanceJob("XNAS", from, from.AddMinutes(15)), new JobContext(Guid.CreateVersion7(), "k", 1, 3, null), CancellationToken.None);

        reader.Asked.Should().BeEmpty();
    }

    private sealed class SliceStamps(Dictionary<StockId, DateTimeOffset> stamps) : IIngestionStamps
    {
        public Task<IReadOnlyDictionary<StockId, DateTimeOffset>> LastSuccessAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<StockId, DateTimeOffset>>(stamps);

        public Task<IReadOnlyList<StockId>> LoadedBetweenAsync(string market, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StockId>>([.. stamps.Where(entry => entry.Value >= from && entry.Value < to).Select(entry => entry.Key)]);
    }

    private sealed class RecordingReader(IReadOnlyDictionary<StockId, PerformanceReport> answer) : IPerformanceReader
    {
        public List<StockId[]> Asked { get; } = [];

        public Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
        {
            Asked.Add([.. stocks]);
            return Task.FromResult(answer);
        }
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
