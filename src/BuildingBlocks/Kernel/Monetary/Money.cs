using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Kernel.Monetary;

/// <summary>
/// An exact amount of money in one currency (ADR-0006, ADR-0037). It holds a <see cref="decimal"/> with no rounding at all: the only place money is
/// rounded is <see cref="ToDisplay"/>, and what it returns has no arithmetic, so a rounded figure cannot be fed back into a calculation. Operations
/// answer with a <see cref="Result{T}"/>: a mixed currency, a zero divisor or a result beyond <see cref="MaxMagnitude"/> is an outcome, and no
/// operation can overflow, because every operand is bounded well below what a <see cref="decimal"/> holds.
/// </summary>
public sealed class Money : ValueObject
{
    /// <summary>The largest amount, in either direction: a thousand million millions. Times the largest rate it is still inside a <see cref="decimal"/>.</summary>
    public const decimal MaxMagnitude = 1_000_000_000_000_000m;

    private Money(decimal amount, CurrencyCode currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>The exact amount.</summary>
    public decimal Amount { get; }

    /// <summary>The currency of the amount.</summary>
    public CurrencyCode Currency { get; }

    /// <summary>Whether the amount is more than zero.</summary>
    public bool IsPositive => Amount > 0m;

    /// <summary>Builds an amount, refusing one beyond <see cref="MaxMagnitude"/>.</summary>
    public static Result<Money> Create(decimal amount, CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return Math.Abs(amount) <= MaxMagnitude
            ? new Money(amount, currency)
            : OutOfRange();
    }

    /// <summary>Zero in <paramref name="currency"/>.</summary>
    public static Money Zero(CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(0m, currency);
    }

    /// <summary>The sum; both amounts must be in the same currency.</summary>
    public Result<Money> Add(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SameCurrency(other) ?? Compute(() => Amount + other.Amount);
    }

    /// <summary>The difference; both amounts must be in the same currency.</summary>
    public Result<Money> Subtract(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SameCurrency(other) ?? Compute(() => Amount - other.Amount);
    }

    /// <summary>The amount times a plain number (a quantity, a factor); the product is exact.</summary>
    public Result<Money> Times(decimal factor) => Compute(() => Amount * factor);

    /// <summary>The amount divided by a plain number. The quotient keeps the 28 digits a <see cref="decimal"/> holds and is not rounded further.</summary>
    public Result<Money> DividedBy(decimal divisor) =>
        divisor == 0m ? Error.Validation(MoneyErrors.DivisionByZero, "A division by zero was asked for.") : Compute(() => Amount / divisor);

    /// <summary>
    /// The amount as shown: rounded to the minor units of its currency with banker's rounding (<see cref="MidpointRounding.ToEven"/>). This is the
    /// one place money is rounded.
    /// </summary>
    public DisplayMoney ToDisplay() => new(Math.Round(Amount, Currency.MinorUnits, MidpointRounding.ToEven), Currency);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency}");

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    // A decimal operation that does not fit throws; here it is an outcome, like a result beyond the limit.
    private Result<Money> Compute(Func<decimal> operation)
    {
        try
        {
            return Create(operation(), Currency);
        }
        catch (OverflowException)
        {
            return OutOfRange();
        }
    }

    internal static Error OutOfRange() =>
        Error.Validation(MoneyErrors.AmountOutOfRange, string.Create(CultureInfo.InvariantCulture, $"An amount of money is at most {MaxMagnitude:0} in either direction."));

    private Error? SameCurrency(Money other) =>
        other.Currency == Currency
            ? null
            : Error.Validation(MoneyErrors.CurrencyMismatch, $"{Currency} and {other.Currency} cannot be added, subtracted or compared as they are.");
}

/// <summary>
/// An amount already rounded for display. It carries no arithmetic on purpose: a rounded figure is for a screen, a response or a statement, and
/// reusing it in another calculation is how cents are lost.
/// </summary>
/// <param name="Amount">The amount, in the minor units of its currency.</param>
/// <param name="Currency">The currency.</param>
public sealed record DisplayMoney(decimal Amount, CurrencyCode Currency);
