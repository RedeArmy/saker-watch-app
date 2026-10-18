using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPerformanceReader"/> over <c>marketdata.price_daily</c>. A listing needs nine prices (the newest and one baseline per window), not
/// five years of rows, so the database does the lookups: one statement finds the newest close of every listing, and a second finds, for every
/// (listing, target date) pair, the last close on or before it. Both walk the primary key backwards and stop at the first row.
/// </summary>
/// <param name="session">The scope's database connection and transaction.</param>
/// <param name="calendars">The exchange calendars, for the previous trading day of the one-day window.</param>
/// <param name="options">How far before a window's start a baseline may lie.</param>
/// <param name="logger">Logger.</param>
public sealed partial class PerformanceReader(
    DbSession session,
    ExchangeCalendars calendars,
    IOptions<PerformanceOptions> options,
    ILogger<PerformanceReader> logger) : IPerformanceReader
{
    // 500 listings are about 4 000 target dates, a statement of a size the planner handles in a few milliseconds.
    private const int _batchSize = 500;

    private const string _latest = """
        SELECT s.id, s.market_mic, p.trade_date, p.adjusted_close
        FROM unnest(@ids) AS s0(id)
        JOIN marketdata.stocks s ON s.id = s0.id
        CROSS JOIN LATERAL (
            SELECT trade_date, adjusted_close FROM marketdata.price_daily
            WHERE stock_id = s.id ORDER BY trade_date DESC LIMIT 1) p
        """;

    private const string _baselines = """
        SELECT t.stock_id, t.target, p.trade_date, p.adjusted_close
        FROM unnest(@stocks, @targets) AS t(stock_id, target)
        CROSS JOIN LATERAL (
            SELECT trade_date, adjusted_close FROM marketdata.price_daily
            WHERE stock_id = t.stock_id AND trade_date <= t.target ORDER BY trade_date DESC LIMIT 1) p
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stocks);

        var reports = new Dictionary<StockId, PerformanceReport>();
        foreach (StockId[] batch in stocks.Distinct().Chunk(_batchSize))
        {
            await ReadBatchAsync(batch, reports, cancellationToken);
        }

        return reports;
    }

    private async Task ReadBatchAsync(StockId[] batch, Dictionary<StockId, PerformanceReport> reports, CancellationToken cancellationToken)
    {
        Dictionary<StockId, (Mic Market, PriceSample Latest)> latest = await LatestAsync(batch, cancellationToken);
        var calendarOf = new Dictionary<StockId, IExchangeCalendar?>();
        var withoutCalendar = new HashSet<string>();
        var targets = new List<(StockId Stock, DateOnly Target)>();
        foreach ((StockId stock, (Mic market, PriceSample sample)) in latest)
        {
            // Prices exist only for markets that had a calendar when they were ingested, so a missing one means it was removed or misspelled. The
            // listing is still served: only the one-day window needs the calendar, and it says so.
            IExchangeCalendar? calendar = calendars.TryGet(market) is { IsSuccess: true } found ? found.Value : null;
            if (calendar is null && withoutCalendar.Add(market.Value))
            {
                LogNoCalendar(market.Value);
            }

            calendarOf[stock] = calendar;
            targets.AddRange(PerformanceCalculator.TargetDates(sample.Date, calendar).Values.Distinct().Select(target => (stock, target)));
        }

        Dictionary<StockId, Dictionary<DateOnly, PriceSample?>> baselines = await BaselinesAsync(targets, cancellationToken);
        int maxGap = options.Value.MaxGapDays;
        foreach (StockId stock in batch)
        {
            reports[stock] = latest.TryGetValue(stock, out (Mic Market, PriceSample Latest) found)
                ? PerformanceCalculator.Calculate(found.Latest, baselines.GetValueOrDefault(stock) ?? [], calendarOf[stock], maxGap)
                : PerformanceCalculator.NoData();
        }
    }

    private async Task<Dictionary<StockId, (Mic Market, PriceSample Latest)>> LatestAsync(StockId[] batch, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_latest, session.Connection, session.Transaction);
        command.Parameters.AddWithValue("ids", batch.Select(stock => stock.Value).ToArray());

        var found = new Dictionary<StockId, (Mic, PriceSample)>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found[StockId.Create(reader.GetGuid(0))] = (Mic.Parse(reader.GetString(1)), PriceSample.Create(reader.GetFieldValue<DateOnly>(2), reader.GetDecimal(3)).Value);
        }

        return found;
    }

    private async Task<Dictionary<StockId, Dictionary<DateOnly, PriceSample?>>> BaselinesAsync(List<(StockId Stock, DateOnly Target)> targets, CancellationToken cancellationToken)
    {
        var found = new Dictionary<StockId, Dictionary<DateOnly, PriceSample?>>();
        if (targets.Count == 0)
        {
            return found;
        }

        await using var command = new NpgsqlCommand(_baselines, session.Connection, session.Transaction);
        command.Parameters.AddWithValue("stocks", targets.Select(t => t.Stock.Value).ToArray());
        command.Parameters.AddWithValue("targets", targets.Select(t => t.Target).ToArray());

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stock = StockId.Create(reader.GetGuid(0));
            if (!found.TryGetValue(stock, out Dictionary<DateOnly, PriceSample?>? byTarget))
            {
                found[stock] = byTarget = [];
            }

            byTarget[reader.GetFieldValue<DateOnly>(1)] = PriceSample.Create(reader.GetFieldValue<DateOnly>(2), reader.GetDecimal(3)).Value;
        }

        return found;
    }

    [LoggerMessage(EventId = 2401, Level = LogLevel.Warning, Message = "The market {Market} has stored prices but no exchange calendar; the one-day window of its listings is unavailable")]
    private partial void LogNoCalendar(string market);
}
