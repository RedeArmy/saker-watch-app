using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPriceSeriesReader"/> over <c>marketdata.price_daily</c> (ADR-0038). A series is one statement per stock: a range scan of the
/// primary key, optionally keeping the last trading day of each week or month. The recent closes of many stocks are one lateral probe per stock,
/// found by walking the primary key backwards and stopping after the days asked for, in batches.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class PriceSeriesReader(DbSession session) : IPriceSeriesReader
{
    private const int _batchSize = 500;

    private const string _days =
        "SELECT trade_date, close, adjusted_close, is_final FROM marketdata.price_daily WHERE stock_id = @id AND trade_date >= @from ORDER BY trade_date";

    private const string _allDays =
        "SELECT trade_date, close, adjusted_close, is_final FROM marketdata.price_daily WHERE stock_id = @id ORDER BY trade_date";

    // The last trading day of each bucket: DISTINCT ON keeps the first row of each bucket in the order given, which is the newest day.
    private const string _buckets =
        """
        SELECT trade_date, close, adjusted_close, is_final FROM (
            SELECT DISTINCT ON (date_trunc('{unit}', trade_date::timestamp)) trade_date, close, adjusted_close, is_final
            FROM marketdata.price_daily
            WHERE stock_id = @id{from}
            ORDER BY date_trunc('{unit}', trade_date::timestamp), trade_date DESC) b
        ORDER BY trade_date
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeriesBar>> ReadAsync(StockId stock, DateOnly? from, SeriesBucket bucket, CancellationToken cancellationToken)
    {
        string sql = bucket switch
        {
            SeriesBucket.Week => _buckets.Replace("{unit}", "week", StringComparison.Ordinal).Replace("{from}", from is null ? string.Empty : " AND trade_date >= @from", StringComparison.Ordinal),
            SeriesBucket.Month => _buckets.Replace("{unit}", "month", StringComparison.Ordinal).Replace("{from}", from is null ? string.Empty : " AND trade_date >= @from", StringComparison.Ordinal),
            _ => from is null ? _allDays : _days,
        };

        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.AddWithValue("id", stock.Value);
        if (from is { } start)
        {
            command.Parameters.AddWithValue("from", start);
        }

        var bars = new List<SeriesBar>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bars.Add(new SeriesBar(reader.GetFieldValue<DateOnly>(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetBoolean(3)));
        }

        return bars;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<StockId, IReadOnlyList<decimal>>> RecentAdjustedAsync(IReadOnlyCollection<StockId> stocks, int days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stocks);
        var recent = new Dictionary<StockId, List<decimal>>();
        foreach (StockId[] batch in stocks.Distinct().Chunk(_batchSize))
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT s.id, p.adjusted_close
                FROM unnest(@ids) AS s(id)
                CROSS JOIN LATERAL (
                    SELECT trade_date, adjusted_close FROM marketdata.price_daily
                    WHERE stock_id = s.id ORDER BY trade_date DESC LIMIT @days) p
                ORDER BY s.id, p.trade_date
                """,
                session.Connection,
                session.Transaction);
            command.Parameters.AddWithValue("ids", batch.Select(id => id.Value).ToArray());
            command.Parameters.AddWithValue("days", days);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = StockId.Create(reader.GetGuid(0));
                if (!recent.TryGetValue(id, out List<decimal>? closes))
                {
                    recent[id] = closes = [];
                }

                closes.Add(reader.GetDecimal(1));
            }
        }

        return recent.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<decimal>)pair.Value);
    }
}
