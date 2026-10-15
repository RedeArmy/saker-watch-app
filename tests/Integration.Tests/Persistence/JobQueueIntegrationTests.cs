using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Jobs;
using SakerWatch.Demo.IntegrationFeatures;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>Job queue: deterministic keys, leases with fencing, heartbeat, retries, dead-lettering and shutdown (ADR-0005, ADR-0017).</summary>
[Trait("Category", "Integration")]
public sealed class JobQueueIntegrationTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private sealed record Settings(int MaxConcurrency = 2, string Lease = "00:05:00", string Backoff = "00:00:00.010");

    private sealed class Env(ServiceProvider api, ServiceProvider worker, MigratedDatabase db) : IAsyncDisposable
    {
        public ServiceProvider Api => api;

        public ServiceProvider Worker => worker;

        public MigratedDatabase Db => db;

        public JobProbe Probe => worker.GetRequiredService<JobProbe>();

        public JobProcessor Processor => worker.GetRequiredService<JobProcessor>();

        public async Task<bool> EnqueueAsync(string key, string name = "widget", int maxAttempts = 3, DateTimeOffset? availableAt = null)
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync(key, new ComputeJob(name), new JobEnqueueOptions(availableAt, maxAttempts), CancellationToken.None);
        }

        public Task<string?> State(string key) => db.Admin.ScalarAsync<string>("SELECT state FROM platform.job_queue WHERE job_key = @k", ("k", key));

        public Task<int> Attempts(string key) => db.Admin.ScalarAsync<int>("SELECT attempts FROM platform.job_queue WHERE job_key = @k", ("k", key));

        public Task<long> Items() => db.Admin.ScalarAsync<long>("SELECT count(*) FROM public.probe_items");

        public Task<int> MakeDue() =>
            db.Admin.ExecuteAsync("UPDATE platform.job_queue SET available_at = now() - interval '1 second' WHERE state = 'pending'");

        public async ValueTask DisposeAsync()
        {
            await api.DisposeAsync();
            await worker.DisposeAsync();
        }
    }

    private async Task<Env> StartAsync(Settings? settings = null)
    {
        Settings s = settings ?? new Settings();
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Admin.ExecuteAsync("CREATE TABLE public.probe_items (id uuid PRIMARY KEY, name text NOT NULL)");
        await db.Admin.ExecuteAsync("GRANT SELECT, INSERT ON public.probe_items TO api_role, worker_role");

        ServiceProvider Build(string connection)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connection,
                ["Jobs:MaxConcurrency"] = s.MaxConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Jobs:LeaseDuration"] = s.Lease,
                ["Jobs:BackoffBase"] = s.Backoff,
                ["Jobs:BackoffCap"] = "00:00:00.100",
                ["Jobs:PollInterval"] = "00:00:00.050",
                ["Jobs:StatsInterval"] = "00:00:01",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
            services.AddSingleton<JobProbe>();
            services.AddScoped<TestUser>();
            services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUser>());
            services.AddSakerPostgres(configuration);
            services.AddJobs(typeof(ComputeJob).Assembly);
            services.AddJobDispatcher(configuration);
            services.AddDbContext<ProbeItemsContext>((provider, options) => options
                .UseNpgsql(provider.GetRequiredService<DbSession>().Connection)
                .AddInterceptors(
                    provider.GetRequiredService<UserContextConnectionInterceptor>(),
                    provider.GetRequiredService<AmbientTransactionInterceptor>()));
            return services.BuildServiceProvider();
        }

        return new Env(Build(db.Api), Build(db.Worker), db);
    }

    [Fact]
    public async Task Enqueue_CreatesAPendingJobUnderItsDeterministicKey()
    {
        await using Env env = await StartAsync();

        bool created = await env.EnqueueAsync("analysis:NVDA:v1:2026-10-13");

        created.Should().BeTrue();
        (await env.State("analysis:NVDA:v1:2026-10-13")).Should().Be("pending");
        (await env.Db.Admin.ScalarAsync<string>("SELECT type FROM platform.job_queue")).Should().Be("demo.compute.v1");
    }

    [Fact]
    public async Task Enqueue_AcceptsANonUtcAvailabilityAndRoutesByTheRuntimePayloadType()
    {
        await using Env env = await StartAsync();
        DateTimeOffset available = DateTimeOffset.UtcNow.AddSeconds(-5).ToOffset(TimeSpan.FromHours(-6));

        await using (AsyncServiceScope scope = env.Api.CreateAsyncScope())
        {
            object payload = new ComputeJob("through-a-base-type");
            bool created = await scope.ServiceProvider.GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync("runtime-type", payload, new JobEnqueueOptions(available), CancellationToken.None);
            created.Should().BeTrue();
        }

        (await env.Db.Admin.ScalarAsync<string>("SELECT type FROM platform.job_queue")).Should().Be("demo.compute.v1");
        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeTrue("the job was already due, whatever offset it was given in");
        (await env.State("runtime-type")).Should().Be("succeeded");
    }

    [Fact]
    public async Task EnqueueingTheSameKeyTwice_CreatesOneJob_AndReportsIt()
    {
        await using Env env = await StartAsync();

        bool first = await env.EnqueueAsync("ingest:XNAS:2026-10-13");
        bool second = await env.EnqueueAsync("ingest:XNAS:2026-10-13", "different payload is ignored");

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(1);
    }

    [Fact]
    public async Task ParallelEnqueuesOfTheSameKey_CreateExactlyOneJob()
    {
        await using Env env = await StartAsync();

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => env.EnqueueAsync("ingest:XNAS:2026-10-13")));

        results.Count(created => created).Should().Be(1);
        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(1);
    }

    [Fact]
    public async Task EnqueueInsideAUnitOfWork_FollowsItsOutcome()
    {
        await using Env env = await StartAsync();
        foreach ((string key, bool commit) in new[] { ("kept", true), ("dropped", false) })
        {
            await using AsyncServiceScope scope = env.Api.CreateAsyncScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.BeginAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IJobEnqueuer>().EnqueueAsync(key, new ComputeJob("x"), null, CancellationToken.None);
            await (commit ? uow.CommitAsync(CancellationToken.None) : uow.RollbackAsync(CancellationToken.None));
        }

        (await env.State("kept")).Should().Be("pending");
        (await env.State("dropped")).Should().BeNull();
    }

    [Fact]
    public async Task Enqueue_RejectsAKeyThatIsTooLongOrHasControlCharacters()
    {
        await using Env env = await StartAsync();

        Func<Task> tooLong = () => env.EnqueueAsync(new string('k', 201));
        Func<Task> control = () => env.EnqueueAsync("bad\nkey");

        await tooLong.Should().ThrowAsync<ArgumentException>();
        await control.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ADueJob_RunsOnce_AndIsMarkedSucceeded()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("job-1");

        bool first = await env.Processor.ProcessNextAsync(CancellationToken.None);
        bool second = await env.Processor.ProcessNextAsync(CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await env.State("job-1")).Should().Be("succeeded");
        (await env.Attempts("job-1")).Should().Be(1);
        (await env.Items()).Should().Be(1);
        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE lease_token IS NOT NULL OR finished_at IS NULL")).Should().Be(0);
    }

    [Fact]
    public async Task AJobScheduledForTheFuture_IsNotRunYet()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("later", availableAt: DateTimeOffset.UtcNow.AddHours(1));

        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AFailingJob_RollsBackItsEffect_AndRetriesWithBackoff()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("flaky");
        env.Probe.FailNext(1);

        await env.Processor.ProcessNextAsync(CancellationToken.None);

        (await env.State("flaky")).Should().Be("pending");
        (await env.Attempts("flaky")).Should().Be(1);
        (await env.Items()).Should().Be(0, "the failed attempt's write rolled back");
        (await env.Db.Admin.ScalarAsync<string>("SELECT last_error FROM platform.job_queue")).Should().Contain("job failed on purpose");

        await env.Db.Admin.ExecuteAsync("UPDATE platform.job_queue SET available_at = now() + interval '1 hour'");
        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeFalse("the retry waits for its backoff");

        await env.MakeDue();
        await env.Processor.ProcessNextAsync(CancellationToken.None);
        (await env.State("flaky")).Should().Be("succeeded");
        (await env.Items()).Should().Be(1, "the effect happened exactly once");
        env.Probe.Attempts.Should().Equal(1, 2);
    }

    [Fact]
    public async Task AJobThatKeepsFailing_IsParkedAsDeadAfterItsMaximumAttempts()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("poison", maxAttempts: 3);
        env.Probe.AlwaysFail = true;

        for (int i = 0; i < 3; i++)
        {
            await env.MakeDue();
            (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        }

        await env.MakeDue();
        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeFalse("a dead job is not claimed again");
        (await env.State("poison")).Should().Be("dead");
        (await env.Attempts("poison")).Should().Be(3);
        (await env.Db.Admin.ScalarAsync<DateTime?>("SELECT finished_at FROM platform.job_queue")).Should().NotBeNull();
        (await env.Items()).Should().Be(0);
    }

    [Fact]
    public async Task AJobOfAnUnknownType_FailsWithAClearError()
    {
        await using Env env = await StartAsync();
        await env.Db.Admin.ExecuteAsync(
            "INSERT INTO platform.job_queue (id, job_key, type, payload, state, attempts, max_attempts, available_at, created_at) VALUES (@id, 'ghost', 'demo.removed.v1', '{}'::jsonb, 'pending', 0, 2, now() - interval '1 second', now())",
            ("id", Guid.CreateVersion7()));

        await env.Processor.ProcessNextAsync(CancellationToken.None);

        (await env.Db.Admin.ScalarAsync<string>("SELECT last_error FROM platform.job_queue")).Should().Contain("Unknown job type");
        (await env.State("ghost")).Should().Be("dead", "a job that cannot run is parked at once, not retried");
        (await env.Attempts("ghost")).Should().Be(1);
    }

    [Fact]
    public async Task TheErrorKeptOnAFailedJob_HoldsNoCredentialsOrEmailsAndIsOneLine()
    {
        await using Env env = await StartAsync();
        env.Probe.AlwaysFail = true;
        env.Probe.FailureMessage = "cannot connect: Host=db.internal;Password=hunter2;User=eder@example.com\nat Npgsql.Connector.Open()";
        await env.EnqueueAsync("leaky", maxAttempts: 3);

        await env.Processor.ProcessNextAsync(CancellationToken.None);

        string error = (await env.Db.Admin.ScalarAsync<string>("SELECT last_error FROM platform.job_queue"))!;
        error.Should().StartWith("InvalidOperationException:").And.Contain("Host=db.internal");
        error.Should().NotContain("hunter2").And.NotContain("eder@example.com").And.NotContain("\n");
    }

    [Fact]
    public async Task AJobWhosePayloadDoesNotFitItsType_IsParkedAtOnce()
    {
        await using Env env = await StartAsync();
        await env.Db.Admin.ExecuteAsync(
            "INSERT INTO platform.job_queue (id, job_key, type, payload, state, attempts, max_attempts, available_at, created_at) VALUES (@id, 'garbled', 'demo.compute.v1', '{\"name\": {\"nested\": true}}'::jsonb, 'pending', 0, 5, now() - interval '1 second', now())",
            ("id", Guid.CreateVersion7()));

        await env.Processor.ProcessNextAsync(CancellationToken.None);

        (await env.State("garbled")).Should().Be("dead");
    }

    [Fact]
    public async Task AnAbandonedLease_IsReclaimedAfterItExpires()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("crashed-worker");
        // A worker claimed it and died: the job is leased with an expired lease and one attempt used.
        await env.Db.Admin.ExecuteAsync(
            "UPDATE platform.job_queue SET state = 'leased', attempts = 1, lease_token = @t, lease_expires_at = now() - interval '1 minute', leased_by = 'dead-worker'",
            ("t", Guid.CreateVersion7()));

        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        (await env.State("crashed-worker")).Should().Be("succeeded");
        (await env.Attempts("crashed-worker")).Should().Be(2);
        (await env.Items()).Should().Be(1);
    }

    [Fact]
    public async Task ALiveLease_IsNotTakenByAnotherWorker()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("busy");
        await env.Db.Admin.ExecuteAsync(
            "UPDATE platform.job_queue SET state = 'leased', attempts = 1, lease_token = @t, lease_expires_at = now() + interval '1 hour'",
            ("t", Guid.CreateVersion7()));

        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AJobWhoseLastAllowedAttemptCrashedTheWorker_IsParkedInsteadOfLoopingForever()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("poison-crash", maxAttempts: 2);
        await env.Db.Admin.ExecuteAsync(
            "UPDATE platform.job_queue SET state = 'leased', attempts = 2, lease_token = @t, lease_expires_at = now() - interval '1 minute'",
            ("t", Guid.CreateVersion7()));

        bool ran = await env.Processor.ProcessNextAsync(CancellationToken.None);

        ran.Should().BeFalse();
        (await env.State("poison-crash")).Should().Be("dead");
        env.Probe.Runs.Should().Be(0);
    }

    [Fact]
    public async Task ALeaseStolenWhileTheJobRuns_IsDetectedByTheHeartbeat_AndNothingIsCommitted()
    {
        await using Env env = await StartAsync(new Settings(Lease: "00:00:01.500"));
        await env.EnqueueAsync("stolen");
        env.Probe.Hold();
        Task<bool> running = env.Processor.ProcessNextAsync(CancellationToken.None);
        await env.Probe.Entered;

        var thief = Guid.CreateVersion7();
        await env.Db.Admin.ExecuteAsync(
            "UPDATE platform.job_queue SET lease_token = @t, leased_by = 'thief', lease_expires_at = now() + interval '1 hour'",
            ("t", thief));
        await running.WaitAsync(TimeSpan.FromSeconds(15));

        (await env.Items()).Should().Be(0, "the stale attempt's effect never committed");
        (await env.Db.Admin.ScalarAsync<Guid>("SELECT lease_token FROM platform.job_queue")).Should().Be(thief);
        (await env.State("stolen")).Should().Be("leased");
    }

    [Fact]
    public async Task ACompletionWithAStaleToken_IsFenced_AndTheEffectRollsBack()
    {
        // A long lease means the heartbeat is too slow to notice; the fenced completion is what protects us.
        await using Env env = await StartAsync(new Settings(Lease: "00:01:00"));
        await env.EnqueueAsync("fenced");
        env.Probe.IgnoreCancellation = true;
        env.Probe.Hold();
        Task<bool> running = env.Processor.ProcessNextAsync(CancellationToken.None);
        await env.Probe.Entered;

        var thief = Guid.CreateVersion7();
        await env.Db.Admin.ExecuteAsync("UPDATE platform.job_queue SET lease_token = @t, leased_by = 'thief'", ("t", thief));
        env.Probe.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(15));

        (await env.Items()).Should().Be(0, "the stale worker could not commit its effect");
        (await env.Db.Admin.ScalarAsync<Guid>("SELECT lease_token FROM platform.job_queue")).Should().Be(thief);
        (await env.State("fenced")).Should().Be("leased");
    }

    [Fact]
    public async Task TheHeartbeat_KeepsALongJobsLeaseAlive_SoNobodyElseTakesIt()
    {
        await using Env env = await StartAsync(new Settings(Lease: "00:00:01.500"));
        await env.EnqueueAsync("long");
        env.Probe.Hold();
        Task<bool> running = env.Processor.ProcessNextAsync(CancellationToken.None);
        await env.Probe.Entered;

        await Task.Delay(TimeSpan.FromSeconds(3.5));
        bool stolen = await env.Processor.ProcessNextAsync(CancellationToken.None);
        env.Probe.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(15));

        stolen.Should().BeFalse("the lease was renewed well past its original 1.5 s");
        (await env.State("long")).Should().Be("succeeded");
        (await env.Attempts("long")).Should().Be(1);
        env.Probe.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Shutdown_RollsBackTheRunningJob_AndReleasesItsLeaseWithoutCountingTheAttempt()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("stopping");
        env.Probe.Hold();
        using var stop = new CancellationTokenSource();
        Task<bool> running = env.Processor.ProcessNextAsync(stop.Token);
        await env.Probe.Entered;

        await stop.CancelAsync();
        Func<Task> act = () => running;

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await env.State("stopping")).Should().Be("pending");
        (await env.Attempts("stopping")).Should().Be(0);
        (await env.Db.Admin.ScalarAsync<Guid?>("SELECT lease_token FROM platform.job_queue")).Should().BeNull();
        (await env.Items()).Should().Be(0);
    }

    [Fact]
    public async Task ParallelWorkers_RunEveryJobExactlyOnce()
    {
        await using Env env = await StartAsync();
        string[] keys = [.. Enumerable.Range(0, 40).Select(i => $"job-{i}")];
        foreach (string key in keys)
        {
            await env.EnqueueAsync(key, key);
        }

        int[] runsPerWorker = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            int runs = 0;
            while (await env.Processor.ProcessNextAsync(CancellationToken.None))
            {
                runs++;
            }

            return runs;
        }));

        runsPerWorker.Sum().Should().Be(40);
        (await env.Items()).Should().Be(40);
        (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE state = 'succeeded' AND attempts = 1")).Should().Be(40);
    }

    [Fact]
    public async Task TheDispatcherService_RunsJobsInTheBackground_AndStopsGracefully()
    {
        await using Env env = await StartAsync(new Settings(MaxConcurrency: 3));
        foreach (int i in Enumerable.Range(0, 10))
        {
            await env.EnqueueAsync($"bg-{i}", $"bg-{i}");
        }

        var dispatcher = env.Worker.GetServices<IHostedService>().OfType<JobDispatcherService>().Single();
        await dispatcher.StartAsync(CancellationToken.None);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (await env.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE state = 'succeeded'") < 10 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Func<Task> stop = () => dispatcher.StopAsync(CancellationToken.None);

        (await env.Items()).Should().Be(10);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task QueueGauges_ReportDepthAgeInFlightAndDeadJobs()
    {
        await using Env env = await StartAsync();
        await env.EnqueueAsync("waiting-1");
        await env.EnqueueAsync("waiting-2");
        await env.EnqueueAsync("dead-1");
        await env.Db.Admin.ExecuteAsync("UPDATE platform.job_queue SET available_at = now() - interval '2 minutes' WHERE job_key = 'waiting-1'");
        await env.Db.Admin.ExecuteAsync("UPDATE platform.job_queue SET state = 'dead' WHERE job_key = 'dead-1'");
        var readings = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name.StartsWith("sakerwatch_jobs_", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => readings[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => readings[instrument.Name] = value);
        listener.Start();

        var metrics = env.Worker.GetRequiredService<JobQueueMetrics>();
        await metrics.RefreshAsync(env.Db.Worker, DateTimeOffset.UtcNow, CancellationToken.None);
        listener.RecordObservableInstruments();

        readings["sakerwatch_jobs_queue_depth"].Should().Be(2);
        readings["sakerwatch_jobs_dead"].Should().Be(1);
        readings["sakerwatch_jobs_in_flight"].Should().Be(0);
        readings["sakerwatch_jobs_queue_oldest_age_seconds"].Should().BeGreaterThan(110);
    }
}
