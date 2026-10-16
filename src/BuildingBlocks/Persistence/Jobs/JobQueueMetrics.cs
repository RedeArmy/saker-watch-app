using System.Diagnostics.Metrics;
using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>
/// Samples the queue periodically and exposes it as gauges (ADR-0016): <c>sakerwatch_jobs_queue_depth</c> (jobs waiting to run: pending
/// ones plus leased ones whose lease expired, which will be reclaimed), <c>sakerwatch_jobs_queue_oldest_age_seconds</c> (how long the
/// oldest due or abandoned job has waited, the staleness alert), <c>sakerwatch_jobs_in_flight</c> (leased with a live lease) and
/// <c>sakerwatch_jobs_dead</c> (parked jobs awaiting a human).
/// </summary>
public sealed class JobQueueMetrics : IDisposable
{
    private readonly Meter _meter = SakerTelemetry.CreateMeter();
    private long _depth;
    private double _oldestAgeSeconds;
    private long _inFlight;
    private long _dead;

    /// <summary>
    /// The sampling query: one small count per state, each shaped to the partial index of that state (pending by available_at, leased
    /// by lease_expires_at, dead by finished_at), so sampling never scans the finished rows that retention keeps for days.
    /// </summary>
    public const string SamplingSql =
        """
            SELECT
              (SELECT count(*) FROM platform.job_queue WHERE state = 'pending'),
              (SELECT count(*) FROM platform.job_queue WHERE state = 'leased' AND lease_expires_at > @now),
              (SELECT count(*) FROM platform.job_queue WHERE state = 'leased' AND lease_expires_at <= @now),
              (SELECT count(*) FROM platform.job_queue WHERE state = 'dead' AND finished_at IS NOT NULL),
              coalesce(extract(epoch FROM (@now - least(
                (SELECT min(available_at) FROM platform.job_queue WHERE state = 'pending' AND available_at <= @now),
                (SELECT min(lease_expires_at) FROM platform.job_queue WHERE state = 'leased' AND lease_expires_at <= @now)))), 0)
            """;

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
        await using var query = new NpgsqlCommand(SamplingSql, connection);
        query.Parameters.AddWithValue("now", now);
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            long pending = reader.GetInt64(0);
            long inFlight = reader.GetInt64(1);
            long abandoned = reader.GetInt64(2);
            Volatile.Write(ref _depth, pending + abandoned);
            Volatile.Write(ref _inFlight, inFlight);
            Volatile.Write(ref _dead, reader.GetInt64(3));
            Volatile.Write(ref _oldestAgeSeconds, Convert.ToDouble(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
