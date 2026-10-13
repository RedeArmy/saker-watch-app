using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>
/// Enqueues jobs through <c>platform.enqueue_job</c> (the API role has no table access): a deterministic key with
/// <c>ON CONFLICT DO NOTHING</c>, so enqueuing the same work twice creates it once. Inside a unit of work the job commits
/// or rolls back with the business change; outside one it commits on its own.
/// </summary>
/// <param name="session">The scope's database session.</param>
/// <param name="registry">Job type names and payload serialization.</param>
/// <param name="correlation">Current correlation id.</param>
/// <param name="ids">Id source.</param>
/// <param name="timeProvider">Injected clock.</param>
public sealed class JobEnqueuer(DbSession session, JobRegistry registry, ICorrelationContext correlation, IIdGenerator ids, TimeProvider timeProvider) : IJobEnqueuer
{
    private const int _maxKeyLength = 200;

    /// <inheritdoc />
    public async Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
        where TPayload : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);
        ArgumentNullException.ThrowIfNull(payload);
        if (jobKey.Length > _maxKeyLength || jobKey.Any(char.IsControl))
        {
            throw new ArgumentException($"A job key is at most {_maxKeyLength} printable characters.", nameof(jobKey));
        }

        JobEnqueueOptions settings = options ?? new JobEnqueueOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxAttempts, 1);
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Join the caller's unit of work, or open a short one of our own (calls nest).
        await session.BeginAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT platform.enqueue_job(@id, @key, @type, @payload, @available, @max, @trace, @correlation, @now)",
                session.Connection,
                session.Transaction);
            command.Parameters.AddWithValue("id", ids.NewGuid());
            command.Parameters.AddWithValue("key", jobKey);
            command.Parameters.AddWithValue("type", registry.NameOf(typeof(TPayload)));
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JobRegistry.Serialize(payload) });
            command.Parameters.AddWithValue("available", settings.AvailableAt ?? now);
            command.Parameters.AddWithValue("max", settings.MaxAttempts);
            command.Parameters.AddWithValue("trace", (object?)System.Diagnostics.Activity.Current?.TraceId.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("correlation", (object?)correlation.CorrelationId ?? DBNull.Value);
            command.Parameters.AddWithValue("now", now);
            bool created = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            await session.CommitAsync(cancellationToken);
            return created;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
