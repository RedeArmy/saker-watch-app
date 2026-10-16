using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>
/// The current user of a job's scope: the user the job works for, or nobody for system work. The processor sets it before the job's
/// unit of work begins, and everything that asks for <see cref="ICurrentUser"/> inside the job (the database session that tags row-level
/// security, the outbox, the idempotency behavior) sees it. It is never taken from the payload, only from the queue row that the
/// database accepted at enqueue time.
/// </summary>
public sealed class JobCurrentUser : ICurrentUser
{
    /// <inheritdoc />
    public Guid? UserId { get; set; }
}
