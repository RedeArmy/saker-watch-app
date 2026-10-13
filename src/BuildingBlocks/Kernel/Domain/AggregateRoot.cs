namespace SakerWatch.BuildingBlocks.Kernel.Domain;

/// <summary>
/// Consistency boundary: invariants are enforced inside the aggregate and state changes go through
/// intention-revealing methods. Domain events are collected here and published after commit via the outbox (ADR-0005).
/// </summary>
/// <typeparam name="TId">Strongly typed identifier.</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Creates an aggregate root with the given identity.</summary>
    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>Events raised since the last <see cref="PullDomainEvents"/>.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>Returns the pending events and clears them, so each event is published once.</summary>
    public IReadOnlyList<IDomainEvent> PullDomainEvents()
    {
        IDomainEvent[] events = [.. _domainEvents];
        _domainEvents.Clear();
        return events;
    }

    /// <summary>Records a domain event raised by a state change.</summary>
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
