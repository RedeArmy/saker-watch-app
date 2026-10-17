using SakerWatch.BuildingBlocks.Application.Jobs;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>A job queue that remembers what was queued and, like the real one, ignores a key it has already seen.</summary>
internal sealed class RecordingEnqueuer : IJobEnqueuer
{
    public List<(string Key, object Payload, JobEnqueueOptions? Options)> Queued { get; } = [];

    public Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
        where TPayload : notnull
    {
        bool created = Queued.TrueForAll(q => q.Key != jobKey);
        if (created)
        {
            Queued.Add((jobKey, payload, options));
        }

        return Task.FromResult(created);
    }
}
