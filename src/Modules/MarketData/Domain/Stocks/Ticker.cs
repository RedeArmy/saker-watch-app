using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Stocks;

/// <summary>
/// A ticker as the listing's own market writes it: upper case letters and digits with <c>.</c>, <c>-</c> or <c>_</c> inside (<c>AAPL</c>,
/// <c>BRK.B</c>, <c>7203</c>). Never an identity by itself: the same ticker can exist on several markets, so a listing is <c>(Mic, Ticker)</c>.
/// The restricted alphabet also keeps it safe as a URL segment and as a cache key segment.
/// </summary>
public sealed class Ticker : ValueObject
{
    /// <summary>Longest accepted ticker.</summary>
    public const int MaxLength = 16;

    private Ticker(string value) => Value = value;

    /// <summary>The normalized ticker.</summary>
    public string Value { get; }

    /// <summary>Normalizes (trim, upper case) and validates <paramref name="value"/>.</summary>
    public static Result<Ticker> Create(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        bool valid = normalized.Length is >= 1 and <= MaxLength
            && char.IsAsciiLetterOrDigit(normalized[0])
            && normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        return valid
            ? new Ticker(normalized)
            : Error.Validation("marketdata.ticker.invalid", $"A ticker has 1 to {MaxLength} letters, digits, '.', '-' or '_' and starts with a letter or digit.");
    }

    /// <summary>Parses a value known to be valid (constants and tests).</summary>
    /// <exception cref="ArgumentException">The value is not a valid ticker.</exception>
    public static Ticker Parse(string value) =>
        Create(value) is { IsSuccess: true } result ? result.Value : throw new ArgumentException($"'{value}' is not a valid ticker.", nameof(value));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
