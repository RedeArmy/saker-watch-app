using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Markets;

/// <summary>An ISO 10383 market identifier code: four upper-case letters or digits (<c>XNYS</c>, <c>XNAS</c>, <c>XTSE</c>).</summary>
public sealed class Mic : ValueObject
{
    /// <summary>Length of every MIC.</summary>
    public const int Length = 4;

    private Mic(string value) => Value = value;

    /// <summary>The normalized code.</summary>
    public string Value { get; }

    /// <summary>Normalizes (trim, upper case) and validates <paramref name="value"/>.</summary>
    public static Result<Mic> Create(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == Length && normalized.All(char.IsAsciiLetterOrDigit)
            ? new Mic(normalized)
            : Error.Validation("marketdata.mic.invalid", "A market identifier code has four letters or digits.");
    }

    /// <summary>Parses a value known to be valid (constants and tests).</summary>
    /// <exception cref="ArgumentException">The value is not a valid MIC.</exception>
    public static Mic Parse(string value) =>
        Create(value) is { IsSuccess: true } result ? result.Value : throw new ArgumentException($"'{value}' is not a valid MIC.", nameof(value));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
