using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class UserContextInterceptorTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);

    private sealed class FixedUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    // A pool of one connection forces every context to reuse the same physical session.
    private static async Task<string> ReadSettingAsync(string connectionString, ICurrentUser user)
    {
        string pooled = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true, MaxPoolSize = 1 }.ConnectionString;
        var options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseNpgsql(pooled)
            .AddInterceptors(new UserContextConnectionInterceptor(user))
            .Options;

        await using var context = new ProbeDbContext(options);
        return await context.Database.SqlQuery<string>($"SELECT current_setting('app.current_user_id', true) AS \"Value\"").SingleAsync();
    }

    [Fact]
    public async Task Open_WithAuthenticatedUser_SetsTheSessionVariable()
    {
        var userId = Guid.CreateVersion7();

        string value = await ReadSettingAsync(postgres.ConnectionString, new FixedUser(userId));

        value.Should().Be(userId.ToString("D"));
    }

    [Fact]
    public async Task Open_WithoutUser_LeavesTheVariableEmpty()
    {
        string value = await ReadSettingAsync(postgres.ConnectionString, new AnonymousCurrentUser());

        value.Should().BeEmpty();
    }

    [Fact]
    public async Task PooledConnection_DoesNotCarryThePreviousUserIntoTheNextRequest()
    {
        var first = new FixedUser(Guid.CreateVersion7());

        string asUser = await ReadSettingAsync(postgres.ConnectionString, first);
        string afterwards = await ReadSettingAsync(postgres.ConnectionString, new AnonymousCurrentUser());

        asUser.Should().NotBeEmpty();
        afterwards.Should().BeEmpty("the interceptor resets the variable on every open");
    }
}
