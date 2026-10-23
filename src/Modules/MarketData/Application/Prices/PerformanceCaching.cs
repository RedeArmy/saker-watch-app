using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Prices;

/// <summary>How the daily numbers are cached. Bound from <c>MarketData:Cache</c> (ADR-0041).</summary>
public sealed class PerformanceCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData:Cache";

    /// <summary>Whether the performance windows are kept in the cache. Off, every read goes to the database.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a report is kept. A report is replaced by a new key whenever its listing loads data, so this only bounds the keys nobody asks for again.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(26);

    /// <summary>
    /// Whether a listing's windows are computed and kept by a job of their own as soon as its data has loaded (ADR-0048), so that the first user to read it
    /// after the nightly load does not pay for the computation. Has no effect while <see cref="Enabled"/> is off.
    /// </summary>
    public bool WarmAfterLoad { get; set; } = true;

    /// <summary>
    /// How many minutes of loads one warming job covers (ADR-0048). The loads of a market in the same slice share a job, which runs when the slice is over and
    /// reads the windows of all of them in one batch: a few jobs a night for a market instead of one for each listing.
    /// </summary>
    [Range(1, 120)]
    public int WarmBucketMinutes { get; set; } = 15;
}

/// <summary>
/// What a cached report was computed from: the listing, the moment its data last loaded and the setting the windows depend on. A change to any
/// of the three is a different key, so a stale report is never found, only left to expire (ADR-0041).
/// </summary>
/// <param name="Stock">The listing.</param>
/// <param name="Stamp">When ingestion last succeeded for the listing; every write of a price is in the same unit of work as that stamp.</param>
/// <param name="MaxGapDays">The gap tolerance the windows were computed with.</param>
public sealed record PerformanceCacheKey(StockId Stock, DateTimeOffset Stamp, int MaxGapDays);

/// <summary>Keeps performance reports by <see cref="PerformanceCacheKey"/>. Nothing in it is authoritative: a failure is a miss.</summary>
public interface IPerformanceCache
{
    /// <summary>The reports found for <paramref name="keys"/>, by listing; a key with no report, or whose report cannot be read, is left out.</summary>
    Task<IReadOnlyDictionary<StockId, PerformanceReport>> GetManyAsync(IReadOnlyCollection<PerformanceCacheKey> keys, CancellationToken cancellationToken);

    /// <summary>Keeps the reports for <paramref name="ttl"/>. A failure is swallowed: the next read is a miss.</summary>
    Task SetManyAsync(IReadOnlyDictionary<PerformanceCacheKey, PerformanceReport> reports, TimeSpan ttl, CancellationToken cancellationToken);
}

/// <summary>When ingestion last succeeded for each listing: the version of the data its performance windows are computed from.</summary>
public interface IIngestionStamps
{
    /// <summary>The last success of every listing in <paramref name="stocks"/> that has one; a listing never loaded is left out.</summary>
    Task<IReadOnlyDictionary<StockId, DateTimeOffset>> LastSuccessAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken);

    /// <summary>The active listings of <paramref name="market"/> whose last success is at or after <paramref name="from"/> and before <paramref name="to"/>.</summary>
    Task<IReadOnlyList<StockId>> LoadedBetweenAsync(string market, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>The outcomes of the performance cache, one count per listing asked for.</summary>
public static class PerformanceCacheMetrics
{
    /// <summary>Result tag of a listing found in the cache.</summary>
    public const string Hit = "hit";

    /// <summary>Result tag of a listing computed from the database.</summary>
    public const string Miss = "miss";

    /// <summary>Result tag of a read or a write the cache could not do.</summary>
    public const string Error = "error";

    private static readonly Counter<long> _lookups = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_perf_cache_total",
        description: "Listings asked of the performance cache by result (hit, miss, error).");

    /// <summary>Counts <paramref name="count"/> listings with <paramref name="result"/>.</summary>
    public static void Count(string result, long count)
    {
        if (count > 0)
        {
            _lookups.Add(count, new KeyValuePair<string, object?>("result", result));
        }
    }
}

/// <summary>
/// <see cref="IPerformanceReader"/> that serves the daily numbers from the cache and asks the database only for the listings it does not have
/// (ADR-0041). The windows change only when a listing loads data, so they are keyed by the listing's last successful load: a load makes a new
/// key and the old report is never read again. The price status is not cached; it depends on the time of the request.
/// </summary>
/// <param name="inner">The reader of the database.</param>
/// <param name="stamps">When each listing last loaded.</param>
/// <param name="cache">The cache.</param>
/// <param name="performance">The gap tolerance the windows depend on.</param>
/// <param name="options">Whether to cache and for how long.</param>
public sealed class CachedPerformanceReader(
    IPerformanceReader inner,
    IIngestionStamps stamps,
    IPerformanceCache cache,
    IOptions<PerformanceOptions> performance,
    IOptions<PerformanceCacheOptions> options) : IPerformanceReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stocks);
        if (!options.Value.Enabled)
        {
            return await inner.ReadAsync(stocks, cancellationToken);
        }

        StockId[] ids = [.. stocks.Distinct()];
        IReadOnlyDictionary<StockId, DateTimeOffset> stamped = await stamps.LastSuccessAsync(ids, cancellationToken);
        int maxGapDays = performance.Value.MaxGapDays;
        PerformanceCacheKey[] keys = [.. ids.Where(stamped.ContainsKey).Select(id => new PerformanceCacheKey(id, stamped[id], maxGapDays))];

        IReadOnlyDictionary<StockId, PerformanceReport> found = keys.Length == 0
            ? new Dictionary<StockId, PerformanceReport>()
            : await cache.GetManyAsync(keys, cancellationToken);
        var reports = new Dictionary<StockId, PerformanceReport>(found);
        StockId[] missing = [.. ids.Where(id => !reports.ContainsKey(id))];
        PerformanceCacheMetrics.Count(PerformanceCacheMetrics.Hit, reports.Count);
        PerformanceCacheMetrics.Count(PerformanceCacheMetrics.Miss, missing.Length);
        if (missing.Length == 0)
        {
            return reports;
        }

        IReadOnlyDictionary<StockId, PerformanceReport> computed = await inner.ReadAsync(missing, cancellationToken);
        var toKeep = new Dictionary<PerformanceCacheKey, PerformanceReport>();
        foreach ((StockId id, PerformanceReport report) in computed)
        {
            reports[id] = report;
            if (report.Latest is not null && stamped.TryGetValue(id, out DateTimeOffset stamp))
            {
                toKeep[new PerformanceCacheKey(id, stamp, maxGapDays)] = report;
            }
        }

        if (toKeep.Count > 0)
        {
            await cache.SetManyAsync(toKeep, options.Value.Ttl, cancellationToken);
        }

        return reports;
    }
}
