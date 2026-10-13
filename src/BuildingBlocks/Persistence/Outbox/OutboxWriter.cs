using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Inserts integration events into <c>platform.outbox_messages</c> inside the active unit of work, so a message exists
/// if and only if the state change that produced it committed (ADR-0005). The event id is the primary key, so adding the
/// same event twice is harmless. Trace, correlation and user ids are stored for lineage (ADR-0016).
/// </summary>
/// <param name="session">The scope's database session.</param>
/// <param name="user">The current caller.</param>
/// <param name="correlation">The current correlation id.</param>
/// <param name="registry">Event names and serialization.</param>
public sealed class OutboxWriter(DbSession session, ICurrentUser user, ICorrelationContext correlation, IntegrationEventRegistry registry) : IOutbox
{
    /// <inheritdoc />
    public async Task AddAsync(IDomainEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        NpgsqlTransaction transaction = session.Transaction
            ?? throw new InvalidOperationException("The outbox needs an active unit of work; add messages from a command handler.");

        // No conflict target on purpose: naming one would require SELECT on the table, which the API role does not have.
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, type, payload, occurred_at, trace_id, correlation_id, user_id, attempts, next_attempt_at)
            VALUES (@id, @type, @payload, @occurred, @trace, @correlation, @user, 0, @occurred)
            ON CONFLICT DO NOTHING
            """,
            session.Connection,
            transaction);
        insert.Parameters.AddWithValue("id", message.EventId);
        insert.Parameters.AddWithValue("type", registry.NameOf(message));
        insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = IntegrationEventRegistry.Serialize(message) });
        // PostgreSQL timestamptz accepts only UTC offsets; producers may hold any offset.
        insert.Parameters.AddWithValue("occurred", message.OccurredAt.ToUniversalTime());
        insert.Parameters.AddWithValue("trace", (object?)System.Diagnostics.Activity.Current?.TraceId.ToString() ?? DBNull.Value);
        insert.Parameters.AddWithValue("correlation", (object?)correlation.CorrelationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("user", (object?)user.UserId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IEnumerable<IDomainEvent> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (IDomainEvent message in messages)
        {
            await AddAsync(message, cancellationToken);
        }
    }
}
