// Persistence rows: columns documented in docs/data-model.md.
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary>
/// <c>identity.users</c>: one row per person, keyed by the internal UUIDv7 and unique on the identity-provider pair
/// <c>(external_issuer, external_subject)</c>. The provider subject is never a foreign key elsewhere (ADR-0009).
/// </summary>
public sealed class UserRow
{
    public Guid Id { get; set; }

    public string ExternalIssuer { get; set; } = string.Empty;

    public string ExternalSubject { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public string Locale { get; set; } = "es-GT";

    public string Status { get; set; } = UserStatuses.Active;

    public DateTimeOffset? DeletionRequestedAt { get; set; }

    public DateTimeOffset? DeletionDueAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Allowed values of <see cref="UserRow.Status"/>.</summary>
public static class UserStatuses
{
    public const string Active = "active";

    public const string PendingDeletion = "pending_deletion";

    public const string Deleted = "deleted";
}

/// <summary>
/// <c>identity.deletion_tombstones</c>: minimal record kept until the identity-provider deletion succeeds
/// (ADR-0009). Not user-readable; only the deletion job touches it.
/// </summary>
public sealed class DeletionTombstoneRow
{
    public Guid UserId { get; set; }

    public string ExternalIssuer { get; set; } = string.Empty;

    public string ExternalSubject { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? IdpDeletedAt { get; set; }
}
