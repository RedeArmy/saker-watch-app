using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Retention.Application;
using SakerWatch.Retention.Domain;

namespace SakerWatch.Retention.Infrastructure;

/// <summary>
/// <see cref="IRetentionStore"/> over the platform tables, as the worker role. Every purge is one bounded statement that locks
/// its candidates with <c>FOR UPDATE SKIP LOCKED</c> where the worker may (outbox, job queue), so a concurrent run never waits on or
/// double-deletes the same rows.
/// Idempotency keys go through their security-definer functions because row-level security hides them from the worker.
/// All statements run inside the caller's unit of work (the job dispatcher's).
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class PlatformRetentionStore(DbSession session) : IRetentionStore
{
    private const string _outboxFilter = "processed_at IS NOT NULL AND processed_at < @cutoff";
    private const string _inboxFilter = "processed_at < @cutoff";
    private const string _jobsSucceededFilter = "state = 'succeeded' AND finished_at < @cutoff";
    private const string _jobsDeadFilter = "state = 'dead' AND finished_at < @cutoff";

    /// <inheritdoc />
    public async Task<long> CountEligibleAsync(RetentionTarget target, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        string sql = target switch
        {
            RetentionTarget.IdempotencyKeys => "SELECT platform.count_expired_idempotency_keys(@cutoff)",
            RetentionTarget.OutboxProcessed => $"SELECT count(*) FROM platform.outbox_messages WHERE {_outboxFilter}",
            RetentionTarget.InboxProcessed => $"SELECT count(*) FROM platform.inbox_messages WHERE {_inboxFilter}",
            RetentionTarget.JobsSucceeded => $"SELECT count(*) FROM platform.job_queue WHERE {_jobsSucceededFilter}",
            RetentionTarget.JobsDead => $"SELECT count(*) FROM platform.job_queue WHERE {_jobsDeadFilter}",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown retention target."),
        };

        await using NpgsqlCommand command = Command(sql, cutoff);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<int> PurgeBatchAsync(RetentionTarget target, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        string sql = target switch
        {
            RetentionTarget.IdempotencyKeys => "SELECT platform.purge_expired_idempotency_keys(@cutoff, @batch)",
            RetentionTarget.OutboxProcessed => Delete("platform.outbox_messages", "id", _outboxFilter, "processed_at"),
            RetentionTarget.InboxProcessed => Delete("platform.inbox_messages", "ctid", _inboxFilter, "processed_at", lockCandidates: false),
            RetentionTarget.JobsSucceeded => Delete("platform.job_queue", "id", _jobsSucceededFilter, "finished_at"),
            RetentionTarget.JobsDead => Delete("platform.job_queue", "id", _jobsDeadFilter, "finished_at"),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown retention target."),
        };

        await using NpgsqlCommand command = Command(sql, cutoff);
        command.Parameters.AddWithValue("batch", batchSize);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Returns the number of deleted rows so every target answers the same way. The identifiers are constants above, never input.
    // Locking the candidates needs the UPDATE privilege, which the worker deliberately lacks on the inbox (it only reads, inserts and
    // deletes there). Without the lock, two concurrent runs may pick the same rows, but the second DELETE finds them gone and counts
    // only what it really removed, so the result is still exact.
    private static string Delete(string table, string key, string filter, string orderBy, bool lockCandidates = true) => $"""
        WITH doomed AS (
            SELECT {key} AS k FROM {table} WHERE {filter} ORDER BY {orderBy} LIMIT @batch{(lockCandidates ? " FOR UPDATE SKIP LOCKED" : string.Empty)}),
        gone AS (
            DELETE FROM {table} t USING doomed d WHERE t.{key} = d.k RETURNING 1)
        SELECT count(*)::integer FROM gone
        """;

    private NpgsqlCommand Command(string sql, DateTimeOffset cutoff)
    {
        NpgsqlTransaction transaction = session.Transaction
            ?? throw new InvalidOperationException("Retention runs inside a unit of work (the job dispatcher's).");
        var command = new NpgsqlCommand(sql, session.Connection, transaction);
        command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        return command;
    }
}
