using SakerWatch.BuildingBlocks.Kernel.Domain;

namespace SakerWatch.BuildingBlocks.Kernel.Time;

/// <summary>Creates time-ordered UUIDv7 identifiers; the only sanctioned source of new ids (<c>Guid.NewGuid</c> is banned).</summary>
public interface IIdGenerator
{
    /// <summary>A new UUIDv7.</summary>
    Guid NewGuid();

    /// <summary>A new strongly typed identifier.</summary>
    TId New<TId>()
        where TId : IStronglyTypedId<TId>;
}

/// <summary><see cref="IIdGenerator"/> that stamps UUIDv7 values with the injected <see cref="TimeProvider"/> clock.</summary>
/// <param name="timeProvider">Clock; a fake in tests makes ids deterministic in ordering.</param>
public sealed class TimeProviderIdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    /// <inheritdoc />
    public Guid NewGuid() => Guid.CreateVersion7(timeProvider.GetUtcNow());

    /// <inheritdoc />
    public TId New<TId>()
        where TId : IStronglyTypedId<TId> => TId.Create(NewGuid());
}
