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

    // The currencies of ISO 4217 whose minor unit is not a hundredth (list 2024); any other code has two decimals.
    private static readonly HashSet<string> _noMinorUnits = new(
        ["BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF"], StringComparer.Ordinal);
    private static readonly HashSet<string> _threeMinorUnits = new(["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"], StringComparer.Ordinal);
    private static readonly HashSet<string> _fourMinorUnits = new(["CLF", "UYW"], StringComparer.Ordinal);

    private CurrencyCode(string value) => Value = value;

    /// <summary>The normalized code.</summary>
    public string Value { get; }

    /// <summary>The decimals money of this currency is shown with: 0, 2, 3 or 4 (ISO 4217 minor units; two unless listed).</summary>
    public int MinorUnits
    {
        get
        {
            if (_noMinorUnits.Contains(Value))
            {
                return 0;
            }

            if (_threeMinorUnits.Contains(Value))
            {
                return 3;
            }

            return _fourMinorUnits.Contains(Value) ? 4 : 2;
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
