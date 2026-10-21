using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Companies;

/// <summary>
/// The Central Index Key the SEC gives a filer: up to ten digits, kept as ten with leading zeros (<c>0000320193</c>), which is how EDGAR writes
/// it in its file names and the way two spellings of the same key compare equal (ADR-0040). It is data from outside, so it is checked before it
/// is used in an address.
/// </summary>
public sealed class Cik : ValueObject
{
    /// <summary>Digits of a normalized key.</summary>
    public const int Length = 10;

    private Cik(string value) => Value = value;

    /// <summary>The ten-digit text.</summary>
    public string Value { get; }

    /// <summary>The key as a number, as EDGAR writes it in the path of a filing.</summary>
    public long Number => long.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Accepts one to ten digits, with or without leading zeros, and normalizes them.</summary>
    public static Result<Cik> Create(string? value)
    {
        string digits = (value ?? string.Empty).Trim();
        return digits.Length is >= 1 and <= Length && digits.All(char.IsAsciiDigit) && digits.Any(c => c != '0')
            ? new Cik(digits.PadLeft(Length, '0'))
            : Error.Validation("marketdata.company.cik_invalid", "A CIK is one to ten digits and is not zero.");
    }

    /// <summary>Accepts a key that is known to be valid (constants and tests).</summary>
    public static Cik Parse(string value) =>
        Create(value) is { IsSuccess: true } result ? result.Value : throw new ArgumentException($"'{value}' is not a valid CIK.", nameof(value));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
