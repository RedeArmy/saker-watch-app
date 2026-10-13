using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Application.Idempotency;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// PostgreSQL implementation of <see cref="IIdempotencyStore"/> on the scope's <see cref="DbSession"/>. Every statement
/// runs inside the active transaction, so the key commits together with the effect. A concurrent duplicate blocks on the
/// primary key until the first transaction ends; a lock timeout is reported as <see cref="IdempotencyClaim.Busy"/>.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class IdempotencyStore(DbSession session) : IIdempotencyStore
{
    private const string _lockNotAvailable = "55P03";

    /// <inheritdoc />
    public async Task<IdempotencyClaim> ClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NpgsqlTransaction transaction = RequireTransaction();

        try
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO platform.idempotency_keys (user_id, method, route, key, request_hash, state, created_at, expires_at, trace_id)
                VALUES (@user, @method, @route, @key, @hash, 'in_progress', @now, @expires, @trace)
                ON CONFLICT (user_id, method, route, key) DO NOTHING
                """,
                session.Connection,
                transaction);
            AddScope(insert, request.UserId, request.Key);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, request.RequestHash);
            insert.Parameters.AddWithValue("now", request.Now);
            insert.Parameters.AddWithValue("expires", request.ExpiresAt);
            insert.Parameters.AddWithValue("trace", (object?)request.TraceId ?? DBNull.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return new IdempotencyClaim.Claimed();
            }

            // The conflicting row was committed by another transaction (we waited for it if it was still open).
            await using var select = new NpgsqlCommand(
                "SELECT request_hash, state, response_status, response_body FROM platform.idempotency_keys WHERE user_id = @user AND method = @method AND route = @route AND key = @key",
                session.Connection,
                transaction);
            AddScope(select, request.UserId, request.Key);
            await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new IdempotencyClaim.Busy();
            }

            if (!((byte[])reader["request_hash"]).AsSpan().SequenceEqual(request.RequestHash))
            {
                return new IdempotencyClaim.Mismatch();
            }

            return string.Equals((string)reader["state"], "completed", StringComparison.Ordinal)
                ? new IdempotencyClaim.Replay(new StoredResponse((int)reader["response_status"], reader["response_body"] as byte[]))
                : new IdempotencyClaim.Busy();
        }
        catch (PostgresException ex) when (ex.SqlState == _lockNotAvailable)
        {
            return new IdempotencyClaim.Busy();
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(IdempotencyCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        NpgsqlTransaction transaction = RequireTransaction();

        await using var update = new NpgsqlCommand(
            """
            UPDATE platform.idempotency_keys
            SET state = 'completed', response_status = @status, response_body = @body
            WHERE user_id = @user AND method = @method AND route = @route AND key = @key
            """,
            session.Connection,
            transaction);
        AddScope(update, completion.UserId, completion.Key);
        update.Parameters.AddWithValue("status", completion.Status);
        update.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Bytea) { Value = (object?)completion.Body ?? DBNull.Value });
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The idempotency key claimed by this unit of work was not found when completing it.");
        }
    }

    private static void AddScope(NpgsqlCommand command, Guid userId, IdempotencyKeyInfo key)
    {
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("method", key.Method);
        command.Parameters.AddWithValue("route", key.Route);
        command.Parameters.AddWithValue("key", key.Key);
    }

    private NpgsqlTransaction RequireTransaction() =>
        session.Transaction ?? throw new InvalidOperationException("Idempotency needs an active unit of work; run the command through the pipeline.");
}
