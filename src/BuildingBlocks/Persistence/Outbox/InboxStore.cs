using Npgsql;
using SakerWatch.BuildingBlocks.Application.Events;

namespace SakerWatch.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// <see cref="IInbox"/> on <c>platform.inbox_messages</c>. The row is written in the consumer's own unit of work, so the
/// "handled" mark and the consumer's effect commit together: redelivery after a crash either finds both or neither.
/// </summary>
/// <param name="session">The scope's database session.</param>
/// <param name="timeProvider">Injected clock.</param>
public sealed class InboxStore(DbSession session, TimeProvider timeProvider) : IInbox
{
    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(string consumer, Guid messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        NpgsqlTransaction transaction = session.Transaction
            ?? throw new InvalidOperationException("The inbox needs an active unit of work.");

        await using var insert = new NpgsqlCommand(
            "INSERT INTO platform.inbox_messages (consumer, message_id, processed_at) VALUES (@consumer, @message, @now) ON CONFLICT DO NOTHING",
            session.Connection,
            transaction);
        insert.Parameters.AddWithValue("consumer", consumer);
        insert.Parameters.AddWithValue("message", messageId);
        insert.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        return await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
