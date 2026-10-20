using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Kernel.Monetary;

/// <summary>
/// A number of shares, possibly a fraction (ADR-0006, ADR-0037): exact and always positive, and bounded so that it times a price never leaves the
/// range of <see cref="Money"/>. A fraction asked for by a user has at most six decimals (<see cref="FromRequest"/>); one that comes out of a
/// division keeps every digit until it is shown, and <see cref="ToDisplay"/> cuts it at six decimals towards zero, so a shown purchase never
/// states more than the money pays for.
/// </summary>
public sealed class Shares : ValueObject
{
    /// <summary>The decimals a share fraction is shown with, and a user may ask for.</summary>
    public const int DisplayDecimals = 6;

    /// <summary>The most shares in one purchase.</summary>
    public const decimal MaxQuantity = 1_000_000_000_000m;

    private Shares(decimal value) => Value = value;

    /// <summary>The exact quantity.</summary>
    public decimal Value { get; }

    /// <summary>Builds a quantity that is more than zero and at most <see cref="MaxQuantity"/>.</summary>
    public static Result<Shares> Create(decimal value) =>
        value is > 0m and <= MaxQuantity
            ? new Shares(value)
            : Error.Validation(MoneyErrors.SharesInvalid, string.Create(CultureInfo.InvariantCulture, $"A number of shares is more than 0 and at most {MaxQuantity:0}."));

    /// <summary>Builds the quantity a user asked for: like <see cref="Create"/>, with at most six decimals (more would be cut on display, and the purchase would not be the one asked for).</summary>
    public static Result<Shares> FromRequest(decimal value) =>
        decimal.Round(value, DisplayDecimals) == value
            ? Create(value)
            : Error.Validation(MoneyErrors.SharesInvalid, string.Create(CultureInfo.InvariantCulture, $"A number of shares has at most {DisplayDecimals} decimals."));

    /// <summary>How many shares <paramref name="amount"/> buys at <paramref name="unitPrice"/>; both are in the same currency and the price is positive.</summary>
    public static Result<Shares> Buying(Money amount, Money unitPrice)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(unitPrice);

        if (amount.Currency != unitPrice.Currency)
        {
            return Error.Validation(MoneyErrors.CurrencyMismatch, $"An amount in {amount.Currency} cannot buy shares priced in {unitPrice.Currency}.");
        }

        if (!unitPrice.IsPositive)
        {
            return Error.Validation(MoneyErrors.PriceInvalid, "The price of a share is more than zero.");
        }

        // A price of a hundred-trillionth of a unit would overflow the quotient: that is a quantity beyond the limit, not a crash.
        try
        {
            decimal quotient = amount.Amount / unitPrice.Amount;
            return quotient == 0m && amount.IsPositive
                ? Error.Validation(MoneyErrors.SharesInvalid, "The amount is too small to buy any part of a share.")
                : Create(quotient);
        }
        catch (OverflowException)
        {
            return Error.Validation(MoneyErrors.SharesInvalid, string.Create(CultureInfo.InvariantCulture, $"A number of shares is more than 0 and at most {MaxQuantity:0}."));
        }
    }

    /// <summary>What these shares cost at <paramref name="unitPrice"/>, exactly.</summary>
    public Result<Money> Cost(Money unitPrice)
    {
        ArgumentNullException.ThrowIfNull(unitPrice);
        return unitPrice.IsPositive
            ? unitPrice.Times(Value)
            : Error.Validation(MoneyErrors.PriceInvalid, "The price of a share is more than zero.");
    }

    /// <summary>The quantity as shown: cut at six decimals towards zero, never rounded up.</summary>
    public DisplayShares ToDisplay() => new(Math.Round(Value, DisplayDecimals, MidpointRounding.ToZero));

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}

/// <summary>A quantity of shares already cut for display; it carries no arithmetic on purpose.</summary>
/// <param name="Value">The quantity, with at most six decimals.</param>
public sealed record DisplayShares(decimal Value);
