using System.Diagnostics.CodeAnalysis;

namespace SakerWatch.BuildingBlocks.Kernel.Domain;

/// <summary>Immutable concept with no identity; two instances are equal when all their components are equal.</summary>
[SuppressMessage("Major Code Smell", "S4035:Classes implementing \"IEquatable<T>\" should be sealed", Justification = "Abstract base type; equality is defined here for all derived types.")]
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>The values that define equality, in a stable order.</summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public bool Equals(ValueObject? other) =>
        other is not null && GetType() == other.GetType() && GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(GetType());
        foreach (object? component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);
}
