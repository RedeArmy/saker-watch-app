using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Jobs;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.Retention.Application;
using SakerWatch.Retention.Domain;
using SakerWatch.Retention.Infrastructure;

namespace SakerWatch.Integration.Tests.Retention;

/// <summary>The retention rules against a real PostgreSQL, as the worker role: what is removed, what is kept, dry runs, batching, re-runs and the daily job.</summary>
[Trait("Category", "Integration")]
public sealed class RetentionIntegrationTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private sealed class Env(ServiceProvider worker, MigratedDatabase db) : IAsyncDisposable
    {
        public ServiceProvider Worker => worker;

        public MigratedDatabase Db => db;

        public async Task<RetentionReport> RunAsync(bool dryRun)
        {
            await using AsyncServiceScope scope = worker.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            await session.BeginAsync(CancellationToken.None);
            RetentionReport report = await scope.ServiceProvider.GetRequiredService<RetentionRunner>().RunAsync(dryRun, CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
            return report;
        }

        public ValueTask DisposeAsync() => worker.DisposeAsync();
    }

    private async Task<Env> StartAsync(Dictionary<string, string?>? retention = null, TimeProvider? clock = null)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = db.Worker,
            ["Jobs:PollInterval"] = "00:00:00.050",
            ["Jobs:StatsInterval"] = "00:00:01",
        };
        foreach ((string key, string? value) in retention ?? [])
        {
            settings[$"Retention:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
        services.AddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.AddSakerPostgres(configuration);
        services.AddJobs(typeof(RetentionRunJob).Assembly);
        services.AddJobDispatcher(configuration);
        services.AddRetention(configuration);
        return new Env(services.BuildServiceProvider(), db);
    }

    private static async Task SeedAsync(MigratedDatabase db)
    {
        // Old and recent rows of every kind, plus rows that must never be touched whatever their age.
        await db.Admin.ExecuteAsync("""
            INSERT INTO platform.idempotency_keys (user_id, method, route, key, request_hash, state, created_at, expires_at)
            VALUES (gen_random_uuid(), 'PUT', '/r', 'expired-1', '\x00', 'completed', now() - interval '5 days', now() - interval '2 days'),
                   (gen_random_uuid(), 'PUT', '/r', 'expired-2', '\x00', 'completed', now() - interval '5 days', now() - interval '1 hour'),
                   (gen_random_uuid(), 'PUT', '/r', 'live-1', '\x00', 'completed', now(), now() + interval '1 day');
            INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, processed_at, attempts, next_attempt_at)
            VALUES (gen_random_uuid(), 't', '{}', now() - interval '30 days', now() - interval '10 days', 1, now()),
                   (gen_random_uuid(), 't', '{}', now() - interval '3 days', now() - interval '1 day', 1, now()),
                   (gen_random_uuid(), 't', '{}', now() - interval '30 days', NULL, 0, now());
            INSERT INTO platform.inbox_messages (consumer, message_id, processed_at)
            VALUES ('c', gen_random_uuid(), now() - interval '10 days'), ('c', gen_random_uuid(), now() - interval '1 day');
            INSERT INTO platform.job_queue (id, job_key, type, payload, state, attempts, max_attempts, available_at, created_at, finished_at)
            VALUES (gen_random_uuid(), 'ok-old', 't', '{}', 'succeeded', 1, 5, now(), now() - interval '30 days', now() - interval '20 days'),
                   (gen_random_uuid(), 'ok-new', 't', '{}', 'succeeded', 1, 5, now(), now() - interval '6 days', now() - interval '5 days'),
                   (gen_random_uuid(), 'dead-old', 't', '{}', 'dead', 5, 5, now(), now() - interval '120 days', now() - interval '100 days'),
                   (gen_random_uuid(), 'dead-new', 't', '{}', 'dead', 5, 5, now(), now() - interval '40 days', now() - interval '30 days'),
                   (gen_random_uuid(), 'pending-old', 't', '{}', 'pending', 0, 5, now(), now() - interval '60 days', NULL);
            """);
    }

    private static async Task<long> CountAsync(MigratedDatabase db, string table) =>
        await db.Admin.ScalarAsync<long>($"SELECT count(*) FROM {table}");

    [Fact]
    public async Task DryRun_ReportsWhatWouldGo_AndDeletesNothing()
    {
        await using Env env = await StartAsync();
        await SeedAsync(env.Db);

        RetentionReport report = await env.RunAsync(dryRun: true);

        report.DryRun.Should().BeTrue();
        report.Targets.ToDictionary(t => t.Target, t => t.Rows).Should().BeEquivalentTo(new Dictionary<RetentionTarget, long>
        {
            [RetentionTarget.IdempotencyKeys] = 2,
            [RetentionTarget.OutboxProcessed] = 1,
            [RetentionTarget.InboxProcessed] = 1,
            [RetentionTarget.JobsSucceeded] = 1,
            [RetentionTarget.JobsDead] = 1,
        });
        (await CountAsync(env.Db, "platform.idempotency_keys")).Should().Be(3);
        (await CountAsync(env.Db, "platform.outbox_messages")).Should().Be(3);
        (await CountAsync(env.Db, "platform.inbox_messages")).Should().Be(2);
        (await CountAsync(env.Db, "platform.job_queue")).Should().Be(5);
    }

    [Fact]
    public async Task Delete_RemovesOnlyWhatHasAgedOut_AndAnotherRunChangesNothing()
    {
        await using Env env = await StartAsync();
        await SeedAsync(env.Db);

        RetentionReport first = await env.RunAsync(dryRun: false);
        RetentionReport second = await env.RunAsync(dryRun: false);

        first.Targets.Sum(t => t.Rows).Should().Be(6);
        second.Targets.Sum(t => t.Rows).Should().Be(0, "running twice is the same as running once");
        (await env.Db.Admin.ScalarAsync<string>("SELECT string_agg(key, ',' ORDER BY key) FROM platform.idempotency_keys")).Should().Be("live-1");
        (await CountAsync(env.Db, "platform.outbox_messages")).Should().Be(2);
        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NULL")).Should().Be(1, "an unprocessed message is never removed, however old");
        (await CountAsync(env.Db, "platform.inbox_messages")).Should().Be(1);
        (await env.Db.Admin.ScalarAsync<string>("SELECT string_agg(job_key, ',' ORDER BY job_key) FROM platform.job_queue")).Should().Be("dead-new,ok-new,pending-old");
    }

    [Fact]
    public async Task ABacklogLargerThanTheBatchLimit_IsFinishedByTheNextRun()
    {
        await using Env env = await StartAsync(new() { ["BatchSize"] = "2", ["MaxBatchesPerTarget"] = "2" });
        await env.Db.Admin.ExecuteAsync("""
            INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, processed_at, attempts, next_attempt_at)
            SELECT gen_random_uuid(), 't', '{}', now() - interval '30 days', now() - interval '20 days', 1, now() FROM generate_series(1, 5);
            """);

        RetentionReport first = await env.RunAsync(dryRun: false);
        RetentionReport second = await env.RunAsync(dryRun: false);

        RetentionTargetResult firstOutbox = first.Targets.Single(t => t.Target == RetentionTarget.OutboxProcessed);
        firstOutbox.Rows.Should().Be(4);
        firstOutbox.MoreRemaining.Should().BeTrue();
        second.Targets.Single(t => t.Target == RetentionTarget.OutboxProcessed).Should().Be(new RetentionTargetResult(RetentionTarget.OutboxProcessed, 1, MoreRemaining: false));
        (await CountAsync(env.Db, "platform.outbox_messages")).Should().Be(0);
    }

    [Fact]
    public async Task TwoRunsAtTheSameTime_DoNotDeleteTheSameRowTwice()
    {
        await using Env env = await StartAsync(new() { ["BatchSize"] = "10" });
        await env.Db.Admin.ExecuteAsync("""
            INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, processed_at, attempts, next_attempt_at)
            SELECT gen_random_uuid(), 't', '{}', now() - interval '30 days', now() - interval '20 days', 1, now() FROM generate_series(1, 200);
            INSERT INTO platform.inbox_messages (consumer, message_id, processed_at)
            SELECT 'c', gen_random_uuid(), now() - interval '20 days' FROM generate_series(1, 200);
            """);

        RetentionReport[] reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.RunAsync(dryRun: false)));

        reports.Sum(r => r.Targets.Single(t => t.Target == RetentionTarget.OutboxProcessed).Rows).Should().Be(200, "SKIP LOCKED gives each row to exactly one run");
        reports.Sum(r => r.Targets.Single(t => t.Target == RetentionTarget.InboxProcessed).Rows).Should().Be(200, "concurrent deletes never count a row twice");
        (await CountAsync(env.Db, "platform.outbox_messages")).Should().Be(0);
        (await CountAsync(env.Db, "platform.inbox_messages")).Should().Be(0);
    }

    [Fact]
    public async Task EachRun_ReportsItsRowsAsAMetric_WithTargetAndModeOnly()
    {
        await using Env env = await StartAsync();
        await SeedAsync(env.Db);
        var readings = new List<(string Target, string Mode, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "sakerwatch_retention_rows_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            KeyValuePair<string, object?>[] all = tags.ToArray();
            string target = all.Single(t => t.Key == "target").Value?.ToString() ?? string.Empty;
            string mode = all.Single(t => t.Key == "mode").Value?.ToString() ?? string.Empty;
            lock (readings)
            {
                readings.Add((target, mode, value));
            }
        });
        listener.Start();

        await env.RunAsync(dryRun: true);

        lock (readings)
        {
            readings.Should().Contain(("OutboxProcessed", "dry_run", 1));
            readings.Should().HaveCount(5);
        }
    }

    [Fact]
    public async Task TheDailyJob_IsEnqueuedOncePerDay_NoMatterHowManyChecksOrWorkers()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 3, 0, 0, TimeSpan.Zero));
        await using Env env = await StartAsync(clock: clock);
        var scheduler = env.Worker.GetServices<IHostedService>().OfType<RetentionScheduler>().Single();
        RetentionOptions settings = env.Worker.GetRequiredService<IOptions<RetentionOptions>>().Value;

        RetentionScheduleOutcome first = await scheduler.EnsureTodaysJobAsync(settings, CancellationToken.None);
        RetentionScheduleOutcome again = await scheduler.EnsureTodaysJobAsync(settings, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(1));
        RetentionScheduleOutcome nextDay = await scheduler.EnsureTodaysJobAsync(settings, CancellationToken.None);

        (first, again, nextDay).Should().Be((RetentionScheduleOutcome.Created, RetentionScheduleOutcome.AlreadyScheduled, RetentionScheduleOutcome.Created));
        (await env.Db.Admin.ScalarAsync<string>("SELECT string_agg(job_key, ',' ORDER BY job_key) FROM platform.job_queue"))
            .Should().Be("retention:dryrun:20261014,retention:dryrun:20261015", "dry run is the default mode");
        (await env.Db.Admin.ScalarAsync<string>("SELECT type FROM platform.job_queue LIMIT 1")).Should().Be("retention.run.v1");
    }

    [Fact]
    public async Task TheQueuedJob_IsRunByTheDispatcher_AndRemovesWhatHasAgedOut()
    {
        await using Env env = await StartAsync(new() { ["DryRun"] = "false" });
        await SeedAsync(env.Db);
        var scheduler = env.Worker.GetServices<IHostedService>().OfType<RetentionScheduler>().Single();
        await scheduler.EnsureTodaysJobAsync(env.Worker.GetRequiredService<IOptions<RetentionOptions>>().Value, CancellationToken.None);

        var dispatcher = env.Worker.GetServices<IHostedService>().OfType<JobDispatcherService>().Single();
        await dispatcher.StartAsync(CancellationToken.None);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE state = 'succeeded' AND job_key LIKE 'retention:%'") < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await dispatcher.StopAsync(CancellationToken.None);

        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE state = 'succeeded' AND job_key LIKE 'retention:%'")).Should().Be(1);
        (await env.Db.Admin.ScalarAsync<string>("SELECT string_agg(key, ',') FROM platform.idempotency_keys")).Should().Be("live-1");
        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.inbox_messages")).Should().Be(1);
    }

    [Fact]
    public async Task TheWorkerRole_CannotReadIdempotencyKeysDirectly_OnlyThroughTheFunctions()
    {
        await using Env env = await StartAsync();

        Func<Task> direct = () => env.Db.Worker.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys");

        await direct.Should().ThrowAsync<Npgsql.PostgresException>().Where(e => e.SqlState == "42501");
    }
}
