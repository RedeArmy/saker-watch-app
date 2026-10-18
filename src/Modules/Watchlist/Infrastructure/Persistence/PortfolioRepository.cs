using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPortfolioRepository"/> over <c>watchlist.portfolios</c> and <c>watchlist.portfolio_items</c>, in the command's unit of work (or the
/// query's). Every statement names the user as well as relying on the row-level security policy. A race is an outcome and not an aborted
/// transaction wherever the statement can say so (<c>ON CONFLICT DO NOTHING</c>); the exceptions the database raises on purpose (the ceilings, a
/// name taken between the check and the write, an item deleted under the insert) are caught by their constraint name, and the caller stops.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class PortfolioRepository(DbSession session) : IPortfolioRepository
{
    private const string _portfolioLimit = "ck_portfolios_max_per_user";
    private const string _memberLimit = "ck_portfolio_items_max_per_portfolio";
    private const string _nameIndex = "ux_portfolios_user_id_lower_name";

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortfolioSummary>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            """
            SELECT p.id, p.name, p.description, p.position, p.created_at, p.updated_at, count(i.watchlist_item_id)
            FROM watchlist.portfolios p
            LEFT JOIN watchlist.portfolio_items i ON i.portfolio_id = p.id
            WHERE p.user_id = @user
            GROUP BY p.id
            ORDER BY p.position, p.created_at, p.id
            """);
        command.Parameters.AddWithValue("user", userId);
        return await ReadSummariesAsync(command, userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PortfolioSummary?> FindAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            """
            SELECT p.id, p.name, p.description, p.position, p.created_at, p.updated_at, count(i.watchlist_item_id)
            FROM watchlist.portfolios p
            LEFT JOIN watchlist.portfolio_items i ON i.portfolio_id = p.id
            WHERE p.user_id = @user AND p.id = @id
            GROUP BY p.id
            """);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("id", id.Value);
        IReadOnlyList<PortfolioSummary> found = await ReadSummariesAsync(command, userId, cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc />
    public async Task<PortfolioWrite> AddAsync(Portfolio portfolio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portfolio);

        await using NpgsqlCommand command = Command(
            """
            INSERT INTO watchlist.portfolios (id, user_id, name, description, position, created_at, updated_at)
            VALUES (@id, @user, @name, @description, @position, @created, @updated)
            ON CONFLICT (user_id, lower(name)) DO NOTHING
            """);
        command.Parameters.AddWithValue("id", portfolio.Id.Value);
        command.Parameters.AddWithValue("user", portfolio.UserId);
        command.Parameters.AddWithValue("name", portfolio.Name.Value);
        command.Parameters.AddWithValue("description", (object?)portfolio.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("position", portfolio.Position);
        command.Parameters.AddWithValue("created", portfolio.CreatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("updated", portfolio.UpdatedAt.ToUniversalTime());

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? PortfolioWrite.Done : PortfolioWrite.NameTaken;
        }
        catch (PostgresException error) when (error is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: _portfolioLimit })
        {
            return PortfolioWrite.LimitReached;
        }
    }

    /// <inheritdoc />
    public async Task<PortfolioWrite> UpdateAsync(Portfolio portfolio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portfolio);

        await using NpgsqlCommand command = Command(
            "UPDATE watchlist.portfolios SET name = @name, description = @description, updated_at = @updated WHERE id = @id AND user_id = @user");
        command.Parameters.AddWithValue("name", portfolio.Name.Value);
        command.Parameters.AddWithValue("description", (object?)portfolio.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("updated", portfolio.UpdatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("id", portfolio.Id.Value);
        command.Parameters.AddWithValue("user", portfolio.UserId);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? PortfolioWrite.Done : PortfolioWrite.NotFound;
        }
        catch (PostgresException error) when (error is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: _nameIndex })
        {
            return PortfolioWrite.NameTaken;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReorderAsync(Guid userId, IReadOnlyList<PortfolioId> ordered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        IReadOnlyList<PortfolioSummary> current = await ListAsync(userId, cancellationToken);
        Guid[] wanted = [.. ordered.Select(id => id.Value)];
        if (!SameSet(current.Select(summary => summary.Portfolio.Id.Value), wanted))
        {
            return false;
        }

        if (wanted.Length > 0)
        {
            await using NpgsqlCommand command = Command(
                """
                UPDATE watchlist.portfolios p SET position = o.ord - 1
                FROM unnest(@ids) WITH ORDINALITY AS o(id, ord)
                WHERE p.id = o.id AND p.user_id = @user
                """);
            command.Parameters.AddWithValue("ids", wanted);
            command.Parameters.AddWithValue("user", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortfolioMember>> MembersAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            """
            SELECT i.stock_id, m.position, m.added_at
            FROM watchlist.portfolio_items m
            JOIN watchlist.watchlist_items i ON i.id = m.watchlist_item_id
            WHERE m.portfolio_id = @id AND m.user_id = @user
            ORDER BY m.position, m.added_at, i.stock_id
            """);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("user", userId);

        var members = new List<PortfolioMember>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new PortfolioMember(reader.GetGuid(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return members;
    }

    /// <inheritdoc />
    public async Task<MemberAdd> AddMemberAsync(Guid userId, PortfolioId id, Guid stockId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid? item = await ItemOfAsync(userId, stockId, cancellationToken);
        if (item is null)
        {
            return MemberAdd.ItemNotOnWatchlist;
        }

        await using NpgsqlCommand command = Command(
            """
            INSERT INTO watchlist.portfolio_items (portfolio_id, watchlist_item_id, user_id, position, added_at)
            SELECT p.id, @item, @user, coalesce((SELECT max(position) + 1 FROM watchlist.portfolio_items WHERE portfolio_id = p.id), 0), @now
            FROM watchlist.portfolios p
            WHERE p.id = @id AND p.user_id = @user
            ON CONFLICT (portfolio_id, watchlist_item_id) DO NOTHING
            """);
        command.Parameters.AddWithValue("item", item.Value);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return MemberAdd.Added;
            }
        }
        catch (PostgresException error) when (error is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: _memberLimit })
        {
            return MemberAdd.LimitReached;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // The stock was taken off the watchlist between the read and the insert.
            return MemberAdd.ItemNotOnWatchlist;
        }

        // Nothing was inserted: the stock is already in the portfolio, or the portfolio is gone.
        return await FindAsync(userId, id, cancellationToken) is null ? MemberAdd.PortfolioNotFound : MemberAdd.AlreadyThere;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveMemberAsync(Guid userId, PortfolioId id, Guid stockId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            """
            DELETE FROM watchlist.portfolio_items m
            USING watchlist.watchlist_items i
            WHERE m.watchlist_item_id = i.id AND m.portfolio_id = @id AND m.user_id = @user AND i.user_id = @user AND i.stock_id = @stock
            """);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("stock", stockId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ReorderMembersAsync(Guid userId, PortfolioId id, IReadOnlyList<Guid> orderedStocks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedStocks);

        IReadOnlyList<PortfolioMember> current = await MembersAsync(userId, id, cancellationToken);
        if (!SameSet(current.Select(member => member.StockId), orderedStocks))
        {
            return false;
        }

        if (orderedStocks.Count > 0)
        {
            await using NpgsqlCommand command = Command(
                """
                UPDATE watchlist.portfolio_items m SET position = o.ord - 1
                FROM unnest(@stocks) WITH ORDINALITY AS o(stock_id, ord), watchlist.watchlist_items i
                WHERE i.id = m.watchlist_item_id AND i.stock_id = o.stock_id AND i.user_id = @user AND m.portfolio_id = @id AND m.user_id = @user
                """);
            command.Parameters.AddWithValue("stocks", orderedStocks.ToArray());
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("id", id.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<DeletionPreview> PreviewDeletionAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            """
            SELECT i.stock_id,
                   EXISTS (SELECT 1 FROM watchlist.portfolio_items o WHERE o.watchlist_item_id = m.watchlist_item_id AND o.portfolio_id <> m.portfolio_id) AS held_elsewhere
            FROM watchlist.portfolio_items m
            JOIN watchlist.watchlist_items i ON i.id = m.watchlist_item_id
            WHERE m.portfolio_id = @id AND m.user_id = @user
            ORDER BY i.stock_id
            """);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("user", userId);

        var toRemove = new List<Guid>();
        int kept = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetBoolean(1))
            {
                kept++;
            }
            else
            {
                toRemove.Add(reader.GetGuid(0));
            }
        }

        return new DeletionPreview(toRemove, kept);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid userId, PortfolioId id, bool removeOrphans, CancellationToken cancellationToken)
    {
        // The portfolio row and the watchlist items it holds are locked first. A parallel request that puts one of those stocks in another
        // portfolio takes a share lock on the item through its foreign key, so it either finishes before this transaction looks, or waits for it and
        // then fails because the item is gone; the orphan check below can never miss a membership that is about to commit.
        await using (NpgsqlCommand lockPortfolio = Command("SELECT 1 FROM watchlist.portfolios WHERE id = @id AND user_id = @user FOR UPDATE"))
        {
            lockPortfolio.Parameters.AddWithValue("id", id.Value);
            lockPortfolio.Parameters.AddWithValue("user", userId);
            if (await lockPortfolio.ExecuteScalarAsync(cancellationToken) is null)
            {
                return false;
            }
        }

        if (removeOrphans)
        {
            await using (NpgsqlCommand lockItems = Command(
                "SELECT i.id FROM watchlist.watchlist_items i WHERE i.user_id = @user AND i.id IN (SELECT watchlist_item_id FROM watchlist.portfolio_items WHERE portfolio_id = @id) ORDER BY i.id FOR UPDATE"))
            {
                lockItems.Parameters.AddWithValue("id", id.Value);
                lockItems.Parameters.AddWithValue("user", userId);
                await lockItems.ExecuteNonQueryAsync(cancellationToken);
            }

            await using NpgsqlCommand orphans = Command(
                """
                DELETE FROM watchlist.watchlist_items i
                WHERE i.user_id = @user
                  AND i.id IN (SELECT watchlist_item_id FROM watchlist.portfolio_items WHERE portfolio_id = @id)
                  AND NOT EXISTS (SELECT 1 FROM watchlist.portfolio_items o WHERE o.watchlist_item_id = i.id AND o.portfolio_id <> @id)
                """);
            orphans.Parameters.AddWithValue("id", id.Value);
            orphans.Parameters.AddWithValue("user", userId);
            await orphans.ExecuteNonQueryAsync(cancellationToken);
        }

        await using NpgsqlCommand delete = Command("DELETE FROM watchlist.portfolios WHERE id = @id AND user_id = @user");
        delete.Parameters.AddWithValue("id", id.Value);
        delete.Parameters.AddWithValue("user", userId);
        return await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static bool SameSet(IEnumerable<Guid> current, IReadOnlyCollection<Guid> wanted)
    {
        Guid[] have = [.. current];
        return wanted.Count == have.Length && wanted.Distinct().Count() == wanted.Count && !have.Except(wanted).Any();
    }

    private async Task<Guid?> ItemOfAsync(Guid userId, Guid stockId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command("SELECT id FROM watchlist.watchlist_items WHERE user_id = @user AND stock_id = @stock");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("stock", stockId);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private static async Task<IReadOnlyList<PortfolioSummary>> ReadSummariesAsync(NpgsqlCommand command, Guid userId, CancellationToken cancellationToken)
    {
        var summaries = new List<PortfolioSummary>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? description = reader.IsDBNull(2) ? null : reader.GetString(2);
            Portfolio portfolio = Portfolio.Restore(
                PortfolioId.Create(reader.GetGuid(0)),
                userId,
                PortfolioName.Create(reader.GetString(1)).Value,
                description,
                reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5));
            summaries.Add(new PortfolioSummary(portfolio, (int)reader.GetInt64(6)));
        }

        return summaries;
    }

    private NpgsqlCommand Command(string sql) => new(sql, session.Connection, session.Transaction);
}
