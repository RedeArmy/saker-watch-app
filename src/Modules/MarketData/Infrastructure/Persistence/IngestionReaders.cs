using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary><see cref="IStockCatalog"/> over <c>marketdata.stocks</c>.</summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class StockCatalog(DbSession session) : IStockCatalog
{
    /// <inheritdoc />
    public async Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT market_mic, ticker, is_active FROM marketdata.stocks WHERE id = @id", session.Connection, session.Transaction);
        command.Parameters.AddWithValue("id", id.Value);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CatalogStock(id, Mic.Parse(reader.GetString(0)), Ticker.Parse(reader.GetString(1)), reader.GetBoolean(2))
            : null;
    }

    /// <inheritdoc />
    public async Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken)
    {
        // A ticker in use by a stock always wins; only when no stock uses it is the history of retired tickers consulted (ADR-0039).
        return await FindByKeyAsync(
            "SELECT id, name, currency, asset_type, is_active, delisted_at, ticker FROM marketdata.stocks WHERE market_mic = @market AND ticker = @ticker",
            market,
            ticker,
            cancellationToken)
            ?? await FindByKeyAsync(
                """
                SELECT s.id, s.name, s.currency, s.asset_type, s.is_active, s.delisted_at, s.ticker
                FROM marketdata.ticker_history h
                JOIN marketdata.stocks s ON s.id = h.stock_id
                WHERE h.market_mic = @market AND h.ticker = @ticker
                """,
                market,
                ticker,
                cancellationToken);
    }

    private async Task<CatalogListing?> FindByKeyAsync(string sql, Mic market, Ticker ticker, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.AddWithValue("market", market.Value);
        command.Parameters.AddWithValue("ticker", ticker.Value);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        DateTimeOffset? delistedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
        return new CatalogListing(
            StockId.Create(reader.GetGuid(0)),
            market,
            Ticker.Parse(reader.GetString(6)),
            reader.GetString(1),
            CurrencyCode.Parse(reader.GetString(2)),
            AssetTypeNames.Parse(reader.GetString(3)),
            reader.GetBoolean(4),
            delistedAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var listings = new List<CatalogListing>();
        foreach (StockId[] batch in ids.Distinct().Chunk(1000))
        {
            await using var command = new NpgsqlCommand(
                "SELECT id, market_mic, ticker, name, currency, asset_type, is_active, delisted_at FROM marketdata.stocks WHERE id = ANY(@ids)",
                session.Connection,
                session.Transaction);
            command.Parameters.AddWithValue("ids", batch.Select(id => id.Value).ToArray());

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                listings.Add(new CatalogListing(
                    StockId.Create(reader.GetGuid(0)),
                    Mic.Parse(reader.GetString(1)),
                    Ticker.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    CurrencyCode.Parse(reader.GetString(4)),
                    AssetTypeNames.Parse(reader.GetString(5)),
                    reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        return listings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT s.id FROM marketdata.stocks s
            LEFT JOIN marketdata.ingestion_state i ON i.stock_id = s.id
            WHERE s.market_mic = @market AND s.is_active
              AND (coalesce(i.status, 'pending') <> 'failed' OR i.failure_code = ANY(@configuration))
            ORDER BY s.ticker
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("market", market.Value);
        // A listing stopped by the configuration (no provider for the market, no calendar, no provider symbol) is asked again every day: nothing of it is
        // wrong, it does not call the provider while the cause stands, and the first run after the cause is fixed clears the mark.
        command.Parameters.AddWithValue("configuration", new[] { ListingIngestion.MarketDisabledCode, ListingIngestion.NoAliasCode, ListingIngestion.NoCalendarCode });

        var ids = new List<StockId>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(StockId.Create(reader.GetGuid(0)));
        }

        return ids;
    }
}

/// <summary><see cref="ISymbolAliases"/> over <c>marketdata.symbol_aliases</c>.</summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class SymbolAliases(DbSession session) : ISymbolAliases
{
    /// <inheritdoc />
    public async Task<ProviderListingKey?> FindAsync(StockId stock, string provider, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT provider_mic, provider_symbol FROM marketdata.symbol_aliases WHERE stock_id = @stock AND provider = @provider",
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("stock", stock.Value);
        command.Parameters.AddWithValue("provider", provider);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new ProviderListingKey(reader.GetString(0), reader.GetString(1)) : null;
    }
}

/// <summary><see cref="IIngestionState"/> over <c>marketdata.ingestion_state</c>.</summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class IngestionState(DbSession session) : IIngestionState
{
    private const string _success = """
        INSERT INTO marketdata.ingestion_state (stock_id, status, last_success_at, last_attempt_at, failure_code)
        VALUES (@stock, 'ok', @at, @at, NULL)
        ON CONFLICT (stock_id) DO UPDATE SET status = 'ok', last_success_at = @at, last_attempt_at = @at, failure_code = NULL, not_found_days = 0, last_not_found_on = NULL
        """;

    private const string _failure = """
        INSERT INTO marketdata.ingestion_state (stock_id, status, last_attempt_at, failure_code)
        VALUES (@stock, 'failed', @at, @code)
        ON CONFLICT (stock_id) DO UPDATE SET status = 'failed', last_attempt_at = @at, failure_code = @code
        """;

    /// <inheritdoc />
    public Task RecordSuccessAsync(StockId stock, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync(_success, stock, at, null, cancellationToken);

    /// <inheritdoc />
    public Task RecordFailureAsync(StockId stock, string code, bool permanent, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return permanent
            ? ExecuteAsync(_failure, stock, at, code, cancellationToken)
            : Task.CompletedTask; // a failure that will be retried leaves no mark: the queue keeps its own record
    }

    private async Task ExecuteAsync(string sql, StockId stock, DateTimeOffset at, string? code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.AddWithValue("stock", stock.Value);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.ToUniversalTime() });
        if (code is not null)
        {
            command.Parameters.AddWithValue("code", code);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary><see cref="IFreshnessReader"/> over the ingestable listings and <c>marketdata.price_daily</c>.</summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class FreshnessReader(DbSession session) : IFreshnessReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ListingLatest>> LatestPerListingAsync(Mic market, CancellationToken cancellationToken)
    {
        // The newest day of a listing is the end of a backward scan of its primary key, so this is one index probe per listing.
        await using var command = new NpgsqlCommand(
            """
            SELECT s.id, (SELECT max(p.trade_date) FROM marketdata.price_daily p WHERE p.stock_id = s.id)
            FROM marketdata.stocks s
            LEFT JOIN marketdata.ingestion_state i ON i.stock_id = s.id
            WHERE s.market_mic = @market AND s.is_active AND coalesce(i.status, 'pending') <> 'failed'
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("market", market.Value);

        var listings = new List<ListingLatest>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            listings.Add(new ListingLatest(StockId.Create(reader.GetGuid(0)), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1)));
        }

        return listings;
    }
}
