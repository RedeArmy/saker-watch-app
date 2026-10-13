namespace SakerWatch.BuildingBlocks.Kernel.Domain;

/// <summary>Something that happened in the domain, named in the past tense (ubiquitous language).</summary>
public interface IDomainEvent
{
    /// <summary>Unique id; consumers dedupe on it through the inbox (ADR-0017).</summary>
    Guid EventId { get; }

    /// <summary>When the event occurred (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}
