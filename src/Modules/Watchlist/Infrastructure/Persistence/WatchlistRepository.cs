using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Domain.Items;

namespace SakerWatch.Watchlist.Infrastructure.Persistence;

/// <summary>
/// <see cref="IWatchlistRepository"/> over <c>watchlist.watchlist_items</c>, in the command's unit of work. Every statement names the user as well
/// as relying on the row-level security policy. An insert that races another one is a plain <c>ON CONFLICT DO NOTHING</c>, so it never aborts the
/// transaction; the only exception the database raises on purpose is the ceiling trigger, which is caught by its constraint name.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class WatchlistRepository(DbSession session) : IWatchlistRepository
{
    private const string _limitConstraint = "ck_watchlist_items_max_per_user";

    /// <inheritdoc />
    public async Task<WatchlistItem?> FindByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            "SELECT id, notes, target_price, created_at, updated_at FROM watchlist.watchlist_items WHERE user_id = @user AND stock_id = @stock");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("stock", stockId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string? notes = reader.IsDBNull(1) ? null : reader.GetString(1);
        decimal? target = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
        return WatchlistItem.Restore(
            WatchlistItemId.Create(reader.GetGuid(0)),
            userId,
            stockId,
            notes,
            target,
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command("SELECT count(*) FROM watchlist.watchlist_items WHERE user_id = @user");
        command.Parameters.AddWithValue("user", userId);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<AddOutcome> AddAsync(WatchlistItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        await using NpgsqlCommand command = Command(
            """
            INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, notes, target_price, created_at, updated_at)
            VALUES (@id, @user, @stock, @notes, @target, @created, @updated)
            ON CONFLICT (user_id, stock_id) DO NOTHING
            """);
        command.Parameters.AddWithValue("id", item.Id.Value);
        command.Parameters.AddWithValue("user", item.UserId);
        command.Parameters.AddWithValue("stock", item.StockId);
        command.Parameters.AddWithValue("notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("target", (object?)item.TargetPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("created", item.CreatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("updated", item.UpdatedAt.ToUniversalTime());

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? AddOutcome.Added : AddOutcome.AlreadyThere;
        }
        catch (PostgresException error) when (error is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: _limitConstraint })
        {
            // The transaction is aborted by now; the caller returns its failure without touching the database again and the unit of work rolls back.
            return AddOutcome.LimitReached;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(WatchlistItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        await using NpgsqlCommand command = Command(
            "UPDATE watchlist.watchlist_items SET notes = @notes, target_price = @target, updated_at = @updated WHERE id = @id AND user_id = @user");
        command.Parameters.AddWithValue("notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("target", (object?)item.TargetPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("updated", item.UpdatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("id", item.Id.Value);
        command.Parameters.AddWithValue("user", item.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command("DELETE FROM watchlist.watchlist_items WHERE user_id = @user AND stock_id = @stock");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("stock", stockId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private NpgsqlCommand Command(string sql) => new(sql, session.Connection, session.Transaction);
}
