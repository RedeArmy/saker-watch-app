using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Kernel.Monetary;

/// <summary>
/// An ISO 4217 currency code: three upper-case letters (<c>USD</c>, <c>CAD</c>, <c>JPY</c>). Money never exists without one, so an amount cannot be
/// added to, compared with or converted as if it were in another currency (ADR-0037). Prices stay in their native currency (ADR-0018).
/// </summary>
public sealed class CurrencyCode : ValueObject
{
    /// <summary>Length of every currency code.</summary>
    public const int Length = 3;

    private static readonly HashSet<string> _noMinorUnits = new(["JPY", "KRW", "CLP", "ISK", "VND", "PYG", "UGX"], StringComparer.Ordinal);
    private static readonly HashSet<string> _threeMinorUnits = new(["KWD", "BHD", "OMR", "JOD", "TND"], StringComparer.Ordinal);

    private CurrencyCode(string value) => Value = value;

    /// <summary>The normalized code.</summary>
    public string Value { get; }

    /// <summary>The decimals money of this currency is shown with: 0, 2 or 3 (ISO 4217 minor units; two unless listed).</summary>
    public int MinorUnits
    {
        get
        {
            if (_noMinorUnits.Contains(Value))
            {
                return 0;
            }

            return _threeMinorUnits.Contains(Value) ? 3 : 2;
        }
    }

    /// <summary>Normalizes (trim, upper case) and validates <paramref name="value"/>.</summary>
    public static Result<CurrencyCode> Create(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == Length && normalized.All(char.IsAsciiLetter)
            ? new CurrencyCode(normalized)
            : Error.Validation(MoneyErrors.CurrencyInvalid, "A currency code has three letters.");
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
