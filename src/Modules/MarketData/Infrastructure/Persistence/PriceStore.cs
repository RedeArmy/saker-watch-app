using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPriceStore"/> over <c>marketdata.price_daily</c>. A batch is one statement: the days travel as arrays and are unnested, so a
/// backfill of twenty years is one round trip and every value is a parameter. The upsert never lets a provisional row replace a final one.
/// </summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class PriceStore(DbSession session) : IPriceStore
{
    // The rows of one statement. A twenty-year history is about 5 000 rows, so the usual backfill is a single call.
    private const int _batchSize = 5000;

    private const string _upsert = """
        INSERT INTO marketdata.price_daily
            (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
        SELECT @stock, t.trade_date, t.open, t.high, t.low, t.close, t.adjusted_close, t.volume, @source, t.is_final, @ingested_at
        FROM unnest(@dates, @opens, @highs, @lows, @closes, @adjusted, @volumes, @finals)
            AS t(trade_date, open, high, low, close, adjusted_close, volume, is_final)
        ON CONFLICT (stock_id, trade_date) DO UPDATE SET
            open = EXCLUDED.open,
            high = EXCLUDED.high,
            low = EXCLUDED.low,
            close = EXCLUDED.close,
            adjusted_close = EXCLUDED.adjusted_close,
            volume = EXCLUDED.volume,
            source = EXCLUDED.source,
            is_final = EXCLUDED.is_final,
            ingested_at = EXCLUDED.ingested_at
        WHERE NOT price_daily.is_final OR EXCLUDED.is_final
        """;

    /// <inheritdoc />
    public async Task<int> UpsertAsync(StockId stock, string source, IReadOnlyList<PricePoint> points, DateTimeOffset ingestedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(points);

        int written = 0;
        foreach (PricePoint[] batch in points.Chunk(_batchSize))
        {
            await using NpgsqlCommand command = Command(_upsert);
            command.Parameters.AddWithValue("stock", stock.Value);
            command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("ingested_at", ingestedAt.ToUniversalTime());
            command.Parameters.AddWithValue("dates", batch.Select(p => p.Raw.Date).ToArray());
            command.Parameters.AddWithValue("opens", batch.Select(p => p.Raw.Open).ToArray());
            command.Parameters.AddWithValue("highs", batch.Select(p => p.Raw.High).ToArray());
            command.Parameters.AddWithValue("lows", batch.Select(p => p.Raw.Low).ToArray());
            command.Parameters.AddWithValue("closes", batch.Select(p => p.Raw.Close).ToArray());
            command.Parameters.AddWithValue("adjusted", batch.Select(p => p.AdjustedClose).ToArray());
            command.Parameters.AddWithValue("volumes", batch.Select(p => p.Raw.Volume).ToArray());
            command.Parameters.AddWithValue("finals", batch.Select(p => p.IsFinal).ToArray());
            written += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return written;
    }

    /// <inheritdoc />
    public async Task<LatestPrice?> LatestAsync(StockId stock, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            "SELECT trade_date, is_final FROM marketdata.price_daily WHERE stock_id = @stock ORDER BY trade_date DESC LIMIT 1");
        command.Parameters.AddWithValue("stock", stock.Value);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new LatestPrice(reader.GetFieldValue<DateOnly>(0), reader.GetBoolean(1)) : null;
    }

    /// <inheritdoc />
    public async Task<decimal?> AdjustedCloseAsync(StockId stock, DateOnly date, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(
            "SELECT adjusted_close FROM marketdata.price_daily WHERE stock_id = @stock AND trade_date = @date");
        command.Parameters.AddWithValue("stock", stock.Value);
        command.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = date });

        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is decimal adjusted ? adjusted : null;
    }

    private NpgsqlCommand Command(string sql) => new(sql, session.Connection, session.Transaction);
}
