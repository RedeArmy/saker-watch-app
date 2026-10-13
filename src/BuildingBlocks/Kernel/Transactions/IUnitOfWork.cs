namespace SakerWatch.BuildingBlocks.Kernel.Transactions;

/// <summary>
/// One atomic unit of work spanning everything a use case writes (every module context, the idempotency row and the
/// outbox). Calls nest: only the outermost commit commits, and a rollback anywhere makes the whole unit roll back.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Starts the unit of work, or joins the one already active.</summary>
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>Ends the active unit; the outermost call commits.</summary>
    /// <exception cref="InvalidOperationException">A nested unit already rolled back, so the outer one cannot commit.</exception>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Marks the unit as failed; the outermost call rolls everything back. Safe to call when nothing is active.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Default for hosts without a database session: nothing to coordinate.</summary>
public sealed class NoOpUnitOfWork : IUnitOfWork
{
    /// <inheritdoc />
    public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
