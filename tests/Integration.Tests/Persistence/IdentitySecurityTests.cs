using AwesomeAssertions;
using Npgsql;
using SakerWatch.Hosts.Migrator;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class IdentitySecurityTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _provision =
        "SELECT user_id FROM identity.provision_user(@id, @issuer, @sub, @email, @name, 'es-GT', now())";

    private const string _issuer = "https://clavaris.example/o/org-1";

    private static (string Name, object? Value)[] ProvisionParams(Guid id, string subject, string? email = null, string issuer = _issuer) =>
        [("id", id), ("issuer", issuer), ("sub", subject), ("email", email), ("name", null)];

    private static async Task ExpectDeniedAsync(Func<Task> action, string sqlState = "42501")
    {
        PostgresException error = (await action.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(sqlState);
    }

    [Fact]
    public async Task Provision_NewIdentity_CreatesTheUserWithTheGivenId()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var newId = Guid.CreateVersion7();

        var created = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(newId, "sub-1", "a@example.com"));

        created.Should().Be(newId);
        (await db.Admin.ScalarAsync<string>("SELECT status FROM identity.users WHERE id = @id", ("id", newId))).Should().Be("active");
    }

    [Fact]
    public async Task Provision_KnownIdentity_ReturnsTheExistingUserAndRefreshesProfile()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var firstId = Guid.CreateVersion7();
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(firstId, "sub-1", "old@example.com"));

        var again = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1", "new@example.com"));

        again.Should().Be(firstId, "the stored id wins; the new id is only a candidate");
        (await db.Admin.ScalarAsync<string>("SELECT email FROM identity.users WHERE id = @id", ("id", firstId))).Should().Be("new@example.com");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
    }

    [Fact]
    public async Task Provision_KnownIdentityWithUnchangedProfile_DoesNotRewriteTheRow()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(id, "sub-1", "same@example.com"));
        string? before = await db.Admin.ScalarAsync<string>("SELECT xmin::text FROM identity.users WHERE id = @id", ("id", id));

        for (int i = 0; i < 3; i++)
        {
            await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1", "same@example.com"));
        }

        (await db.Admin.ScalarAsync<string>("SELECT xmin::text FROM identity.users WHERE id = @id", ("id", id))).Should().Be(before, "an unchanged login is a read, not an update");
    }

    [Fact]
    public async Task Provision_KnownIdentityWithAChangedProfile_UpdatesTheRowAndKeepsUnsentFields()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(id, "sub-1", "old@example.com"));
        string? before = await db.Admin.ScalarAsync<string>("SELECT xmin::text FROM identity.users WHERE id = @id", ("id", id));

        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1", "new@example.com"));
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1", null));

        (await db.Admin.ScalarAsync<string>("SELECT email FROM identity.users WHERE id = @id", ("id", id))).Should().Be("new@example.com", "a token without an email does not erase the stored one");
        (await db.Admin.ScalarAsync<string>("SELECT xmin::text FROM identity.users WHERE id = @id", ("id", id))).Should().NotBe(before);
    }

    [Fact]
    public async Task Provision_AccountPendingDeletion_IsReturnedWithoutBeingEdited()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(id, "sub-1", "kept@example.com"));
        await db.Admin.ExecuteAsync("UPDATE identity.users SET status = 'pending_deletion' WHERE id = @id", ("id", id));

        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1", "changed@example.com"));

        (await db.Admin.ScalarAsync<string>("SELECT email FROM identity.users WHERE id = @id", ("id", id))).Should().Be("kept@example.com");
        (await db.Admin.ScalarAsync<string>("SELECT status FROM identity.users WHERE id = @id", ("id", id))).Should().Be("pending_deletion");
    }

    [Fact]
    public async Task UpgradeFromTheEveryLoginUpdateVersion_KeepsGrantsAndBehavior()
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var identity = MigrationSteps.IdentityStep(admin);
        await identity.MigrateToAsync("ProvisionUserWithStatus", CancellationToken.None);
        var existing = Guid.CreateVersion7();
        await admin.ExecuteAsync(
            "SELECT * FROM identity.provision_user(@id, 'i', 's', 'a@example.com', NULL, 'es-GT', now())",
            ("id", existing));

        await identity.RunAsync(CancellationToken.None);
        string api = await TimescalePostgresFixture.CreateRoleLoginAsync(admin, "api_role");

        var again = await api.AsUserScalarAsync<Guid>(null, "SELECT user_id FROM identity.provision_user(@id, 'i', 's', 'a@example.com', NULL, 'es-GT', now())", ("id", Guid.CreateVersion7()));
        again.Should().Be(existing, "the api role can still execute the replaced function and the existing user is found");
        (await api.AsUserScalarAsync<bool>(null, "SELECT was_created FROM identity.provision_user(@id, 'i', 'new-sub', NULL, NULL, 'es-GT', now())", ("id", Guid.CreateVersion7()))).Should().BeTrue();
    }

    [Fact]
    public async Task Provision_SameSubjectUnderAnotherIssuer_IsADifferentUser()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        var one = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1"));
        var two = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-1", issuer: "https://clavaris.example/o/org-2"));

        two.Should().NotBe(one);
    }

    [Fact]
    public async Task Provision_UnderParallelFirstLogins_CreatesExactlyOneUser()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        Guid[] ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "sub-race"))));

        ids.Distinct().Should().HaveCount(1);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
    }

    [Fact]
    public async Task Api_SeesOnlyItsOwnUser_EvenWithAnUnfilteredQuery()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "alice"));
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "bob"));

        long visible = await db.Api.AsUserScalarAsync<long>(alice, "SELECT count(*) FROM identity.users");
        string? subject = await db.Api.AsUserScalarAsync<string>(alice, "SELECT external_subject FROM identity.users");

        visible.Should().Be(1);
        subject.Should().Be("alice");
    }

    [Fact]
    public async Task Api_WithoutAUserContext_SeesNoUsers()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "alice"));

        (await db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM identity.users")).Should().Be(0);
    }

    [Fact]
    public async Task Api_CannotChangeAnotherUsersProfile()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "alice"));
        var bob = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "bob", "bob@example.com"));

        int hit = await db.Api.AsUserExecuteAsync(alice, "UPDATE identity.users SET email = 'owned@example.com' WHERE id = @bob", ("bob", bob));

        hit.Should().Be(0);
        (await db.Admin.ScalarAsync<string>("SELECT email FROM identity.users WHERE id = @bob", ("bob", bob))).Should().Be("bob@example.com");
    }

    [Fact]
    public async Task Api_CannotInsertOrDeleteUsersDirectly()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "alice"));

        await ExpectDeniedAsync(() => db.Api.AsUserExecuteAsync(alice, "DELETE FROM identity.users"));
        await ExpectDeniedAsync(() => db.Api.AsUserExecuteAsync(
            alice,
            "INSERT INTO identity.users (id, external_issuer, external_subject, status, created_at, updated_at, locale) VALUES (@id, 'i', 's', 'active', now(), now(), 'es-GT')",
            ("id", alice)));
    }

    [Fact]
    public async Task Api_CannotReadTombstones_AndWorkerHasNoAccessToUsers()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectDeniedAsync(() => db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM identity.deletion_tombstones"));
        await ExpectDeniedAsync(() => db.Worker.AsUserScalarAsync<long>(null, "SELECT count(*) FROM identity.users"));
        await ExpectDeniedAsync(() => db.Worker.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "x")));
    }

    [Fact]
    public async Task InvalidStatus_IsRejectedByTheCheckConstraint()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = await db.Api.AsUserScalarAsync<Guid>(null, _provision, ProvisionParams(Guid.CreateVersion7(), "alice"));

        await ExpectDeniedAsync(() => db.Admin.ExecuteAsync("UPDATE identity.users SET status = 'zombie' WHERE id = @id", ("id", id)), "23514");
    }

    [Fact]
    public async Task UpgradeFromThePreviousVersion_KeepsUsersAndAppliesRowLevelSecurity()
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var identity = MigrationSteps.IdentityStep(admin);
        await identity.MigrateToAsync("CreateUsers", CancellationToken.None);
        var legacy = Guid.CreateVersion7();
        await admin.ExecuteAsync(
            "INSERT INTO identity.users (id, external_issuer, external_subject, locale, status, created_at, updated_at) VALUES (@id, 'i', 's', 'es-GT', 'active', now(), now())",
            ("id", legacy));

        await identity.RunAsync(CancellationToken.None);
        string api = await TimescalePostgresFixture.CreateRoleLoginAsync(admin, "api_role");

        (await admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
        (await api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM identity.users")).Should().Be(0, "the legacy user is now protected");
        (await api.AsUserScalarAsync<long>(legacy, "SELECT count(*) FROM identity.users")).Should().Be(1);
    }
}
