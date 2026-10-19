using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="ILatestBars"/> over <c>marketdata.price_daily</c>: for each listing the newest row, found by walking the primary key backwards and
/// stopping at the first row, so the cost is one index probe per listing and not the length of its history.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class LatestBars(DbSession session) : ILatestBars
{
    private const int _batchSize = 500;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<StockId, LatestBar>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stocks);
        var bars = new Dictionary<StockId, LatestBar>();
        foreach (StockId[] batch in stocks.Distinct().Chunk(_batchSize))
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT s.id, p.trade_date, p.close, p.is_final
                FROM unnest(@ids) AS s(id)
                CROSS JOIN LATERAL (
                    SELECT trade_date, close, is_final FROM marketdata.price_daily
                    WHERE stock_id = s.id ORDER BY trade_date DESC LIMIT 1) p
                """,
                session.Connection,
                session.Transaction);
            command.Parameters.AddWithValue("ids", batch.Select(id => id.Value).ToArray());

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bars[StockId.Create(reader.GetGuid(0))] = new LatestBar(reader.GetFieldValue<DateOnly>(1), reader.GetDecimal(2), reader.GetBoolean(3));
            }
        }

        return bars;
    }
}

/// <summary><see cref="IIngestionStatuses"/> over <c>marketdata.ingestion_state</c>.</summary>
/// <param name="session">The scope's database session.</param>
public sealed class FailedIngestions(DbSession session) : IIngestionStatuses
{
    /// <inheritdoc />
    public async Task<IReadOnlySet<StockId>> FailedAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stocks);
        var failed = new HashSet<StockId>();
        foreach (StockId[] batch in stocks.Distinct().Chunk(1000))
        {
            await using var command = new NpgsqlCommand(
                "SELECT stock_id FROM marketdata.ingestion_state WHERE stock_id = ANY(@ids) AND status = 'failed'",
                session.Connection,
                session.Transaction);
            command.Parameters.AddWithValue("ids", batch.Select(id => id.Value).ToArray());

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                failed.Add(StockId.Create(reader.GetGuid(0)));
            }
        }

        return failed;
    }
}
