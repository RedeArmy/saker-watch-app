namespace SakerWatch.BuildingBlocks.Kernel.Security;

/// <summary>
/// The validated caller of the current request or job. The web layer implements it from the validated token; the
/// user id is never taken from client input (ADR-0010). Database sessions tag it for row-level security and the
/// idempotency behavior scopes keys by it.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The internal user id, or <see langword="null"/> when there is no authenticated user (workers, anonymous calls).</summary>
    Guid? UserId { get; }
}

/// <summary>Default caller: nobody, so row-level security exposes no user-owned rows and idempotent commands are refused.</summary>
public sealed class AnonymousCurrentUser : ICurrentUser
{
    /// <inheritdoc />
    public Guid? UserId => null;
}
