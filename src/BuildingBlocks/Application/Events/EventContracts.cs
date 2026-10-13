using SakerWatch.BuildingBlocks.Kernel.Domain;

namespace SakerWatch.BuildingBlocks.Application.Events;

/// <summary>
/// Gives an event the stable name it is stored and routed under in the outbox (for example <c>watchlist.item_added.v1</c>).
/// Renaming the CLR type must never orphan stored messages, so the name is explicit and versioned.
/// </summary>
/// <param name="name">Stable event name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class IntegrationEventNameAttribute(string name) : Attribute
{
    /// <summary>The stable event name.</summary>
    public string Name { get; } = name;
}

/// <summary>Writes integration events in the current unit of work, so they commit (or roll back) with the state change (ADR-0005).</summary>
public interface IOutbox
{
    /// <summary>Adds <paramref name="message"/> to the outbox. Requires an active unit of work.</summary>
    Task AddAsync(IDomainEvent message, CancellationToken cancellationToken);

    /// <summary>Adds every event, in order. Typical use: <c>outbox.AddRangeAsync(aggregate.PullDomainEvents(), ct)</c>.</summary>
    Task AddRangeAsync(IEnumerable<IDomainEvent> messages, CancellationToken cancellationToken);
}

/// <summary>Records that a consumer handled a message, so redelivery has no second effect (ADR-0017).</summary>
public interface IInbox
{
    /// <summary>
    /// Registers <paramref name="messageId"/> for <paramref name="consumer"/> in the active unit of work.
    /// Returns <see langword="false"/> when it was already handled; in that case the consumer must skip its effect.
    /// </summary>
    Task<bool> TryBeginAsync(string consumer, Guid messageId, CancellationToken cancellationToken);
}

/// <summary>Reacts to an integration event. Consumers must be re-runnable; the dispatcher also dedupes through the inbox.</summary>
/// <typeparam name="TEvent">The event type handled.</typeparam>
public interface IIntegrationEventConsumer<in TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Applies the event. Throwing makes the message retry with backoff and rolls back everything it wrote.</summary>
    Task ConsumeAsync(TEvent message, CancellationToken cancellationToken);
}
