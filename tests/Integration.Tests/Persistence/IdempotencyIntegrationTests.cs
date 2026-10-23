using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Demo.IntegrationFeatures;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>The idempotency contract of ADR-0017 against a real PostgreSQL, through the real pipeline.</summary>
[Trait("Category", "Integration")]
public sealed class IdempotencyIntegrationTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _exampleId = "0123456789abcdef-key";

    private static IdempotencyKeyInfo KeyFor(string key = _exampleId, string route = "/v1/items") => new(key, "POST", route);

    private sealed class Host(ServiceProvider provider, MigratedDatabase db) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;

        public MigratedDatabase Db => db;

        public async Task<(Result<ItemDto> Result, bool Replayed)> SendAsync(Guid user, CreateItemCommand command)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TestUser>().Id = user;
            Result<ItemDto> result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, CancellationToken.None);
            return (result, scope.ServiceProvider.GetRequiredService<IIdempotencyContext>().Replayed);
        }

        public async Task<long> ItemsAsync() => await db.Admin.ScalarAsync<long>("SELECT count(*) FROM public.probe_items");

        public async Task<long> KeysAsync() => await db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys");

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private async Task<Host> StartAsync(string? sessionOptions = null)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Admin.ExecuteAsync("CREATE TABLE public.probe_items (id uuid PRIMARY KEY, name text NOT NULL)");
        await db.Admin.ExecuteAsync("GRANT SELECT, INSERT ON public.probe_items TO api_role");

        string api = sessionOptions is null ? db.Api : new NpgsqlConnectionStringBuilder(db.Api) { Options = sessionOptions }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = api })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HandlerProbe>();
        services.AddScoped<TestUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUser>());
        services.AddApplicationPipeline(typeof(CreateItemHandler).Assembly);
        services.AddSakerPostgres(configuration);
        services.AddIdempotency(configuration);
        services.AddDbContext<ProbeItemsContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<DbSession>().Connection)
            .AddInterceptors(
                provider.GetRequiredService<UserContextConnectionInterceptor>(),
                provider.GetRequiredService<AmbientTransactionInterceptor>()));
        return new Host(services.BuildServiceProvider(), db);
    }

    [Fact]
    public async Task ParallelIdenticalRequests_ProduceExactlyOneEffect()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();
        CreateItemCommand command = new(KeyFor(), "widget");

        (Result<ItemDto> Result, bool Replayed)[] outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => host.SendAsync(user, command)));

        outcomes.Should().OnlyContain(o => o.Result.IsSuccess);
        outcomes.Select(o => o.Result.Value.Id).Distinct().Should().HaveCount(1, "every caller sees the first attempt's result");
        outcomes.Count(o => o.Replayed).Should().Be(19);
        (await host.ItemsAsync()).Should().Be(1);
        host.Provider.GetRequiredService<HandlerProbe>().Executions.Should().Be(1);
        (await host.KeysAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RepeatedRequest_ReplaysTheStoredResultWithoutRunningTheHandler()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();
        CreateItemCommand command = new(KeyFor(), "widget");

        (Result<ItemDto> first, bool firstReplayed) = await host.SendAsync(user, command);
        (Result<ItemDto> second, bool secondReplayed) = await host.SendAsync(user, command);

        firstReplayed.Should().BeFalse();
        secondReplayed.Should().BeTrue();
        second.Value.Should().Be(first.Value);
        host.Provider.GetRequiredService<HandlerProbe>().Executions.Should().Be(1);
        (await host.ItemsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SameKeyWithADifferentPayload_IsRejectedAndHasNoEffect()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();
        await host.SendAsync(user, new CreateItemCommand(KeyFor(), "widget"));

        (Result<ItemDto> result, bool replayed) = await host.SendAsync(user, new CreateItemCommand(KeyFor(), "gadget"));

        result.Error!.Code.Should().Be(IdempotencyErrors.KeyReuse);
        result.Error.Type.Should().Be(ErrorType.Unprocessable);
        replayed.Should().BeFalse();
        (await host.ItemsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SameKeyOnAnotherRoute_IsADifferentOperation()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();

        await host.SendAsync(user, new CreateItemCommand(KeyFor(route: "/v1/items"), "widget"));
        (Result<ItemDto> second, bool replayed) = await host.SendAsync(user, new CreateItemCommand(KeyFor(route: "/v1/other"), "widget"));

        second.IsSuccess.Should().BeTrue();
        replayed.Should().BeFalse();
        (await host.ItemsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task SameKeyFromAnotherUser_IsAnIndependentOperation()
    {
        await using Host host = await StartAsync();

        await host.SendAsync(Guid.CreateVersion7(), new CreateItemCommand(KeyFor(), "widget"));
        (Result<ItemDto> second, bool replayed) = await host.SendAsync(Guid.CreateVersion7(), new CreateItemCommand(KeyFor(), "widget"));

        second.IsSuccess.Should().BeTrue();
        replayed.Should().BeFalse();
        (await host.ItemsAsync()).Should().Be(2);
        (await host.KeysAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CrashAfterTheEffect_LeavesNothingBehind_AndTheRetryRunsOnce()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();

        Func<Task> crash = async () => await host.SendAsync(user, new CreateItemCommand(KeyFor(), "widget", ThrowAfterInsert: true));
        await crash.Should().ThrowAsync<InvalidOperationException>();

        (await host.ItemsAsync()).Should().Be(0, "the effect rolled back with the key");
        (await host.KeysAsync()).Should().Be(0);

        (Result<ItemDto> retry, bool replayed) = await host.SendAsync(user, new CreateItemCommand(KeyFor(), "widget"));
        retry.IsSuccess.Should().BeTrue();
        replayed.Should().BeFalse();
        (await host.ItemsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FailureResult_IsNotStored_SoARetryReevaluates()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();

        (Result<ItemDto> failed, _) = await host.SendAsync(user, new CreateItemCommand(KeyFor(), "widget", ReturnFailure: true));
        failed.Error!.Code.Should().Be("demo.item_exists");
        (await host.ItemsAsync()).Should().Be(0);
        (await host.KeysAsync()).Should().Be(0);

        // Same key, same payload, but the second attempt succeeds: nothing was stored from the failure.
        (Result<ItemDto> retry, bool replayed) = await host.SendAsync(user, new CreateItemCommand(KeyFor(), "widget", ReturnFailure: true) with { ReturnFailure = false });
        retry.IsSuccess.Should().BeTrue();
        replayed.Should().BeFalse();
    }

    [Fact]
    public async Task ADuplicateWhileTheFirstIsStillRunning_GetsAConflict_ThenTheFirstCompletesOnce()
    {
        await using Host host = await StartAsync(sessionOptions: "-c lock_timeout=500");
        var user = Guid.CreateVersion7();
        var probe = host.Provider.GetRequiredService<HandlerProbe>();
        probe.HoldNextExecution();
        CreateItemCommand command = new(KeyFor(), "widget");

        Task<(Result<ItemDto> Result, bool Replayed)> first = host.SendAsync(user, command);
        await probe.EnteredFirstExecution;
        (Result<ItemDto> duplicate, _) = await host.SendAsync(user, command);

        duplicate.Error!.Code.Should().Be(IdempotencyErrors.InProgress);
        duplicate.Error.Type.Should().Be(ErrorType.Conflict);

        probe.Release();
        (Result<ItemDto> completed, _) = await first;
        completed.IsSuccess.Should().BeTrue();
        (await host.ItemsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ADuplicateWhileTheFirstIsStillRunning_OnALoginWithNoLockTimeout_IsStillAnsweredAsInProgress()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();
        var probe = host.Provider.GetRequiredService<HandlerProbe>();
        probe.HoldNextExecution();
        CreateItemCommand command = new(KeyFor(), "widget");

        Task<(Result<ItemDto> Result, bool Replayed)> first = host.SendAsync(user, command);
        await probe.EnteredFirstExecution;
        Task<(Result<ItemDto> Result, bool Replayed)> duplicate = host.SendAsync(user, command);

        (await duplicate.WaitAsync(TimeSpan.FromSeconds(20))).Result.Error!.Code.Should().Be(IdempotencyErrors.InProgress, "the claim bounds its own wait");
        probe.Release();
        (await first).Result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AnExpiredKey_CanBePurged_AndThenTheOperationRunsAgain()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();
        CreateItemCommand command = new(KeyFor(), "widget");
        await host.SendAsync(user, command);

        await host.Db.Admin.ExecuteAsync("UPDATE platform.idempotency_keys SET expires_at = now() - interval '1 hour'");
        int purged = await host.Db.Worker.AsUserScalarAsync<int>(null, "SELECT platform.purge_expired_idempotency_keys(now(), 100)");
        (Result<ItemDto> again, bool replayed) = await host.SendAsync(user, command);

        purged.Should().Be(1);
        again.IsSuccess.Should().BeTrue();
        replayed.Should().BeFalse();
        (await host.ItemsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AFirstResponseTooLargeToStore_IsReplayedAsAConflictPointingToAGet()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();
        CreateItemCommand command = new(KeyFor(), new string('x', ResultSnapshot.MaxBodyBytes + 100));

        (Result<ItemDto> first, _) = await host.SendAsync(user, command);
        (Result<ItemDto> second, bool replayed) = await host.SendAsync(user, command);

        first.IsSuccess.Should().BeTrue();
        replayed.Should().BeTrue();
        second.Error!.Code.Should().Be(IdempotencyErrors.ReplayUnavailable);
        (await host.ItemsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CommandsWithoutAKey_AreUnaffected()
    {
        await using Host host = await StartAsync();
        var user = Guid.CreateVersion7();

        await using AsyncServiceScope scope = host.Provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUser>().Id = user;
        Result<ItemDto> result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PlainCreateItemCommand("plain"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await host.KeysAsync()).Should().Be(0);
        (await host.ItemsAsync()).Should().Be(1);
    }
}
