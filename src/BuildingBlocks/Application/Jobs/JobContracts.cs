namespace SakerWatch.BuildingBlocks.Application.Jobs;

/// <summary>
/// Gives a job payload the stable name it is stored and routed under (for example <c>analysis.run.v1</c>). Renaming the
/// CLR type must never orphan queued jobs, so the name is explicit and versioned.
/// </summary>
/// <param name="name">Stable job type name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class JobTypeAttribute(string name) : Attribute
{
    /// <summary>The stable job type name.</summary>
    public string Name { get; } = name;
}

/// <summary>Options for a new job.</summary>
/// <param name="AvailableAt">Do not run before this moment; <see langword="null"/> means now.</param>
/// <param name="MaxAttempts">Attempts before the job is parked as dead.</param>
public sealed record JobEnqueueOptions(DateTimeOffset? AvailableAt = null, int MaxAttempts = 5);

/// <summary>What a running job knows about itself.</summary>
/// <param name="JobId">Queue row id.</param>
/// <param name="JobKey">Deterministic key (for example <c>analysis:{stockId}:{promptVersion}:{date}</c>).</param>
/// <param name="Attempt">1 for the first run, increasing with every retry or takeover.</param>
/// <param name="MaxAttempts">Attempts allowed before the job is parked.</param>
/// <param name="CorrelationId">Correlation id of the request that enqueued it.</param>
public sealed record JobContext(Guid JobId, string JobKey, int Attempt, int MaxAttempts, string? CorrelationId);

/// <summary>Puts jobs on the durable queue (ADR-0005, ADR-0017).</summary>
public interface IJobEnqueuer
{
    /// <summary>
    /// Enqueues <paramref name="payload"/> under the deterministic <paramref name="jobKey"/>. Returns <see langword="true"/>
    /// when the job was created and <see langword="false"/> when a job with that key already exists (nothing changes), so
    /// callers may enqueue idempotently. Inside a unit of work the job commits or rolls back with it.
    /// </summary>
    Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
        where TPayload : notnull;
}

/// <summary>
/// Runs one kind of job. A handler may run more than once for the same job (lease expiry, retry), so it must be re-runnable.
/// The dispatcher runs it in a unit of work together with the fenced completion, so Postgres-only effects happen once.
/// </summary>
/// <typeparam name="TPayload">The payload type, annotated with <see cref="JobTypeAttribute"/>.</typeparam>
public interface IJobHandler<in TPayload>
    where TPayload : notnull
{
    /// <summary>Does the work. Throwing makes the job retry with backoff; the token is cancelled when the lease is lost or the worker stops.</summary>
    Task HandleAsync(TPayload payload, JobContext context, CancellationToken cancellationToken);
}
