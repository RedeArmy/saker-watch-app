using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Kernel.Monetary;

/// <summary>
/// How many units of <see cref="To"/> one unit of <see cref="From"/> is worth (ADR-0028, ADR-0037). The direction is part of the value: it converts
/// money in <see cref="From"/> and refuses money in any other currency, so a rate can never be applied the wrong way round. Rates are exact, and
/// chaining or inverting them rounds nothing.
/// </summary>
public sealed class ExchangeRate : ValueObject
{
    /// <summary>The smallest rate: rates are stored with eight decimals, and a chain or an inversion may go lower.</summary>
    public const decimal MinRate = 0.000000000001m;

    /// <summary>The largest rate. Times <see cref="Money.MaxMagnitude"/> it still fits a <see cref="decimal"/>.</summary>
    public const decimal MaxRate = 1_000_000_000_000m;

    private ExchangeRate(CurrencyCode from, CurrencyCode to, decimal rate)
    {
        From = from;
        To = to;
        Rate = rate;
    }

    /// <summary>The currency being converted.</summary>
    public CurrencyCode From { get; }

    /// <summary>The currency converted to.</summary>
    public CurrencyCode To { get; }

    /// <summary>Units of <see cref="To"/> for one unit of <see cref="From"/>.</summary>
    public decimal Rate { get; }

    /// <summary>Builds a rate between two different currencies, positive and inside the range.</summary>
    public static Result<ExchangeRate> Create(CurrencyCode from, CurrencyCode to, decimal rate)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from == to)
        {
            return Error.Validation(MoneyErrors.RateInvalid, "A rate relates two different currencies; use Identity for the same one.");
        }

        return rate is >= MinRate and <= MaxRate
            ? new ExchangeRate(from, to, rate)
            : Error.Validation(MoneyErrors.RateInvalid, string.Create(CultureInfo.InvariantCulture, $"An exchange rate is between {MinRate} and {MaxRate:0}."));
    }

    /// <summary>The rate of a currency to itself: one.</summary>
    public static ExchangeRate Identity(CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new ExchangeRate(currency, currency, 1m);
    }

    /// <summary>Converts money in <see cref="From"/> to <see cref="To"/>, exactly; money in another currency is refused.</summary>
    public Result<Money> Convert(Money money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return money.Currency == From
            ? Money.Create(money.Amount * Rate, To)
            : Error.Validation(MoneyErrors.CurrencyMismatch, $"A {From} to {To} rate converts {From}, not {money.Currency}.");
    }

    /// <summary>The same rate the other way round (<see cref="To"/> to <see cref="From"/>): one over this one, exact.</summary>
    public Result<ExchangeRate> Invert() =>
        From == To ? this : Within(To, From, 1m / Rate);

    /// <summary>The rate of this one followed by <paramref name="next"/>, which must start where this one ends.</summary>
    public Result<ExchangeRate> Then(ExchangeRate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (To != next.From)
        {
            return Error.Validation(MoneyErrors.CurrencyMismatch, $"A rate ending in {To} cannot be followed by one starting in {next.From}.");
        }

        if (From == next.To)
        {
            // A round trip is not a rate: a currency relates to itself only through the identity, and the product of two quoted rates is rarely one.
            return Error.Validation(MoneyErrors.RateInvalid, "A rate cannot be chained back to the currency it starts from.");
        }

        return Within(From, next.To, Rate * next.Rate);
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{From}/{To} {Rate}");

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return From;
        yield return To;
        yield return Rate;
    }

    private static Result<ExchangeRate> Within(CurrencyCode from, CurrencyCode to, decimal rate) =>
        rate is >= MinRate and <= MaxRate
            ? new ExchangeRate(from, to, rate)
            : Error.Validation(MoneyErrors.RateInvalid, string.Create(CultureInfo.InvariantCulture, $"An exchange rate is between {MinRate} and {MaxRate:0}."));
}
