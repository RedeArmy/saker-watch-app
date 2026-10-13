using System.Diagnostics.CodeAnalysis;

namespace SakerWatch.BuildingBlocks.Kernel.Domain;

/// <summary>An object defined by its identity rather than its attributes (ADR-0013).</summary>
/// <typeparam name="TId">Strongly typed identifier.</typeparam>
[SuppressMessage("Major Code Smell", "S4035:Classes implementing \"IEquatable<T>\" should be sealed", Justification = "Abstract base type; equality is defined here for all derived types.")]
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>Creates an entity with the given identity.</summary>
    protected Entity(TId id) => Id = id;

    /// <summary>The identity; immutable for the entity's lifetime.</summary>
    public TId Id { get; }

    /// <summary>Entities are equal when they are of the same type and share an identity.</summary>
    public bool Equals(Entity<TId>? other) =>
        other is not null && GetType() == other.GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>Identity equality.</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    /// <summary>Identity inequality.</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}
