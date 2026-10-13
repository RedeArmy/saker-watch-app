namespace SakerWatch.BuildingBlocks.Kernel.Domain;

/// <summary>
/// A UUID-backed identifier with its own type (<c>UserId</c>, <c>StockId</c>...), so ids of different
/// concepts cannot be mixed up. Implement it on a <c>readonly record struct</c>.
/// </summary>
/// <typeparam name="TSelf">The implementing identifier type.</typeparam>
public interface IStronglyTypedId<TSelf>
    where TSelf : IStronglyTypedId<TSelf>
{
    /// <summary>The underlying UUID.</summary>
    Guid Value { get; }

    /// <summary>Wraps an existing UUID, for example one read from storage.</summary>
    static abstract TSelf Create(Guid value);
}
