using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>
/// Computes and keeps the performance windows of the listings of one market that loaded during one slice of time (ADR-0048). The loads of a market in the
/// same slice share one job, so a night of loads is a few jobs and not one for each listing.
/// </summary>
/// <param name="Market">The canonical market code.</param>
/// <param name="From">The start of the slice (UTC).</param>
/// <param name="To">The end of the slice (UTC); the job runs after it.</param>
[JobType("marketdata.warm-performance.v2")]
public sealed record WarmPerformanceJob(string Market, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>The slice that <paramref name="loadedAt"/> falls in, of <paramref name="minutes"/> minutes counted from the Unix epoch (so they follow the clock when <paramref name="minutes"/> divides an hour).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) SliceOf(DateTimeOffset loadedAt, int minutes)
    {
        long slice = TimeSpan.FromMinutes(minutes).Ticks;
        long start = loadedAt.UtcTicks - (loadedAt.UtcTicks % slice);
        return (new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(start + slice, TimeSpan.Zero));
    }

    /// <summary>The deterministic job key of the warming of a market's slice.</summary>
    public static string KeyFor(Mic market, DateTimeOffset from) =>
        $"marketdata.warm-performance:{market.Value}:{from.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Reads the windows of every listing of the market that loaded in the slice through the cached reader, in one batch, which computes them for the stamp
/// each load left and keeps them: the first user to ask for a listing after the nightly load then finds them. It is the same read a user would make, made
/// when the worker has nothing better to do; a failure of the cache is a miss as ever. The reads count as misses in the cache metric (that is what they are).
/// </summary>
public sealed class WarmPerformanceHandler(IPerformanceReader reader, IIngestionStamps stamps) : IJobHandler<WarmPerformanceJob>
{
    private static readonly Counter<long> _warmed = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_perf_warm_total",
        description: "Listings whose performance windows were computed ahead of the first reader, by outcome (warmed, no_price).");

    /// <inheritdoc />
    public async Task HandleAsync(WarmPerformanceJob payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var loaded = await stamps.LoadedBetweenAsync(payload.Market, payload.From, payload.To, cancellationToken);
        if (loaded.Count == 0)
        {
            return;
        }

        var reports = await reader.ReadAsync(loaded, cancellationToken);

        int withPrice = reports.Values.Count(report => report.Latest is not null);
        _warmed.Add(withPrice, new KeyValuePair<string, object?>("outcome", "warmed"));
        _warmed.Add(loaded.Count - withPrice, new KeyValuePair<string, object?>("outcome", "no_price"));
    }
}
