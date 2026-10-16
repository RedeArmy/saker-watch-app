using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>The job's lease was taken over by another worker, so this attempt's work must not count.</summary>
public sealed class LeaseLostException(Guid jobId)
    : InvalidOperationException($"The lease of job {jobId} was lost to another worker.");

/// <summary>
/// The job cannot run however often it is retried: its type is unknown, its payload does not fit the type, or no handler is registered
/// for it. The job is parked as dead at once instead of burning every attempt and its backoff.
/// </summary>
public sealed class JobPayloadException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JobPayloadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Runs queued jobs with leases and fencing (ADR-0005, ADR-0017). A job is claimed with <c>FOR UPDATE SKIP LOCKED</c> and
/// reserved for a lease with a fresh token; the worker renews the lease while the job runs. The handler's effects and the
/// completion run in one unit of work and the completion only succeeds while the token still matches, so a worker that lost
/// its lease can never commit. Failures retry with backoff and jitter and end as <c>dead</c> after the maximum attempts; an
/// abandoned lease (crashed worker) is reclaimed after it expires; shutdown releases the lease.
/// </summary>
public sealed partial class JobProcessor
{
    private static readonly Meter _meter = new("SakerWatch");
    private static readonly Counter<long> _jobs = _meter.CreateCounter<long>(
        "sakerwatch_jobs_total",
        description: "Job attempts by type and outcome (success, retry, dead, lease_lost, released).");
    private static readonly Counter<long> _deadLetters = _meter.CreateCounter<long>(
        "sakerwatch_jobs_dead_letter_total",
        description: "Jobs parked as dead after exhausting their attempts.");
    private static readonly Histogram<double> _duration = _meter.CreateHistogram<double>(
        "sakerwatch_jobs_duration_seconds",
        unit: "s",
        description: "Duration of job attempts by type and outcome.");
    private static readonly ActivitySource _activities = new("SakerWatch");

    // Recording a failure or releasing a lease happens while the worker may be shutting down; a hung database must not block it.
    private const string _tokenParameter = "token";

    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly JobRegistry _registry;
    private readonly JobOptions _options;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ExponentialBackoff _backoff;
    private readonly ILogger<JobProcessor> _logger;
    private readonly string _worker = $"{Environment.MachineName}:{Guid.CreateVersion7():N}";
    private long _nextParkCheckTicks;

    /// <summary>Creates a processor.</summary>
    public JobProcessor(
        IServiceScopeFactory scopes,
        JobRegistry registry,
        IOptions<JobOptions> options,
        IOptions<PostgresOptions> postgres,
        TimeProvider timeProvider,
        ExponentialBackoff backoff,
        ILogger<JobProcessor> logger)
    {
        _scopes = scopes;
        _registry = registry;
        _options = options.Value;
        _connectionString = postgres.Value.Postgres;
        _timeProvider = timeProvider;
        _backoff = backoff;
        _logger = logger;
    }

    /// <summary>Claims and runs the next due job. Returns <see langword="false"/> when none is due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (ParkCheckIsDue())
        {
            await ParkExhaustedAsync(cancellationToken);
        }

        ClaimedJob? job = await ClaimAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        await RunAsync(job, cancellationToken);
        return true;
    }

    private async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        using var leaseToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopHeartbeat = new CancellationTokenSource();
        int lost = 0;
        Task heartbeat = HeartbeatAsync(job, () =>
        {
            Volatile.Write(ref lost, 1);
            leaseToken.Cancel();
        }, stopHeartbeat.Token);

        using Activity? activity = _activities.StartActivity($"Job.Run {job.Type}");
        activity?.SetTag("saker.job.type", job.Type);
        activity?.SetTag("saker.job.key", job.Key);
        activity?.SetTag("saker.origin_trace_id", job.TraceId);
        long start = _timeProvider.GetTimestamp();
        string outcome = "success";
        try
        {
            object payload = DeserializePayload(job);
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = job.CorrelationId;
            await session.BeginAsync(leaseToken.Token);
            await InvokeAsync(scope.ServiceProvider, payload, new JobContext(job.Id, job.Key, job.Attempts, job.MaxAttempts, job.CorrelationId), leaseToken.Token);
            await CompleteAsync(session, job, leaseToken.Token);
            await session.CommitAsync(CancellationToken.None);
        }
        catch (LeaseLostException)
        {
            await session.RollbackAsync(CancellationToken.None);
            outcome = "lease_lost";
            LeaseLost(job.Type, job.Id);
        }
        catch (JobPayloadException ex)
        {
            await session.RollbackAsync(CancellationToken.None);
            outcome = await FailAsync(job, ex, permanent: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker is stopping: undo the attempt, hand the job back, and let the stop proceed.
            await session.RollbackAsync(CancellationToken.None);
            await ReleaseAsync(job);
            outcome = "released";
            throw;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref lost) == 1)
        {
            await session.RollbackAsync(CancellationToken.None);
            outcome = "lease_lost";
            LeaseLost(job.Type, job.Id);
        }
        catch (Exception ex)
        {
            await session.RollbackAsync(CancellationToken.None);
            outcome = await FailAsync(job, ex);
        }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
            TagList tags = new() { { "type", job.Type }, { "outcome", outcome } };
            _jobs.Add(1, tags);
            _duration.Record(_timeProvider.GetElapsedTime(start).TotalSeconds, tags);
        }
    }

    private async Task HeartbeatAsync(ClaimedJob job, Action onLost, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.LeaseDuration / 3, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    if (!await RenewAsync(job, stop))
                    {
                        onLost();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Any trouble renewing (a database blip, a disposed connection) must neither kill the job nor fault
                    // this task, which is awaited in a finally block; the lease may still be valid.
                    RenewFailed(ex, job.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished or the worker stopped: nothing more to renew.
        }
    }

    // An unknown type or a payload that does not fit it will not get better with a retry.
    private object DeserializePayload(ClaimedJob job)
    {
        try
        {
            return _registry.Deserialize(job.Type, job.Payload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new JobPayloadException(ex.Message, ex);
        }
    }

    private static async Task InvokeAsync(IServiceProvider services, object payload, JobContext context, CancellationToken cancellationToken)
    {
        Type handlerType = typeof(IJobHandler<>).MakeGenericType(payload.GetType());
        object handler = services.GetService(handlerType)
            ?? throw new JobPayloadException($"No handler is registered for job payload {payload.GetType().FullName}.");
        MethodInfo handle = handlerType.GetMethod(nameof(IJobHandler<object>.HandleAsync))!;

        try
        {
            await (Task)handle.Invoke(handler, [payload, context, cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var claim = new NpgsqlCommand(
            """
            UPDATE platform.job_queue AS j
            SET state = 'leased', attempts = j.attempts + 1, lease_token = @token, lease_expires_at = @expires, leased_by = @worker
            WHERE j.id = (
                SELECT id FROM platform.job_queue
                WHERE ((state = 'pending' AND available_at <= @now) OR (state = 'leased' AND lease_expires_at <= @now))
                  AND attempts < max_attempts
                ORDER BY available_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING j.id, j.job_key, j.type, j.payload::text, j.attempts, j.max_attempts, j.correlation_id, j.trace_id, j.lease_token
            """,
            connection);
        claim.Parameters.AddWithValue(_tokenParameter, Guid.CreateVersion7());
        claim.Parameters.AddWithValue("expires", now + _options.LeaseDuration);
        claim.Parameters.AddWithValue("worker", _worker);
        claim.Parameters.AddWithValue("now", now);
        await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedJob(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetGuid(8));
    }

    // True for exactly one caller per ParkInterval: the polling loops of every worker share the work of this housekeeping query.
    private bool ParkCheckIsDue()
    {
        long now = _timeProvider.GetUtcNow().UtcTicks;
        long next = Interlocked.Read(ref _nextParkCheckTicks);
        return now >= next && Interlocked.CompareExchange(ref _nextParkCheckTicks, now + _options.ParkInterval.Ticks, next) == next;
    }

    // A job whose last allowed attempt crashed the worker would otherwise be reclaimed forever; park it instead.
    private async Task ParkExhaustedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var park = new NpgsqlCommand(
            """
            UPDATE platform.job_queue
            SET state = 'dead', finished_at = @now, lease_token = NULL, lease_expires_at = NULL, leased_by = NULL,
                last_error = coalesce(last_error, 'the lease expired on the last allowed attempt')
            WHERE state = 'leased' AND lease_expires_at <= @now AND attempts >= max_attempts
            """,
            connection);
        park.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
        int parked = await park.ExecuteNonQueryAsync(cancellationToken);
        if (parked > 0)
        {
            _deadLetters.Add(parked);
            ParkedAfterCrash(parked);
        }
    }

    private async Task CompleteAsync(DbSession session, ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var complete = new NpgsqlCommand(
            """
            UPDATE platform.job_queue
            SET state = 'succeeded', finished_at = @now, lease_token = NULL, lease_expires_at = NULL, leased_by = NULL, last_error = NULL
            WHERE id = @id AND lease_token = @token AND state = 'leased'
            """,
            session.Connection,
            session.Transaction);
        complete.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
        complete.Parameters.AddWithValue("id", job.Id);
        complete.Parameters.AddWithValue(_tokenParameter, job.LeaseToken);
        if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new LeaseLostException(job.Id);
        }
    }

    private async Task<bool> RenewAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var renew = new NpgsqlCommand(
            "UPDATE platform.job_queue SET lease_expires_at = @expires WHERE id = @id AND lease_token = @token AND state = 'leased'",
            connection);
        renew.Parameters.AddWithValue("expires", _timeProvider.GetUtcNow() + _options.LeaseDuration);
        renew.Parameters.AddWithValue("id", job.Id);
        renew.Parameters.AddWithValue(_tokenParameter, job.LeaseToken);
        return await renew.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // Recorded on its own connection after the failed unit of work rolled back; fenced by the lease token.
    private async Task<string> FailAsync(ClaimedJob job, Exception exception, bool permanent = false)
    {
        bool dead = permanent || job.Attempts >= job.MaxAttempts;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        using var cleanup = new CancellationTokenSource(_cleanupTimeout);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cleanup.Token);
        await using var fail = new NpgsqlCommand(
            """
            UPDATE platform.job_queue
            SET state = @state, available_at = @next, last_error = @error, finished_at = @finished,
                lease_token = NULL, lease_expires_at = NULL, leased_by = NULL
            WHERE id = @id AND lease_token = @token AND state = 'leased'
            """,
            connection);
        fail.Parameters.AddWithValue("state", dead ? "dead" : "pending");
        fail.Parameters.AddWithValue("next", now + _backoff.Delay(job.Attempts));
        fail.Parameters.AddWithValue("error", ErrorText.Describe(exception));
        fail.Parameters.AddWithValue("finished", dead ? now : DBNull.Value);
        fail.Parameters.AddWithValue("id", job.Id);
        fail.Parameters.AddWithValue(_tokenParameter, job.LeaseToken);
        if (await fail.ExecuteNonQueryAsync(cleanup.Token) != 1)
        {
            LeaseLost(job.Type, job.Id);
            return "lease_lost";
        }

        if (dead)
        {
            _deadLetters.Add(1);
            Parked(exception, job.Type, job.Id, job.Attempts);
            return "dead";
        }

        WillRetry(exception, job.Type, job.Id, job.Attempts);
        return "retry";
    }

    // Shutdown: the interrupted attempt does not count, and the job is immediately available to another worker.
    private async Task ReleaseAsync(ClaimedJob job)
    {
        using var cleanup = new CancellationTokenSource(_cleanupTimeout);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cleanup.Token);
        await using var release = new NpgsqlCommand(
            """
            UPDATE platform.job_queue
            SET state = 'pending', attempts = attempts - 1, available_at = @now,
                lease_token = NULL, lease_expires_at = NULL, leased_by = NULL
            WHERE id = @id AND lease_token = @token AND state = 'leased'
            """,
            connection);
        release.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
        release.Parameters.AddWithValue("id", job.Id);
        release.Parameters.AddWithValue(_tokenParameter, job.LeaseToken);
        await release.ExecuteNonQueryAsync(cleanup.Token);
    }

    [LoggerMessage(EventId = 9511, Level = LogLevel.Warning, Message = "Job {Type} {JobId} failed on attempt {Attempt}; it will retry")]
    private partial void WillRetry(Exception exception, string type, Guid jobId, int attempt);

    [LoggerMessage(EventId = 9512, Level = LogLevel.Error, Message = "Job {Type} {JobId} parked as dead after {Attempts} attempts")]
    private partial void Parked(Exception exception, string type, Guid jobId, int attempts);

    [LoggerMessage(EventId = 9513, Level = LogLevel.Warning, Message = "Job {Type} {JobId} lost its lease; this attempt was rolled back")]
    private partial void LeaseLost(string type, Guid jobId);

    [LoggerMessage(EventId = 9514, Level = LogLevel.Error, Message = "{Count} job(s) parked as dead because their lease expired on the last allowed attempt")]
    private partial void ParkedAfterCrash(int count);

    [LoggerMessage(EventId = 9515, Level = LogLevel.Warning, Message = "Renewing the lease of job {JobId} failed; it will be retried on the next tick")]
    private partial void RenewFailed(Exception exception, Guid jobId);

    private sealed record ClaimedJob(Guid Id, string Key, string Type, string Payload, int Attempts, int MaxAttempts, string? CorrelationId, string? TraceId, Guid LeaseToken);
}
