using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Tracing;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Outbox;
using SakerWatch.Demo.IntegrationFeatures;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>Outbox writing, delivery, retry, dead-lettering and inbox dedupe (ADR-0005, ADR-0017) on a real PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class OutboxIntegrationTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private sealed class Env(ServiceProvider api, ServiceProvider worker, MigratedDatabase db) : IAsyncDisposable
    {
        public ServiceProvider Api => api;

        public ServiceProvider Worker => worker;

        public MigratedDatabase Db => db;

        public ConsumerProbe Probe => worker.GetRequiredService<ConsumerProbe>();

        public OutboxProcessor Processor => worker.GetRequiredService<OutboxProcessor>();

        public async Task PublishAsync(ThingCreated message, Guid? user = null, string? correlation = null)
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TestUser>().Id = user;
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = correlation;
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.BeginAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(message, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        public Task<long> Count(string sql) => db.Admin.ScalarAsync<long>(sql);

        public async ValueTask DisposeAsync()
        {
            await api.DisposeAsync();
            await worker.DisposeAsync();
        }
    }

    private static ThingCreated NewMessage(string name = "widget") => new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, name);

    private async Task<Env> StartAsync(int maxAttempts = 3)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Admin.ExecuteAsync("CREATE TABLE public.probe_items (id uuid PRIMARY KEY, name text NOT NULL)");
        await db.Admin.ExecuteAsync("GRANT SELECT, INSERT ON public.probe_items TO api_role, worker_role");

        ServiceProvider Build(string connection)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connection,
                ["Outbox:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Outbox:BackoffBase"] = "00:00:00.010",
                ["Outbox:BackoffCap"] = "00:00:00.050",
                ["Outbox:PollInterval"] = "00:00:00.050",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ConsumerProbe>();
            services.AddScoped<TestUser>();
            services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUser>());
            services.AddSakerPostgres(configuration);
            services.AddIntegrationEvents(typeof(ThingCreated).Assembly);
            services.AddOutboxDispatcher(configuration);
            services.AddDbContext<ProbeItemsContext>((provider, options) => options
                .UseNpgsql(provider.GetRequiredService<DbSession>().Connection)
                .AddInterceptors(
                    provider.GetRequiredService<UserContextConnectionInterceptor>(),
                    provider.GetRequiredService<AmbientTransactionInterceptor>()));
            return services.BuildServiceProvider();
        }

        return new Env(Build(db.Api), Build(db.Worker), db);
    }

    private static Task<int> MakeDueAsync(Env env) =>
        env.Db.Admin.ExecuteAsync("UPDATE platform.outbox_messages SET next_attempt_at = now() - interval '1 second' WHERE processed_at IS NULL");

    [Fact]
    public async Task Commit_StoresTheMessageWithItsLineage()
    {
        await using Env env = await StartAsync();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test.outbox",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test.outbox");
        using Activity span = source.StartActivity("request")!;
        var user = Guid.CreateVersion7();
        ThingCreated message = NewMessage("nvda");

        await env.PublishAsync(message, user, "corr-123");

        (await env.Db.Admin.ScalarAsync<string>("SELECT type FROM platform.outbox_messages WHERE id = @id", ("id", message.EventId))).Should().Be("demo.thing_created.v1");
        (await env.Db.Admin.ScalarAsync<string>("SELECT payload->>'name' FROM platform.outbox_messages WHERE id = @id", ("id", message.EventId))).Should().Be("nvda");
        (await env.Db.Admin.ScalarAsync<Guid>("SELECT user_id FROM platform.outbox_messages WHERE id = @id", ("id", message.EventId))).Should().Be(user);
        (await env.Db.Admin.ScalarAsync<string>("SELECT correlation_id FROM platform.outbox_messages WHERE id = @id", ("id", message.EventId))).Should().Be("corr-123");
        (await env.Db.Admin.ScalarAsync<string>("SELECT trace_id FROM platform.outbox_messages WHERE id = @id", ("id", message.EventId))).Should().Be(span.TraceId.ToString());
    }

    [Fact]
    public async Task Commit_AcceptsAnEventTimestampedWithAnyUtcOffset()
    {
        await using Env env = await StartAsync();
        var occurred = new DateTimeOffset(2026, 10, 13, 18, 30, 0, TimeSpan.FromHours(5));
        var message = new ThingCreated(Guid.CreateVersion7(), occurred, "offset");

        await env.PublishAsync(message);

        (await env.Db.Admin.ScalarAsync<DateTime>("SELECT occurred_at AT TIME ZONE 'UTC' FROM platform.outbox_messages"))
            .Should().Be(occurred.UtcDateTime, "the instant is preserved even though PostgreSQL stores only UTC");
    }

    [Fact]
    public async Task Rollback_LeavesNoMessage()
    {
        await using Env env = await StartAsync();
        await using (AsyncServiceScope scope = env.Api.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.BeginAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(NewMessage(), CancellationToken.None);
            await uow.RollbackAsync(CancellationToken.None);
        }

        (await env.Count("SELECT count(*) FROM platform.outbox_messages")).Should().Be(0);
    }

    [Fact]
    public async Task AddingOutsideAUnitOfWork_IsAProgrammingError()
    {
        await using Env env = await StartAsync();
        await using AsyncServiceScope scope = env.Api.CreateAsyncScope();

        Func<Task> act = () => scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(NewMessage(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unit of work*");
    }

    [Fact]
    public async Task AddingTheSameEventTwice_StoresOneMessage()
    {
        await using Env env = await StartAsync();
        ThingCreated message = NewMessage();

        await env.PublishAsync(message);
        await env.PublishAsync(message);

        (await env.Count("SELECT count(*) FROM platform.outbox_messages")).Should().Be(1);
    }

    [Fact]
    public async Task Delivery_RunsEveryConsumerOnce_AndMarksTheMessageProcessed()
    {
        await using Env env = await StartAsync();
        ThingCreated message = NewMessage("widget");
        await env.PublishAsync(message);

        bool first = await env.Processor.ProcessNextAsync(CancellationToken.None);
        bool second = await env.Processor.ProcessNextAsync(CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse("nothing else is due");
        env.Probe.Handled[message.EventId].Should().Be(1);
        (await env.Count("SELECT count(*) FROM public.probe_items")).Should().Be(1);
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NOT NULL")).Should().Be(1);
        (await env.Count("SELECT count(*) FROM platform.inbox_messages")).Should().Be(2, "one inbox mark per consumer");
    }

    [Fact]
    public async Task AFailingConsumer_RollsBackEverythingTheMessageWrote_ThenRetriesWithBackoff()
    {
        await using Env env = await StartAsync();
        ThingCreated message = NewMessage();
        await env.PublishAsync(message);
        env.Probe.FailNext(1);

        await env.Processor.ProcessNextAsync(CancellationToken.None);

        (await env.Count("SELECT count(*) FROM public.probe_items")).Should().Be(0, "the first consumer's write rolled back with the failure");
        (await env.Count("SELECT count(*) FROM platform.inbox_messages")).Should().Be(0);
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NULL AND attempts = 1")).Should().Be(1);
        (await env.Db.Admin.ScalarAsync<string>("SELECT last_error FROM platform.outbox_messages")).Should().Contain("flaky consumer failed");

        await MakeDueAsync(env);
        await env.Processor.ProcessNextAsync(CancellationToken.None);

        env.Probe.Handled[message.EventId].Should().Be(2, "the consumer ran twice, but the first run rolled back with the failure");
        (await env.Count("SELECT count(*) FROM public.probe_items")).Should().Be(1, "so the durable effect happened exactly once");
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task ARetryIsNotClaimedBeforeItsBackoffElapses()
    {
        await using Env env = await StartAsync();
        await env.PublishAsync(NewMessage());
        env.Probe.FailNext(1);
        await env.Processor.ProcessNextAsync(CancellationToken.None);
        await env.Db.Admin.ExecuteAsync("UPDATE platform.outbox_messages SET next_attempt_at = now() + interval '1 hour'");

        bool claimed = await env.Processor.ProcessNextAsync(CancellationToken.None);

        claimed.Should().BeFalse();
    }

    [Fact]
    public async Task AMessageThatKeepsFailing_IsParkedAsDeadAfterTheMaximumAttempts()
    {
        await using Env env = await StartAsync(maxAttempts: 3);
        await env.PublishAsync(NewMessage());
        env.Probe.AlwaysFail = true;

        for (int i = 0; i < 3; i++)
        {
            await MakeDueAsync(env);
            (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        }

        await MakeDueAsync(env);
        (await env.Processor.ProcessNextAsync(CancellationToken.None)).Should().BeFalse("a dead message is no longer claimed");
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NULL AND attempts = 3")).Should().Be(1);
        (await env.Count("SELECT count(*) FROM public.probe_items")).Should().Be(0);
    }

    [Fact]
    public async Task RedeliveringAProcessedMessage_DoesNotRepeatTheEffect()
    {
        await using Env env = await StartAsync();
        ThingCreated message = NewMessage();
        await env.PublishAsync(message);
        await env.Processor.ProcessNextAsync(CancellationToken.None);

        // Simulate a duplicate delivery, as after a crash between the effect and the processed mark in another system.
        await env.Db.Admin.ExecuteAsync("UPDATE platform.outbox_messages SET processed_at = NULL, next_attempt_at = now() - interval '1 second'");
        await env.Processor.ProcessNextAsync(CancellationToken.None);

        env.Probe.Handled[message.EventId].Should().Be(1, "the inbox recognized the redelivery");
        (await env.Count("SELECT count(*) FROM public.probe_items")).Should().Be(1);
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task ParallelWorkers_DeliverEveryMessageExactlyOnce()
    {
        await using Env env = await StartAsync();
        ThingCreated[] messages = [.. Enumerable.Range(0, 30).Select(i => NewMessage($"item-{i}"))];
        foreach (ThingCreated message in messages)
        {
            await env.PublishAsync(message);
        }

        int[] deliveredPerWorker = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            int delivered = 0;
            while (await env.Processor.ProcessNextAsync(CancellationToken.None))
            {
                delivered++;
            }

            return delivered;
        }));

        deliveredPerWorker.Sum().Should().Be(30);

        messages.Should().OnlyContain(m => env.Probe.Handled.GetValueOrDefault(m.EventId) == 1);
        (await env.Count("SELECT count(*) FROM public.probe_items")).Should().Be(30);
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NOT NULL")).Should().Be(30);
        (await env.Count("SELECT count(*) FROM platform.inbox_messages")).Should().Be(60);
    }

    [Fact]
    public async Task AMessageOfAnUnknownType_IsRetriedAndEventuallyParkedWithAClearError()
    {
        await using Env env = await StartAsync(maxAttempts: 2);
        await env.Db.Admin.ExecuteAsync(
            "INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, attempts, next_attempt_at) VALUES (@id, 'demo.removed.v1', '{}'::jsonb, now(), 0, now() - interval '1 second')",
            ("id", Guid.CreateVersion7()));

        await env.Processor.ProcessNextAsync(CancellationToken.None);
        await MakeDueAsync(env);
        await env.Processor.ProcessNextAsync(CancellationToken.None);

        (await env.Db.Admin.ScalarAsync<string>("SELECT last_error FROM platform.outbox_messages")).Should().Contain("Unknown integration event");
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE attempts = 2 AND processed_at IS NULL")).Should().Be(1);
    }

    [Fact]
    public async Task Shutdown_LeavesTheMessagePendingAndUntouched()
    {
        await using Env env = await StartAsync();
        await env.PublishAsync(NewMessage());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => env.Processor.ProcessNextAsync(cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await env.Count("SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NULL AND attempts = 0")).Should().Be(1);
    }

    [Fact]
    public async Task TheDispatcherService_DeliversInTheBackground_AndStopsGracefully()
    {
        await using Env env = await StartAsync();
        ThingCreated message = NewMessage();
        var dispatcher = env.Worker.GetServices<IHostedService>().OfType<OutboxDispatcherService>().Single();

        await dispatcher.StartAsync(CancellationToken.None);
        await env.PublishAsync(message);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (env.Probe.Handled.GetValueOrDefault(message.EventId) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Func<Task> stop = () => dispatcher.StopAsync(CancellationToken.None);

        env.Probe.Handled[message.EventId].Should().Be(1);
        await stop.Should().NotThrowAsync();
    }
}
