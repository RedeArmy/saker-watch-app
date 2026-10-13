namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// The validated caller whose rows a database session may see. The web layer implements it from the validated
/// token; the user id is never taken from client input (ADR-0010).
/// </summary>
public interface IDbUserContext
{
    /// <summary>The internal user id, or <see langword="null"/> when there is no authenticated user (workers, anonymous calls).</summary>
    Guid? CurrentUserId { get; }
}

/// <summary>Default context: no user, so row-level security exposes no user-owned rows.</summary>
public sealed class AnonymousDbUserContext : IDbUserContext
{
    /// <inheritdoc />
    public Guid? CurrentUserId => null;
}
