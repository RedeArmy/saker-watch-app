// Throwaway job payload and handler that stand in for a real module's background work.
using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Application.Jobs;

namespace SakerWatch.Demo.IntegrationFeatures;

[JobType("demo.compute.v1")]
internal sealed record ComputeJob(string Name);

/// <summary>Controls the demo handler: failures, holding a run open, and whether it honors cancellation.</summary>
internal sealed class JobProbe
{
    private int _failuresRemaining;
    private TaskCompletionSource? _gate;
    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runs;

    public bool AlwaysFail { get; set; }

    public string FailureMessage { get; set; } = "job failed on purpose";

    public bool IgnoreCancellation { get; set; }

    /// <summary>Makes the handler cut its own database connection and fail, so the clean-up after the failure cannot reach the database either.</summary>
    public bool KillOwnConnection { get; set; }

    public int Runs => Volatile.Read(ref _runs);

    public Task Entered => _entered.Task;

    public List<int> Attempts { get; } = [];

    /// <summary>For each run: the current user the database session was tagged with (empty for none), and the one in the job context.</summary>
    public List<(string Session, Guid? Context)> UsersSeen { get; } = [];

    public void FailNext(int times) => Volatile.Write(ref _failuresRemaining, times);

    public bool ShouldFail() => AlwaysFail || Interlocked.Decrement(ref _failuresRemaining) >= 0;

    public void Hold()
    {
        _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() => _gate?.TrySetResult();

    public async Task OnRunAsync(int attempt, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        lock (Attempts)
        {
            Attempts.Add(attempt);
        }

        if (ShouldFail())
        {
            throw new InvalidOperationException(FailureMessage);
        }

        if (_gate is { } gate)
        {
            _entered.TrySetResult();
            await (IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken));
        }
    }
}

internal sealed class ComputeJobHandler(ProbeItemsContext db, JobProbe probe) : IJobHandler<ComputeJob>
{
    public async Task HandleAsync(ComputeJob payload, JobContext context, CancellationToken cancellationToken)
    {
        if (probe.KillOwnConnection)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_terminate_backend(pg_backend_pid())", cancellationToken);
        }

        List<string> session = await db.Database
            .SqlQueryRaw<string>("SELECT coalesce(current_setting('app.current_user_id', true), '') AS \"Value\"")
            .ToListAsync(cancellationToken);
        lock (probe.UsersSeen)
        {
            probe.UsersSeen.Add((session.Single(), context.UserId));
        }

        db.Items.Add(new ProbeItemRow { Id = Guid.CreateVersion7(), Name = payload.Name });
        await db.SaveChangesAsync(cancellationToken);
        await probe.OnRunAsync(context.Attempt, cancellationToken);
    }
}
