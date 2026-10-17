using AwesomeAssertions;
using Npgsql;
using SakerWatch.Hosts.Migrator;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PlatformSecurityTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _insertKey = """
        INSERT INTO platform.idempotency_keys (user_id, method, route, key, request_hash, state, created_at, expires_at)
        VALUES (@u, 'POST', '/v1/things', @k, @h, 'in_progress', @created, @expires)
        """;

    private const string _insertOutbox = """
        INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, attempts, next_attempt_at)
        VALUES (@id, 'demo.happened', '{}'::jsonb, now(), 0, now())
        """;

    private static (string Name, object? Value)[] KeyParams(Guid user, string key, DateTimeOffset expires) =>
        [("u", user), ("k", key), ("h", new byte[] { 1, 2, 3 }), ("created", DateTimeOffset.UtcNow), ("expires", expires)];

    private static async Task ExpectDeniedAsync(Func<Task> action, string sqlState = "42501")
    {
        PostgresException error = (await action.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(sqlState);
    }

    [Fact]
    public async Task Api_InsertsIdempotencyKey_ForTheCurrentUser()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();

        int rows = await db.Api.AsUserExecuteAsync(user, _insertKey, KeyParams(user, "key-1", DateTimeOffset.UtcNow.AddDays(1)));

        rows.Should().Be(1);
    }

    [Fact]
    public async Task Api_CannotInsertIdempotencyKey_ForAnotherUser()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var me = Guid.CreateVersion7();
        var someoneElse = Guid.CreateVersion7();

        await ExpectDeniedAsync(
            () => db.Api.AsUserExecuteAsync(me, _insertKey, KeyParams(someoneElse, "key-1", DateTimeOffset.UtcNow.AddDays(1))));
    }

    [Fact]
    public async Task Api_WithAnUnfilteredQuery_StillSeesOnlyItsOwnKeys()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(alice, "a-key", DateTimeOffset.UtcNow.AddDays(1)));
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(bob, "b-key", DateTimeOffset.UtcNow.AddDays(1)));

        // No WHERE clause: this is what a bug that forgets the application-level filter would send.
        long asAlice = await db.Api.AsUserScalarAsync<long>(alice, "SELECT count(*) FROM platform.idempotency_keys");
        string? key = await db.Api.AsUserScalarAsync<string>(alice, "SELECT key FROM platform.idempotency_keys");

        asAlice.Should().Be(1);
        key.Should().Be("a-key");
    }

    [Fact]
    public async Task Api_CannotUpdateOrDeleteAnotherUsersKeys()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(bob, "b-key", DateTimeOffset.UtcNow.AddDays(1)));

        int updated = await db.Api.AsUserExecuteAsync(alice, "UPDATE platform.idempotency_keys SET state = 'completed'");
        int deleted = await db.Api.AsUserExecuteAsync(alice, "DELETE FROM platform.idempotency_keys");

        updated.Should().Be(0);
        deleted.Should().Be(0);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys WHERE state = 'in_progress'")).Should().Be(1);
    }

    [Fact]
    public async Task Api_WithoutAUserContext_SeesNoKeys()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var bob = Guid.CreateVersion7();
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(bob, "b-key", DateTimeOffset.UtcNow.AddDays(1)));

        (await db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.idempotency_keys")).Should().Be(0);
    }

    [Fact]
    public async Task Worker_CannotReadIdempotencyKeys()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectDeniedAsync(() => db.Worker.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.idempotency_keys"));
    }

    [Fact]
    public async Task Worker_PurgesExpiredKeysAcrossUsers_ThroughTheFunctionOnly()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(alice, "old-a", now.AddHours(-3)));
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(bob, "old-b", now.AddHours(-2)));
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(bob, "fresh-b", now.AddHours(5)));

        int purged = await db.Worker.AsUserScalarAsync<int>(null, "SELECT platform.purge_expired_idempotency_keys(@before, 100)", ("before", now));

        purged.Should().Be(2);
        (await db.Admin.ScalarAsync<string>("SELECT key FROM platform.idempotency_keys")).Should().Be("fresh-b");
    }

    [Fact]
    public async Task Worker_CanRemoveEveryIdempotencyKeyOfOneUser_AndNobodyElses()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(alice, "a-1", now.AddHours(5)));
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(alice, "a-2", now.AddHours(-5)));
        await db.Admin.ExecuteAsync(_insertKey, KeyParams(bob, "b-1", now.AddHours(5)));

        int removed = await db.Worker.AsUserScalarAsync<int>(null, "SELECT platform.purge_user_idempotency_keys(@user)", ("user", alice));

        removed.Should().Be(2, "fresh and expired keys alike");
        (await db.Admin.ScalarAsync<string>("SELECT key FROM platform.idempotency_keys")).Should().Be("b-1");
        await ExpectDeniedAsync(() => db.Api.AsUserScalarAsync<int>(bob, "SELECT platform.purge_user_idempotency_keys(@user)", ("user", bob)));
    }

    [Fact]
    public async Task TheJobQueue_RefusesAStateNothingProduces()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        const string insert = "INSERT INTO platform.job_queue (id, job_key, type, payload, state, attempts, max_attempts, available_at, created_at) VALUES (@id, @key, 'demo.compute.v1', '{}'::jsonb, @state, 0, 5, now(), now())";

        foreach (string state in new[] { "pending", "leased", "succeeded", "dead" })
        {
            (await db.Admin.ExecuteAsync(insert, ("id", Guid.CreateVersion7()), ("key", $"k-{state}"), ("state", state))).Should().Be(1, state);
        }

        await ExpectDeniedAsync(() => db.Admin.ExecuteAsync(insert, ("id", Guid.CreateVersion7()), ("key", "k-failed"), ("state", "failed")), "23514");
    }

    [Fact]
    public async Task UpgradeFromTheVersionThatStillAllowedFailed_KeepsTheJobs()
    {
        string admin = await postgres.CreateDatabaseAsync();
        var platform = MigrationSteps.PlatformStep(admin);
        await platform.MigrateToAsync("PurgeUserIdempotencyKeys", CancellationToken.None);
        await admin.ExecuteAsync(
            "INSERT INTO platform.job_queue (id, job_key, type, payload, state, attempts, max_attempts, available_at, created_at) VALUES (@id, 'old-job', 'demo.compute.v1', '{}'::jsonb, 'pending', 0, 5, now(), now())",
            ("id", Guid.CreateVersion7()));

        await platform.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE job_key = 'old-job'")).Should().Be(1);
        await ExpectDeniedAsync(() => admin.ExecuteAsync("UPDATE platform.job_queue SET state = 'failed'"), "23514");
    }

    [Fact]
    public async Task UpgradeFromTheVersionWithoutJobUsers_KeepsTheJobsAndReplacesTheEnqueueFunction()
    {
        string admin = await postgres.CreateDatabaseAsync();
        var platform = MigrationSteps.PlatformStep(admin);
        await platform.MigrateToAsync("RemoveUnusedFailedJobState", CancellationToken.None);
        await admin.ExecuteAsync(
            "INSERT INTO platform.job_queue (id, job_key, type, payload, state, attempts, max_attempts, available_at, created_at) VALUES (@id, 'before-upgrade', 'demo.compute.v1', '{}'::jsonb, 'pending', 0, 5, now(), now())",
            ("id", Guid.CreateVersion7()));

        await platform.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE job_key = 'before-upgrade' AND user_id IS NULL")).Should().Be(1, "existing jobs are system jobs");
        (await admin.ScalarAsync<string>("SELECT to_regprocedure('platform.enqueue_job(uuid,text,text,jsonb,timestamptz,integer,text,text,timestamptz)')::text")).Should().BeNull("the old signature is gone, so nothing can bypass the new rule");
        (await admin.ScalarAsync<string>("SELECT to_regprocedure('platform.enqueue_job(uuid,text,text,jsonb,timestamptz,integer,text,text,timestamptz,uuid)')::text")).Should().NotBeNull();
    }

    [Fact]
    public async Task Api_CannotRunThePurgeFunction()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectDeniedAsync(
            () => db.Api.AsUserScalarAsync<int>(Guid.CreateVersion7(), "SELECT platform.purge_expired_idempotency_keys(now(), 10)"));
    }

    [Fact]
    public async Task Api_CanWriteTheOutbox_ButNeitherReadNorChangeIt()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();

        (await db.Api.AsUserExecuteAsync(Guid.CreateVersion7(), _insertOutbox, ("id", id))).Should().Be(1);

        await ExpectDeniedAsync(() => db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.outbox_messages"));
        await ExpectDeniedAsync(() => db.Api.AsUserExecuteAsync(null, "UPDATE platform.outbox_messages SET attempts = 9"));
        (await db.Worker.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.outbox_messages")).Should().Be(1);
    }

    [Fact]
    public async Task Api_HasNoDirectAccessToTheJobQueueOrInbox()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectDeniedAsync(() => db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.job_queue"));
        await ExpectDeniedAsync(() => db.Api.AsUserExecuteAsync(null, "DELETE FROM platform.job_queue"));
        await ExpectDeniedAsync(() => db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.inbox_messages"));
    }

    [Fact]
    public async Task Inbox_RejectsTheSameMessageForTheSameConsumer()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var messageId = Guid.CreateVersion7();
        const string sql = "INSERT INTO platform.inbox_messages (consumer, message_id, processed_at) VALUES ('watchlist', @m, now())";

        await db.Worker.AsUserExecuteAsync(null, sql, ("m", messageId));

        await ExpectDeniedAsync(() => db.Worker.AsUserExecuteAsync(null, sql, ("m", messageId)), "23505");
        (await db.Worker.AsUserExecuteAsync(null, "INSERT INTO platform.inbox_messages (consumer, message_id, processed_at) VALUES ('analysis', @m, now())", ("m", messageId))).Should().Be(1);
    }

    [Fact]
    public async Task EnqueueJob_WithTheSameKeyTwice_CreatesOneJob()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        async Task<bool> Enqueue() => await db.Api.AsUserScalarAsync<bool>(
            Guid.CreateVersion7(),
            "SELECT platform.enqueue_job(@id, 'analysis:NVDA:v1:2026-10-12', 'analysis', '{}'::jsonb, now(), 5, null, null, now(), null)",
            ("id", Guid.CreateVersion7()));

        bool first = await Enqueue();
        bool second = await Enqueue();

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(1);
    }

    [Fact]
    public async Task EnqueueJob_UnderParallelDuplicates_CreatesExactlyOneJob()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => db.Api.AsUserScalarAsync<bool>(
            Guid.CreateVersion7(),
            "SELECT platform.enqueue_job(@id, 'ingest:XNAS:2026-10-12', 'ingest', '{}'::jsonb, now(), 5, null, null, now(), null)",
            ("id", Guid.CreateVersion7()))));

        results.Count(created => created).Should().Be(1);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(1);
    }

    [Fact]
    public async Task AppendAudit_LinksEntriesIntoAVerifiableChain()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        const string sql = "SELECT platform.append_audit(now(), @e, 'actor-hmac', null, '{}'::jsonb, 'trace', 'corr')";

        long first = await db.Api.AsUserScalarAsync<long>(Guid.CreateVersion7(), sql, ("e", "user.provisioned"));
        long second = await db.Worker.AsUserScalarAsync<long>(null, sql, ("e", "config.changed"));

        second.Should().BeGreaterThan(first);
        (await db.Admin.ScalarAsync<bool>("SELECT (SELECT prev_hash FROM platform.audit_log WHERE id = @b) = (SELECT hash FROM platform.audit_log WHERE id = @a)", ("a", first), ("b", second)))
            .Should().BeTrue();
        (await db.Worker.AsUserScalarAsync<long?>(null, "SELECT platform.verify_audit_chain()")).Should().BeNull();
    }

    [Fact]
    public async Task AuditLog_HasNoDirectAccessForTheApplicationRoles()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectDeniedAsync(() => db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.audit_log"));
        await ExpectDeniedAsync(() => db.Worker.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.audit_log"));
        await ExpectDeniedAsync(() => db.Api.AsUserExecuteAsync(null, "INSERT INTO platform.audit_log (occurred_at, event_type, actor_ref, details, hash) VALUES (now(), 'x', 'y', '{}'::jsonb, '\\x00')"));
    }

    [Fact]
    public async Task AuditLog_IsAppendOnly_EvenForTheOwner()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Api.AsUserScalarAsync<long>(Guid.CreateVersion7(), "SELECT platform.append_audit(now(), 'user.provisioned', 'actor', null, '{}'::jsonb, null, null)");

        await ExpectDeniedAsync(() => db.Admin.ExecuteAsync("UPDATE platform.audit_log SET event_type = 'tampered'"));
        await ExpectDeniedAsync(() => db.Admin.ExecuteAsync("DELETE FROM platform.audit_log"));
        await ExpectDeniedAsync(() => db.Admin.ExecuteAsync("TRUNCATE platform.audit_log"));
    }

    [Fact]
    public async Task VerifyAuditChain_DetectsAnEntryChangedBehindTheTriggersBack()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        const string sql = "SELECT platform.append_audit(now(), @e, 'actor', null, '{}'::jsonb, null, null)";
        await db.Api.AsUserScalarAsync<long>(Guid.CreateVersion7(), sql, ("e", "one"));
        long tampered = await db.Api.AsUserScalarAsync<long>(Guid.CreateVersion7(), sql, ("e", "two"));
        await db.Api.AsUserScalarAsync<long>(Guid.CreateVersion7(), sql, ("e", "three"));

        await db.Admin.ExecuteAsync("ALTER TABLE platform.audit_log DISABLE TRIGGER audit_log_no_update_delete");
        await db.Admin.ExecuteAsync("UPDATE platform.audit_log SET event_type = 'rewritten' WHERE id = @id", ("id", tampered));

        (await db.Worker.AsUserScalarAsync<long?>(null, "SELECT platform.verify_audit_chain()")).Should().Be(tampered);
    }

    [Fact]
    public async Task AppendAudit_UnderParallelWriters_KeepsASingleVerifiableChain()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => db.Api.AsUserScalarAsync<long>(
            Guid.CreateVersion7(),
            "SELECT platform.append_audit(now(), @e, 'actor', null, '{}'::jsonb, null, null)",
            ("e", $"event-{i}"))));

        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log")).Should().Be(20);
        (await db.Admin.ScalarAsync<long>("SELECT count(DISTINCT prev_hash) FROM platform.audit_log")).Should().Be(19, "every entry but the first has a distinct predecessor");
        (await db.Worker.AsUserScalarAsync<long?>(null, "SELECT platform.verify_audit_chain()")).Should().BeNull();
    }

    [Fact]
    public async Task NoFunctionOfTheApplicationSchemas_IsExecutableByPublic()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        string? exposed = await db.Admin.ScalarAsync<string>(
            """
            SELECT string_agg(n.nspname || '.' || p.proname, ', ')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname IN ('platform', 'identity', 'marketdata') AND has_function_privilege('public', p.oid, 'EXECUTE')
            """);

        exposed.Should().BeNull("every function must be granted explicitly to the roles that need it");
    }

    [Fact]
    public async Task EverySecurityDefinerFunction_PinsItsSearchPath()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        string? unpinned = await db.Admin.ScalarAsync<string>(
            """
            SELECT string_agg(n.nspname || '.' || p.proname, ', ')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname IN ('platform', 'identity', 'marketdata') AND p.prosecdef
              AND NOT EXISTS (SELECT 1 FROM unnest(coalesce(p.proconfig, '{}')) c WHERE c LIKE 'search_path=%, pg_temp')
            """);

        unpinned.Should().BeNull("an unpinned search_path, or one that lets the temporary schema come first, lets a caller shadow objects used by a privileged function");
    }

    [Fact]
    public async Task Public_CannotCreateTemporaryTables_SoApplicationLoginsCannotEither()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        (await db.Admin.ScalarAsync<bool>("SELECT has_database_privilege('api_role', current_database(), 'TEMPORARY')")).Should().BeFalse();
        (await db.Admin.ScalarAsync<bool>("SELECT has_database_privilege('worker_role', current_database(), 'TEMPORARY')")).Should().BeFalse();
    }

    [Fact]
    public async Task AFunctionCreatedLater_IsNotExecutableByPublicUntilGranted()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await db.Admin.ExecuteAsync("CREATE FUNCTION platform.forgotten_revoke() RETURNS integer LANGUAGE sql AS 'SELECT 1'");

        (await db.Admin.ScalarAsync<bool>("SELECT has_function_privilege('api_role', 'platform.forgotten_revoke()', 'EXECUTE')")).Should().BeFalse();
    }

    [Fact]
    public async Task UpgradeFromTheVersionBeforeTheAuditTriggerRevoke_KeepsTheAuditLogAndLocksTheTriggerFunction()
    {
        string admin = await postgres.CreateDatabaseAsync();
        var platform = MigrationSteps.PlatformStep(admin);
        await platform.MigrateToAsync("PlatformSecurity", CancellationToken.None);
        await admin.ExecuteAsync("SELECT platform.append_audit(now(), 'test.event', 'actor', null, '{}'::jsonb, null, null)");

        await platform.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log")).Should().Be(1);
        (await admin.ScalarAsync<bool>("SELECT has_function_privilege('public', 'platform.audit_log_reject_change()', 'EXECUTE')")).Should().BeFalse();
        (await admin.ScalarAsync<long?>("SELECT platform.verify_audit_chain()")).Should().BeNull("the chain is intact after the upgrade");
    }

    [Fact]
    public async Task UpgradeFromThePreviousVersion_KeepsDataAndAppliesSecurity()
    {
        string admin = await postgres.CreateDatabaseAsync();
        var step = MigrationSteps.PlatformStep(admin);
        await step.MigrateToAsync("CreatePlatformTables", CancellationToken.None);
        var user = Guid.CreateVersion7();
        await admin.ExecuteAsync(_insertKey, KeyParams(user, "legacy", DateTimeOffset.UtcNow.AddDays(1)));

        await step.RunAsync(CancellationToken.None);
        string api = await TimescalePostgresFixture.CreateRoleLoginAsync(admin, "api_role");

        (await admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys")).Should().Be(1);
        (await admin.ScalarAsync<bool>("SELECT relrowsecurity FROM pg_class WHERE oid = 'platform.idempotency_keys'::regclass")).Should().BeTrue();
        (await api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM platform.idempotency_keys")).Should().Be(0, "the legacy row is now protected");
        (await api.AsUserScalarAsync<long>(user, "SELECT count(*) FROM platform.idempotency_keys")).Should().Be(1);
    }

    [Fact]
    public async Task RetentionSupport_UpgradesFromThePreviousVersion_KeepingDataAndGrantingOnlyTheWorker()
    {
        string admin = await postgres.CreateDatabaseAsync();
        var step = MigrationSteps.PlatformStep(admin);
        await step.MigrateToAsync("RevokePublicFromAuditTrigger", CancellationToken.None);
        await admin.ExecuteAsync("""
            INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, processed_at, attempts, next_attempt_at)
            VALUES (gen_random_uuid(), 't', '{}', now() - interval '30 days', now() - interval '20 days', 1, now());
            """);

        await step.RunAsync(CancellationToken.None);
        string worker = await TimescalePostgresFixture.CreateRoleLoginAsync(admin, "worker_role");
        string api = await TimescalePostgresFixture.CreateRoleLoginAsync(admin, "api_role");

        (await admin.ScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages")).Should().Be(1);
        (await worker.ScalarAsync<long>("SELECT platform.count_expired_idempotency_keys(now())")).Should().Be(0);
        Func<Task> asApi = () => api.ScalarAsync<long>("SELECT platform.count_expired_idempotency_keys(now())");
        await asApi.Should().ThrowAsync<Npgsql.PostgresException>().Where(e => e.SqlState == "42501");
        (await admin.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'platform' AND indexname IN ('ix_outbox_messages_processed_at', 'ix_inbox_messages_processed_at', 'ix_job_queue_finished')")).Should().Be(3);
    }
}
