using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="IListingLifecycleStore"/> over <c>marketdata.stocks</c>, <c>ticker_history</c>, <c>symbol_aliases</c>, <c>corporate_actions</c> and
/// <c>ingestion_state</c> (ADR-0039). Every change runs in the caller's unit of work, so a stock is never half renamed: the new ticker, the
/// history of the old one, the provider symbol and the corporate action commit together or not at all.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class ListingLifecycleStore(DbSession session) : IListingLifecycleStore
{
    /// <inheritdoc />
    public async Task<bool> DelistAsync(StockId stock, DateTimeOffset at, DateOnly on, string source, CancellationToken cancellationToken)
    {
        int changed = await ExecuteAsync(
            "UPDATE marketdata.stocks SET is_active = false, delisted_at = @at WHERE id = @id AND delisted_at IS NULL",
            cancellationToken,
            ("id", stock.Value), ("at", at.ToUniversalTime()));
        if (changed == 0)
        {
            return false;
        }

        await RecordActionAsync(stock, "delisting", on, source, at, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RelistAsync(StockId stock, DateTimeOffset at, CancellationToken cancellationToken)
    {
        int changed = await ExecuteAsync(
            "UPDATE marketdata.stocks SET is_active = true, delisted_at = NULL WHERE id = @id AND delisted_at IS NOT NULL",
            cancellationToken,
            ("id", stock.Value));
        if (changed == 0)
        {
            return false;
        }

        // Ingestion tries the stock from scratch: whatever stopped it, and the days the provider did not know it, are forgotten.
        await ExecuteAsync(
            "UPDATE marketdata.ingestion_state SET status = 'pending', failure_code = NULL, not_found_days = 0, last_not_found_on = NULL, last_attempt_at = @at WHERE stock_id = @id",
            cancellationToken,
            ("id", stock.Value), ("at", at.ToUniversalTime()));
        return true;
    }

    /// <inheritdoc />
    public async Task<RenameOutcome> RenameAsync(
        StockId stock, Mic market, Ticker oldTicker, Ticker newTicker, DateOnly on, string providerSymbol, DateTimeOffset at, CancellationToken cancellationToken)
    {
        object? taken = await ScalarAsync(
            "SELECT 1 FROM marketdata.stocks WHERE market_mic = @market AND ticker = @new AND id <> @id",
            cancellationToken,
            ("market", market.Value), ("new", newTicker.Value), ("id", stock.Value));
        if (taken is not null)
        {
            return RenameOutcome.TickerTaken;
        }

        // A ticker or a provider symbol that another stock took between the check and the write is an outcome, not an exception; the transaction is
        // aborted by then, so the caller's unit of work rolls everything back.
        try
        {
            int renamed = await ExecuteAsync(
                "UPDATE marketdata.stocks SET ticker = @new WHERE id = @id AND market_mic = @market AND ticker = @old",
                cancellationToken,
                ("market", market.Value), ("old", oldTicker.Value), ("new", newTicker.Value), ("id", stock.Value));
            if (renamed == 0)
            {
                return RenameOutcome.StockNotFound;
            }

            // The ticker in use is no longer a retired one (of this stock or of another that once had it); the old one is retired now.
            await ExecuteAsync(
                "DELETE FROM marketdata.ticker_history WHERE market_mic = @market AND ticker = @new",
                cancellationToken,
                ("market", market.Value), ("new", newTicker.Value));
            await ExecuteAsync(
                """
                INSERT INTO marketdata.ticker_history (market_mic, ticker, stock_id, changed_on, created_at)
                VALUES (@market, @old, @id, @on, @at)
                ON CONFLICT (market_mic, ticker) DO UPDATE SET stock_id = EXCLUDED.stock_id, changed_on = EXCLUDED.changed_on
                """,
                cancellationToken,
                ("market", market.Value), ("old", oldTicker.Value), ("id", stock.Value), ("on", on), ("at", at.ToUniversalTime()));

            // The provider calls the stock by its new symbol from now on; a symbol that was not the old ticker is the operator's to change.
            await ExecuteAsync(
                "UPDATE marketdata.symbol_aliases SET provider_symbol = @symbol WHERE stock_id = @id AND provider_symbol = @old",
                cancellationToken,
                ("symbol", providerSymbol), ("id", stock.Value), ("old", oldTicker.Value));
            await RecordActionAsync(stock, "symbol_change", on, Application.Lifecycle.ListingLifecycle.OperatorSource, at, cancellationToken);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return error.ConstraintName?.Contains("symbol_aliases", StringComparison.Ordinal) == true ? RenameOutcome.SymbolTaken : RenameOutcome.TickerTaken;
        }

        return RenameOutcome.Done;
    }

    /// <inheritdoc />
    public async Task<int> NoteSymbolNotFoundAsync(StockId stock, DateOnly day, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // One day counts once however many times it is tried; the status of a listing that works is not touched, a listing never tried is pending.
        object? days = await ScalarAsync(
            """
            INSERT INTO marketdata.ingestion_state (stock_id, status, last_attempt_at, not_found_days, last_not_found_on)
            VALUES (@id, 'pending', @at, 1, @day)
            ON CONFLICT (stock_id) DO UPDATE SET
                last_attempt_at = @at,
                not_found_days = CASE WHEN marketdata.ingestion_state.last_not_found_on = @day THEN marketdata.ingestion_state.not_found_days ELSE marketdata.ingestion_state.not_found_days + 1 END,
                last_not_found_on = @day
            RETURNING not_found_days
            """,
            cancellationToken,
            ("id", stock.Value), ("at", at.ToUniversalTime()), ("day", day));
        return Convert.ToInt32(days, System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<int> RecordActionAsync(StockId stock, string kind, DateOnly on, string source, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO marketdata.corporate_actions (id, stock_id, kind, effective_date, source, ingested_at)
            VALUES (@action, @id, @kind, @on, @source, @at)
            ON CONFLICT (stock_id, kind, effective_date) DO NOTHING
            """,
            cancellationToken,
            ("action", Guid.CreateVersion7(at)), ("id", stock.Value), ("kind", kind), ("on", on), ("source", source), ("at", at.ToUniversalTime()));

    private async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = Command(sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private NpgsqlCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
