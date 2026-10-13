// Throwaway event and consumers that stand in for a real module's integration events.
using System.Collections.Concurrent;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Domain;

namespace SakerWatch.Demo.IntegrationFeatures;

[IntegrationEventName("demo.thing_created.v1")]
internal sealed record ThingCreated(Guid EventId, DateTimeOffset OccurredAt, string Name) : IDomainEvent;

/// <summary>Records what the consumers did and lets a test make the flaky consumer fail.</summary>
internal sealed class ConsumerProbe
{
    private int _failuresRemaining;

    public ConcurrentDictionary<Guid, int> Handled { get; } = new();

    public bool AlwaysFail { get; set; }

    public void FailNext(int times) => Volatile.Write(ref _failuresRemaining, times);

    public bool ShouldFail() => AlwaysFail || Interlocked.Decrement(ref _failuresRemaining) >= 0;

    public void Record(Guid eventId) => Handled.AddOrUpdate(eventId, 1, (_, count) => count + 1);
}

internal sealed class WriteProbeRowConsumer(ProbeItemsContext db, ConsumerProbe probe) : IIntegrationEventConsumer<ThingCreated>
{
    public async Task ConsumeAsync(ThingCreated message, CancellationToken cancellationToken)
    {
        db.Items.Add(new ProbeItemRow { Id = Guid.CreateVersion7(), Name = message.Name });
        await db.SaveChangesAsync(cancellationToken);
        probe.Record(message.EventId);
    }
}

internal sealed class FlakyConsumer(ConsumerProbe probe) : IIntegrationEventConsumer<ThingCreated>
{
    public Task ConsumeAsync(ThingCreated message, CancellationToken cancellationToken) =>
        probe.ShouldFail() ? throw new InvalidOperationException("flaky consumer failed") : Task.CompletedTask;
}
