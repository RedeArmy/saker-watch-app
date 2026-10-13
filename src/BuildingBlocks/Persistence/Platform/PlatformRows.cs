// Persistence rows: one class per table, columns documented in docs/data-model.md and ADR-0005/0017.
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace SakerWatch.BuildingBlocks.Persistence.Platform;

/// <summary><c>platform.idempotency_keys</c>: one row per (user, method, route, key) of a retryable request (ADR-0017). User-owned, protected by RLS.</summary>
public sealed class IdempotencyKeyRow
{
    public Guid UserId { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public byte[] RequestHash { get; set; } = [];

    public string State { get; set; } = IdempotencyStates.InProgress;

    public DateTimeOffset? LockedUntil { get; set; }

    public int? ResponseStatus { get; set; }

    public string? ResponseHeaders { get; set; }

    public byte[]? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? TraceId { get; set; }
}

/// <summary>Allowed values of <see cref="IdempotencyKeyRow.State"/>.</summary>
public static class IdempotencyStates
{
    public const string InProgress = "in_progress";

    public const string Completed = "completed";
}

/// <summary><c>platform.outbox_messages</c>: integration events written in the same transaction as the state change (ADR-0005).</summary>
public sealed class OutboxMessageRow
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; }

    public string? TraceId { get; set; }

    public string? CorrelationId { get; set; }

    public Guid? UserId { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary><c>platform.inbox_messages</c>: dedupe of consumed messages by (consumer, message id) (ADR-0017).</summary>
public sealed class InboxMessageRow
{
    public string Consumer { get; set; } = string.Empty;

    public Guid MessageId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary><c>platform.job_queue</c>: durable jobs with a deterministic key, leases and a fencing token (ADR-0005, ADR-0017).</summary>
public sealed class JobRow
{
    public Guid Id { get; set; }

    public string JobKey { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    public string State { get; set; } = JobStates.Pending;

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 5;

    public DateTimeOffset AvailableAt { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? LeasedBy { get; set; }

    public string? LastError { get; set; }

    public string? TraceId { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Allowed values of <see cref="JobRow.State"/>.</summary>
public static class JobStates
{
    public const string Pending = "pending";

    public const string Leased = "leased";

    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Dead = "dead";
}

/// <summary>
/// <c>platform.audit_log</c>: append-only, hash-chained security events. Written only through
/// <c>platform.append_audit(...)</c>; the application roles have no direct privileges on the table.
/// </summary>
public sealed class AuditLogRow
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string ActorRef { get; set; } = string.Empty;

    public string? SubjectRef { get; set; }

    public string Details { get; set; } = "{}";

    public string? TraceId { get; set; }

    public string? CorrelationId { get; set; }

    public byte[]? PrevHash { get; set; }

    public byte[] Hash { get; set; } = [];
}
