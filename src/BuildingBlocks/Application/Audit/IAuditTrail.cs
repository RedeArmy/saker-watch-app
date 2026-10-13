namespace SakerWatch.BuildingBlocks.Application.Audit;

/// <summary>One security-relevant event for the append-only audit log (ADR-0016).</summary>
/// <param name="EventType">Stable event name, for example <c>user.provisioned</c>.</param>
/// <param name="ActorUserId">The user who acted (stored only as a keyed hash); <see langword="null"/> for the system.</param>
/// <param name="SubjectRef">Pseudonymous reference to what was acted on, if different from the actor. Never an email or name.</param>
/// <param name="Details">Small, non-personal details serialized as JSON.</param>
public sealed record AuditEvent(string EventType, Guid? ActorUserId, string? SubjectRef = null, object? Details = null);

/// <summary>Writes to <c>platform.audit_log</c>. Call it inside the unit of work of the change being audited, so both commit together.</summary>
public interface IAuditTrail
{
    /// <summary>Appends <paramref name="auditEvent"/> to the hash-chained log.</summary>
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
