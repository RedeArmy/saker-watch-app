using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Markets;

/// <summary>An ISO 4217 currency code: three upper-case letters (<c>USD</c>, <c>CAD</c>, <c>JPY</c>). Prices stay in their native currency (ADR-0018).</summary>
public sealed class CurrencyCode : ValueObject
{
    /// <summary>Length of every currency code.</summary>
    public const int Length = 3;

    private CurrencyCode(string value) => Value = value;

    /// <summary>The normalized code.</summary>
    public string Value { get; }

    /// <summary>Normalizes (trim, upper case) and validates <paramref name="value"/>.</summary>
    public static Result<CurrencyCode> Create(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == Length && normalized.All(char.IsAsciiLetter)
            ? new CurrencyCode(normalized)
            : Error.Validation("marketdata.currency.invalid", "A currency code has three letters.");
    }

    /// <summary>Parses a value known to be valid (constants and tests).</summary>
    /// <exception cref="ArgumentException">The value is not a valid currency code.</exception>
    public static CurrencyCode Parse(string value) =>
        Create(value) is { IsSuccess: true } result ? result.Value : throw new ArgumentException($"'{value}' is not a valid currency code.", nameof(value));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
