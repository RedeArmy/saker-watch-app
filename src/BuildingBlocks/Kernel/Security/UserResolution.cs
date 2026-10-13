using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Kernel.Security;

/// <summary>State of an account in Saker Watch (docs/data-model.md).</summary>
public enum UserAccountStatus
{
    /// <summary>The account can be used.</summary>
    Active,

    /// <summary>Deletion was requested and is in its grace period; the account cannot be used.</summary>
    PendingDeletion,

    /// <summary>The account is deleted.</summary>
    Deleted,
}

/// <summary>The identity proven by a validated token: the pair <see cref="Issuer"/> and <see cref="Subject"/> is the key (ADR-0009).</summary>
/// <param name="Issuer">Identity provider issuer.</param>
/// <param name="Subject">Stable account id at the provider.</param>
/// <param name="Email">Profile email, if the token carries one.</param>
/// <param name="DisplayName">Profile name, if any.</param>
/// <param name="Locale">Profile locale, if any.</param>
public sealed record ExternalIdentity(string Issuer, string Subject, string? Email, string? DisplayName, string? Locale);

/// <summary>The internal user an external identity maps to.</summary>
/// <param name="UserId">Internal UUIDv7; the only id used as a foreign key anywhere.</param>
/// <param name="Status">Account state.</param>
public sealed record ResolvedUser(Guid UserId, UserAccountStatus Status);

/// <summary>
/// Maps a validated external identity to the internal user, creating it on first sight (just-in-time provisioning, ADR-0009).
/// The web layer calls it once per request after authentication.
/// </summary>
public interface IUserResolver
{
    /// <summary>Finds or creates the user for <paramref name="identity"/>.</summary>
    Task<Result<ResolvedUser>> ResolveAsync(ExternalIdentity identity, CancellationToken cancellationToken);
}
