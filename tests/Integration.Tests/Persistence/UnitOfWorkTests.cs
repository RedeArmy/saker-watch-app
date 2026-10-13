using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class UnitOfWorkTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    /// <summary>A second module context, standing in for another module's schema.</summary>
    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options)
    {
        public DbSet<ProbeItem> Items => Set<ProbeItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ProbeItem>(e =>
            {
                e.ToTable("probe_items", "public");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Name).HasColumnName("name");
            });
    }

    private sealed class ProbeItem
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class FixedUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private sealed record TestHost(ServiceProvider Provider, MigratedDatabase Db) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private async Task<TestHost> StartAsync(Guid? user = null)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Admin.ExecuteAsync("CREATE TABLE public.probe_items (id uuid PRIMARY KEY, name text NOT NULL)");
        await db.Admin.ExecuteAsync("GRANT SELECT, INSERT ON public.probe_items TO api_role");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = db.Api })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FixedUser(user));
        services.AddSakerPostgres(configuration);
        services.AddSakerDbContext<PlatformDbContext>(PlatformDbContext.Schema);
        services.AddDbContext<ProbeDbContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<DbSession>().Connection)
            .AddInterceptors(
                provider.GetRequiredService<UserContextConnectionInterceptor>(),
                provider.GetRequiredService<AmbientTransactionInterceptor>()));
        return new TestHost(services.BuildServiceProvider(), db);
    }

    private static OutboxMessageRow Outbox() => new()
    {
        Id = Guid.CreateVersion7(),
        Type = "demo.happened",
        OccurredAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow,
    };

    private static ProbeItem Probe() => new() { Id = Guid.CreateVersion7(), Name = "item" };

    private static async Task SaveBothAsync(IServiceProvider scope)
    {
        scope.GetRequiredService<PlatformDbContext>().OutboxMessages.Add(Outbox());
        scope.GetRequiredService<ProbeDbContext>().Items.Add(Probe());
        await scope.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        await scope.GetRequiredService<ProbeDbContext>().SaveChangesAsync();
    }

    private static async Task<(long Outbox, long Probe)> CountsAsync(MigratedDatabase db) =>
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages"),
         await db.Admin.ScalarAsync<long>("SELECT count(*) FROM public.probe_items"));

    [Fact]
    public async Task Commit_PersistsWritesOfEveryContextTogether()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        MigratedDatabase db = host.Db;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.BeginAsync(CancellationToken.None);
            await SaveBothAsync(scope.ServiceProvider);

            (await CountsAsync(db)).Should().Be((0, 0), "nothing is visible to others before commit");
            await uow.CommitAsync(CancellationToken.None);
        }

        (await CountsAsync(db)).Should().Be((1, 1));
    }

    [Fact]
    public async Task Rollback_UndoesWritesOfEveryContextTogether()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        MigratedDatabase db = host.Db;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.BeginAsync(CancellationToken.None);
            await SaveBothAsync(scope.ServiceProvider);
            await uow.RollbackAsync(CancellationToken.None);
        }

        (await CountsAsync(db)).Should().Be((0, 0));
    }

    [Fact]
    public async Task ScopeDisposedMidUnit_NeverCommitsByAccident()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        MigratedDatabase db = host.Db;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(CancellationToken.None);
            await SaveBothAsync(scope.ServiceProvider);
        }

        (await CountsAsync(db)).Should().Be((0, 0));
    }

    [Fact]
    public async Task NestedUnits_CommitOnlyWhenTheOutermostCommits()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        MigratedDatabase db = host.Db;
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.BeginAsync(CancellationToken.None);
        await uow.BeginAsync(CancellationToken.None);
        await SaveBothAsync(scope.ServiceProvider);
        await uow.CommitAsync(CancellationToken.None);
        (await CountsAsync(db)).Should().Be((0, 0), "the inner commit does not commit");

        await uow.CommitAsync(CancellationToken.None);
        (await CountsAsync(db)).Should().Be((1, 1));
    }

    [Fact]
    public async Task NestedRollback_PreventsTheOuterCommit()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        MigratedDatabase db = host.Db;
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.BeginAsync(CancellationToken.None);
        await uow.BeginAsync(CancellationToken.None);
        await SaveBothAsync(scope.ServiceProvider);
        await uow.RollbackAsync(CancellationToken.None);
        Func<Task> outer = () => uow.CommitAsync(CancellationToken.None);

        await outer.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nested*");
        (await CountsAsync(db)).Should().Be((0, 0));
    }

    [Fact]
    public async Task Commit_WithoutBegin_IsAProgrammingError()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Func<Task> act = () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Rollback_WithoutBegin_IsHarmless()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Func<Task> act = () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RollbackAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OutsideAUnitOfWork_SaveChangesStillAutoCommits()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        MigratedDatabase db = host.Db;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            await SaveBothAsync(scope.ServiceProvider);
        }

        (await CountsAsync(db)).Should().Be((1, 1));
    }

    [Fact]
    public async Task UserContext_IsAppliedInsideAndOutsideTheTransaction()
    {
        var user = Guid.CreateVersion7();
        await using TestHost host = await StartAsync(user);
        ServiceProvider provider = host.Provider;
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var probe = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
        FormattableString query = $"SELECT current_setting('app.current_user_id', true) AS \"Value\"";

        await uow.BeginAsync(CancellationToken.None);
        string inside = await probe.Database.SqlQuery<string>(query).SingleAsync();
        await uow.CommitAsync(CancellationToken.None);
        string after = await probe.Database.SqlQuery<string>(query).SingleAsync();

        inside.Should().Be(user.ToString("D"));
        after.Should().Be(user.ToString("D"), "the connection is re-tagged when EF reopens it");
    }

    [Fact]
    public async Task UnitOfWork_ResolvesToTheScopesDatabaseSession()
    {
        await using TestHost host = await StartAsync();
        ServiceProvider provider = host.Provider;
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Should().BeSameAs(scope.ServiceProvider.GetRequiredService<DbSession>());
    }
}
