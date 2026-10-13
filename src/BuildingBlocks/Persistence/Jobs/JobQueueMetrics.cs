using System.Diagnostics.Metrics;
using Npgsql;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>
/// Samples the queue periodically and exposes it as gauges (ADR-0016): <c>sakerwatch_jobs_queue_depth</c> (due or waiting),
/// <c>sakerwatch_jobs_queue_oldest_age_seconds</c> (how long the oldest due job has waited, the staleness alert),
/// <c>sakerwatch_jobs_in_flight</c> and <c>sakerwatch_jobs_dead</c> (parked jobs awaiting a human).
/// </summary>
public sealed class JobQueueMetrics : IDisposable
{
    private readonly Meter _meter = new("SakerWatch");
    private long _depth;
    private double _oldestAgeSeconds;
    private long _inFlight;
    private long _dead;

    /// <summary>Creates the gauges.</summary>
    public JobQueueMetrics()
    {
        _meter.CreateObservableGauge("sakerwatch_jobs_queue_depth", () => Volatile.Read(ref _depth), description: "Jobs waiting to run.");
        _meter.CreateObservableGauge("sakerwatch_jobs_queue_oldest_age_seconds", () => Volatile.Read(ref _oldestAgeSeconds), unit: "s", description: "Age of the oldest due job.");
        _meter.CreateObservableGauge("sakerwatch_jobs_in_flight", () => Volatile.Read(ref _inFlight), description: "Jobs currently leased.");
        _meter.CreateObservableGauge("sakerwatch_jobs_dead", () => Volatile.Read(ref _dead), description: "Jobs parked as dead.");
    }

    /// <summary>Reads the queue and updates the gauges.</summary>
    public async Task RefreshAsync(string connectionString, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var query = new NpgsqlCommand(
            """
            SELECT count(*) FILTER (WHERE state = 'pending'),
                   count(*) FILTER (WHERE state = 'leased'),
                   count(*) FILTER (WHERE state = 'dead'),
                   coalesce(extract(epoch FROM (@now - min(available_at) FILTER (WHERE state = 'pending' AND available_at <= @now))), 0)
            FROM platform.job_queue
            """,
            connection);
        query.Parameters.AddWithValue("now", now);
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            Volatile.Write(ref _depth, reader.GetInt64(0));
            Volatile.Write(ref _inFlight, reader.GetInt64(1));
            Volatile.Write(ref _dead, reader.GetInt64(2));
            Volatile.Write(ref _oldestAgeSeconds, Convert.ToDouble(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
