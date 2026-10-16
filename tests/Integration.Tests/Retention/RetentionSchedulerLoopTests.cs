using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.Retention.Application;
using SakerWatch.Retention.Infrastructure;

namespace SakerWatch.Integration.Tests.Retention;

/// <summary>The scheduler's loop timing, with a fake clock and a fake queue (no database): quick retry after a failure, normal spacing after a success.</summary>
public sealed class RetentionSchedulerLoopTests
{
    private sealed class FlakyEnqueuer(int failures) : IJobEnqueuer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
            where TPayload : notnull
        {
            int call = Interlocked.Increment(ref _calls);
            return call <= failures ? throw new InvalidOperationException("database not ready") : Task.FromResult(true);
        }
    }

    // Time only moves when asked; real time is spent just to let the loop run between steps.
    private static async Task<TimeSpan> AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> done, TimeSpan step, TimeSpan limit)
    {
        TimeSpan advanced = TimeSpan.Zero;
        while (!done() && advanced < limit)
        {
            await Task.Delay(30);
            clock.Advance(step);
            advanced += step;
        }

        await Task.Delay(30);
        return advanced;
    }

    // The clock is not involved in the first attempt, so wait for it by condition with a generous real-time bound: a loaded machine can
    // take far longer than any fixed pause to start the loop.
    private static async Task WaitForCallsAsync(FlakyEnqueuer enqueuer, int calls)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (enqueuer.Calls < calls)
        {
            await Task.Delay(10, bound.Token);
        }

        await Task.Delay(30);
    }

    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Production", false, false)]
    [InlineData("Development", true, false)]
    public async Task DryRunOutsideDevelopment_IsWarnedAboutAtStart(string environment, bool dryRun, bool warned)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 3, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        var enqueuer = new FlakyEnqueuer(failures: 0);
        services.AddScoped<IJobEnqueuer>(_ => enqueuer);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var logger = new FakeLogger<RetentionScheduler>();
        using var scheduler = new RetentionScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RetentionOptions { DryRun = dryRun }),
            clock,
            new TestHostEnvironment(environment),
            logger);

        await scheduler.StartAsync(CancellationToken.None);

        // The warning is logged before the first enqueue attempt, so once that attempt has happened the answer is final. Wait for it
        // by condition (with a generous bound), not for a fixed pause: a loaded machine can take far longer than any pause.
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (enqueuer.Calls < 1)
        {
            await Task.Delay(10, bound.Token);
        }

        await scheduler.StopAsync(CancellationToken.None);

        logger.Collector.GetSnapshot().Any(r => r.Id.Id == 9705).Should().Be(warned);
    }

    [Fact]
    public async Task AfterAFailure_TheScheduleIsRetriedWithinMinutes_ThenKeepsTheNormalSpacing()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 3, 0, 0, TimeSpan.Zero));
        var enqueuer = new FlakyEnqueuer(failures: 1);
        var services = new ServiceCollection();
        services.AddScoped<IJobEnqueuer>(_ => enqueuer);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var settings = new RetentionOptions { CheckInterval = TimeSpan.FromHours(1) };
        using var scheduler = new RetentionScheduler(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(settings), clock, new TestHostEnvironment(Environments.Production), NullLogger<RetentionScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(enqueuer, 1);
        enqueuer.Calls.Should().Be(1, "the first attempt runs immediately and fails");

        TimeSpan toRetry = await AdvanceUntilAsync(clock, () => enqueuer.Calls >= 2, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
        enqueuer.Calls.Should().Be(2);
        toRetry.Should().BeLessThanOrEqualTo(RetentionScheduler.RetryDelay + TimeSpan.FromSeconds(10), "a failed attempt is retried after a minute, not after a whole hour");

        TimeSpan halfHour = await AdvanceUntilAsync(clock, () => enqueuer.Calls >= 3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
        enqueuer.Calls.Should().Be(2, $"after a success the next check waits the full interval (advanced {halfHour})");

        await AdvanceUntilAsync(clock, () => enqueuer.Calls >= 3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(40));
        enqueuer.Calls.Should().Be(3);

        await scheduler.StopAsync(CancellationToken.None);
    }
}
