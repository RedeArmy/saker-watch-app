namespace SakerWatch.Identity.Contracts;

/// <summary>Outcome of asking the identity provider to delete an account.</summary>
public enum ProviderDeletionOutcome
{
    /// <summary>The provider deleted the identity.</summary>
    Deleted,

    /// <summary>The provider no longer knows the identity; deleting twice is therefore safe.</summary>
    AlreadyAbsent,

    /// <summary>The call failed in a way worth retrying (network, 5xx, throttling).</summary>
    TransientFailure,

    /// <summary>The call was refused and retrying will not help (credential or permission problem).</summary>
    PermanentFailure,
}

/// <summary>
/// Management operations on the external identity provider, used only by the account-deletion saga (Phase 6, ADR-0009).
/// It is a separate port from <see cref="IAuthProvider"/> because it needs a different, far more privileged credential that
/// only the Worker holds. The operation is idempotent: asking again for an identity that is gone reports
/// <see cref="ProviderDeletionOutcome.AlreadyAbsent"/>.
/// </summary>
public interface IIdentityProviderAdmin
{
    /// <summary>Deletes the identity <paramref name="subject"/> issued by <paramref name="issuer"/>.</summary>
    Task<ProviderDeletionOutcome> DeleteIdentityAsync(string issuer, string subject, CancellationToken cancellationToken);
}
