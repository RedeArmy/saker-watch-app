using Npgsql;
using NpgsqlTypes;
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
        await using var command = new NpgsqlCommand(
            "SELECT id, name, currency, asset_type, is_active FROM marketdata.stocks WHERE market_mic = @market AND ticker = @ticker",
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("market", market.Value);
        command.Parameters.AddWithValue("ticker", ticker.Value);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CatalogListing(
                StockId.Create(reader.GetGuid(0)),
                market,
                ticker,
                reader.GetString(1),
                CurrencyCode.Parse(reader.GetString(2)),
                reader.GetString(3) switch { "equity" => AssetType.Equity, "etf" => AssetType.Etf, _ => AssetType.Other },
                reader.GetBoolean(4))
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT s.id FROM marketdata.stocks s
            LEFT JOIN marketdata.ingestion_state i ON i.stock_id = s.id
            WHERE s.market_mic = @market AND s.is_active AND coalesce(i.status, 'pending') <> 'failed'
            ORDER BY s.ticker
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("market", market.Value);

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
        ON CONFLICT (stock_id) DO UPDATE SET status = 'ok', last_success_at = @at, last_attempt_at = @at, failure_code = NULL
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
