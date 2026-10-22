using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;
using StackExchange.Redis;

namespace SakerWatch.MarketData.Infrastructure.Providers;

/// <summary>
/// <see cref="IPerformanceCache"/> over Redis (ADR-0041): one string per listing under <c>v1:perf:{stock}:{stamp}:{maxGap}:c{calculation version}</c> (the version of <see cref="PerformanceCalculator"/>, so a change of the rules is a new key). The report is stored as
/// text (dates ISO, decimals invariant) and the changes are not stored at all: they are rebuilt from the two closes by the same function the
/// calculator uses, so the cache cannot hold a number the calculator would not produce. Nothing here is authoritative: a Redis failure, an entry
/// that cannot be read or a version bump is a miss, never an error for the request.
/// </summary>
/// <param name="redis">The shared connection.</param>
/// <param name="logger">Logger.</param>
public sealed partial class RedisPerformanceCache(IConnectionMultiplexer redis, ILogger<RedisPerformanceCache> logger) : IPerformanceCache
{
    private const string _dateFormat = "yyyy-MM-dd";
    private const int _format = 1;

    private sealed record StoredWindow(int Window, string? BaselineDate, string? BaselineClose, int? Gap);

    private sealed record Stored(int Format, string? LatestDate, string? LatestClose, StoredWindow[] Windows);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<StockId, PerformanceReport>> GetManyAsync(IReadOnlyCollection<PerformanceCacheKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var found = new Dictionary<StockId, PerformanceReport>();
        if (keys.Count == 0)
        {
            return found;
        }

        PerformanceCacheKey[] ordered = [.. keys];
        try
        {
            RedisValue[] values = await redis.GetDatabase().StringGetAsync([.. ordered.Select(KeyFor)]);
            for (int i = 0; i < ordered.Length; i++)
            {
                if (values[i].HasValue && TryRead(values[i].ToString()) is { } report)
                {
                    found[ordered[i].Stock] = report;
                }
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            PerformanceCacheMetrics.Count(PerformanceCacheMetrics.Error, ordered.Length);
            LogUnavailable(ex.GetType().Name);
            found.Clear();
        }

        return found;
    }

    /// <inheritdoc />
    public async Task SetManyAsync(IReadOnlyDictionary<PerformanceCacheKey, PerformanceReport> reports, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IBatch batch = redis.GetDatabase().CreateBatch();
            Task[] writes = [.. reports.Select(entry => batch.StringSetAsync(KeyFor(entry.Key), Write(entry.Value), ttl))];
            batch.Execute();
            await Task.WhenAll(writes);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            PerformanceCacheMetrics.Count(PerformanceCacheMetrics.Error, reports.Count);
            LogUnavailable(ex.GetType().Name);
        }
    }

    private static RedisKey KeyFor(PerformanceCacheKey key) => RedisKeys.For(
        "perf",
        key.Stock.Value.ToString("D", CultureInfo.InvariantCulture),
        key.Stamp.UtcTicks.ToString(CultureInfo.InvariantCulture),
        key.MaxGapDays.ToString(CultureInfo.InvariantCulture),
        "c" + PerformanceCalculator.Version.ToString(CultureInfo.InvariantCulture));

    private static string Write(PerformanceReport report) => JsonSerializer.Serialize(new Stored(
        _format,
        report.Latest?.Date.ToString(_dateFormat, CultureInfo.InvariantCulture),
        report.Latest?.AdjustedClose.ToString(CultureInfo.InvariantCulture),
        [.. report.Windows.Select(window => new StoredWindow(
            (int)window.Window,
            window.Baseline?.Date.ToString(_dateFormat, CultureInfo.InvariantCulture),
            window.Baseline?.AdjustedClose.ToString(CultureInfo.InvariantCulture),
            window.Gap is { } gap ? (int)gap : null))]));

    // An entry that is not exactly what Write produces is a miss: the cache never guesses.
    private PerformanceReport? TryRead(string text)
    {
        try
        {
            Stored? stored = JsonSerializer.Deserialize<Stored>(text);
            if (stored is null || stored.Format != _format || stored.LatestDate is null || stored.LatestClose is null || stored.Windows.Length != Enum.GetValues<PerformanceWindow>().Length)
            {
                return Unreadable();
            }

            PriceSample latest = Sample(stored.LatestDate, stored.LatestClose);
            var windows = new List<WindowPerformance>(stored.Windows.Length);
            foreach ((StoredWindow window, PerformanceWindow expected) in stored.Windows.Zip(Enum.GetValues<PerformanceWindow>()))
            {
                if (window.Window != (int)expected)
                {
                    return Unreadable();
                }

                if (window.Gap is { } gap)
                {
                    windows.Add(Enum.IsDefined((PerformanceGap)gap) ? WindowPerformance.Missing(expected, (PerformanceGap)gap) : throw new FormatException());
                    continue;
                }

                PriceSample baseline = Sample(window.BaselineDate!, window.BaselineClose!);
                windows.Add(WindowPerformance.Of(expected, RelativeChange.Between(baseline.AdjustedClose, latest.AdjustedClose).Value, baseline));
            }

            return new PerformanceReport(latest, windows);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException or InvalidOperationException)
        {
            return Unreadable();
        }
    }

    private PerformanceReport? Unreadable()
    {
        PerformanceCacheMetrics.Count(PerformanceCacheMetrics.Error, 1);
        LogEntryUnreadable();
        return null;
    }

    private static PriceSample Sample(string date, string close) =>
        PriceSample.Create(DateOnly.ParseExact(date, _dateFormat, CultureInfo.InvariantCulture), decimal.Parse(close, NumberStyles.Number, CultureInfo.InvariantCulture)).Value;

    [LoggerMessage(EventId = 2801, Level = LogLevel.Warning, Message = "The performance cache could not be used ({ExceptionType}); the numbers come from the database")]
    private partial void LogUnavailable(string exceptionType);

    [LoggerMessage(EventId = 2802, Level = LogLevel.Warning, Message = "A cached performance report could not be read and is computed again")]
    private partial void LogEntryUnreadable();
}
