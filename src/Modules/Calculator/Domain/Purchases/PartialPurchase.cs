using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Calculator.Domain.Purchases;

/// <summary>Codes of the failures that belong to the calculator. The rest (currencies, shares, rates, ranges) come from the money kit.</summary>
public static class CalculatorErrors
{
    /// <summary>The price of a share is outside what the calculator accepts: less than the precision prices are kept with, or above the largest.</summary>
    public const string PriceOutOfRange = "calculator.price_out_of_range";

    /// <summary>The amount to invest is zero or negative.</summary>
    public const string AmountInvalid = "calculator.amount_invalid";

    /// <summary>The broker's commission is negative or above the most the calculator accepts.</summary>
    public const string FeeOutOfRange = "calculator.fee_out_of_range";
}

/// <summary>
/// The broker's commission as a percentage of the value of the shares bought (<c>1</c> is 1 %), between 0 and <see cref="Max"/>. It is optional:
/// without one the commission is zero and the calculation is exactly the one without commission (ADR-0036).
/// </summary>
public sealed class FeePercent : ValueObject
{
    /// <summary>The largest commission accepted, in percent.</summary>
    public const decimal Max = 10m;

    private FeePercent(decimal value) => Value = value;

    /// <summary>No commission.</summary>
    public static FeePercent None { get; } = new(0m);

    /// <summary>The percentage as given (<c>1.5</c> is 1.5 %).</summary>
    public decimal Value { get; }

    /// <summary>Whether the commission is zero.</summary>
    public bool IsNone => Value == 0m;

    /// <summary>The percentage as a fraction (<c>0.015</c> for 1.5 %).</summary>
    public decimal Fraction => Value / 100m;

    /// <summary>Builds a commission from 0 to <see cref="Max"/> percent.</summary>
    public static Result<FeePercent> Create(decimal value) =>
        value is >= 0m and <= Max
            ? new FeePercent(value)
            : Error.Validation(CalculatorErrors.FeeOutOfRange, string.Create(CultureInfo.InvariantCulture, $"The commission is between 0 and {Max:0} percent."));

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}

/// <summary>
/// The outcome of a partial purchase (ADR-0006, ADR-0036, ADR-0037): how many shares an amount buys, or what a number of shares costs, in the
/// stock's own currency and converted with an exchange rate (quetzales, for the endpoint). The broker's commission is part of the total paid:
/// <paramref name="Amount"/> is what leaves the pocket, <paramref name="Fee"/> the part of it that is commission. Every figure is exact;
/// <see cref="ForDisplay"/> is the one place they are rounded, and what it returns cannot be calculated with. A reference only: a broker's
/// rate, fees and minimum order sizes differ.
/// </summary>
/// <param name="Price">The price of one share, in the stock's currency.</param>
/// <param name="Rate">The exchange rate applied, from the stock's currency.</param>
/// <param name="FeePercent">The commission assumed, as a percentage of the value of the shares; none when it was not given.</param>
/// <param name="Shares">The shares bought, as a fraction.</param>
/// <param name="Amount">The total paid, commission included, in the stock's currency.</param>
/// <param name="Fee">The commission within <paramref name="Amount"/>, in the stock's currency.</param>
/// <param name="PriceConverted">The price of one share in the currency of <paramref name="Rate"/>.</param>
/// <param name="AmountConverted">The total paid, in the currency of <paramref name="Rate"/>.</param>
/// <param name="FeeConverted">The commission, in the currency of <paramref name="Rate"/>.</param>
public sealed record PartialPurchase(
    Money Price,
    ExchangeRate Rate,
    FeePercent FeePercent,
    Shares Shares,
    Money Amount,
    Money Fee,
    Money PriceConverted,
    Money AmountConverted,
    Money FeeConverted)
{
    /// <summary>
    /// The figures as they are shown: money rounded to the minor units of its currency with banker's rounding, and shares cut at six decimals
    /// towards zero, so a purchase is never shown as buying more than the money pays for. The price, the rate and the percentage are echoed as given.
    /// </summary>
    public PartialPurchaseDisplay ForDisplay() =>
        new(Price, Rate, FeePercent, Shares.ToDisplay(), Amount.ToDisplay(), Fee.ToDisplay(), PriceConverted.ToDisplay(), AmountConverted.ToDisplay(), FeeConverted.ToDisplay());
}

/// <summary>A partial purchase as it is shown. It carries no arithmetic on purpose (see <see cref="DisplayMoney"/>).</summary>
/// <param name="Price">The price of one share, as given.</param>
/// <param name="Rate">The exchange rate, as applied.</param>
/// <param name="FeePercent">The commission assumed, as given.</param>
/// <param name="Shares">The shares, cut at six decimals.</param>
/// <param name="Amount">The total paid, in the stock's currency.</param>
/// <param name="Fee">The commission, in the stock's currency.</param>
/// <param name="PriceConverted">The price of one share, converted.</param>
/// <param name="AmountConverted">The total paid, converted.</param>
/// <param name="FeeConverted">The commission, converted.</param>
public sealed record PartialPurchaseDisplay(
    Money Price,
    ExchangeRate Rate,
    FeePercent FeePercent,
    DisplayShares Shares,
    DisplayMoney Amount,
    DisplayMoney Fee,
    DisplayMoney PriceConverted,
    DisplayMoney AmountConverted,
    DisplayMoney FeeConverted);

/// <summary>
/// The partial-purchase arithmetic with no I/O, on the types of the money kit: the price, the amount and the rate cannot be swapped or mixed
/// across currencies, and no operation can overflow or throw. The only limits that are the calculator's own are those of a share price; the
/// ranges of amounts, share quantities and rates are the kit's.
/// </summary>
public static class PartialPurchaseCalculator
{
    /// <summary>Smallest price: the precision prices are stored with (six decimals).</summary>
    public const decimal MinPrice = 0.000001m;

    /// <summary>Largest price of one share.</summary>
    public const decimal MaxPrice = 1_000_000_000m;

    /// <summary>
    /// What <paramref name="amount"/> buys at <paramref name="price"/>, and what that is converted with <paramref name="rate"/>. The amount is the
    /// total paid: the commission (<paramref name="fee"/>, none when <see langword="null"/>) is a percentage of the value of the shares, so those are
    /// bought with <c>amount / (1 + fee)</c>.
    /// </summary>
    public static Result<PartialPurchase> ByAmount(Money price, Money amount, ExchangeRate rate, FeePercent? fee = null)
    {
        ArgumentNullException.ThrowIfNull(amount);
        Error? invalid = Check(price, rate);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!amount.IsPositive)
        {
            return Error.Validation(CalculatorErrors.AmountInvalid, "The amount to invest is more than zero.");
        }

        FeePercent commission = fee ?? FeePercent.None;
        Result<Money> value = commission.IsNone ? amount : amount.DividedBy(1m + commission.Fraction);
        if (!value.IsSuccess)
        {
            return value.Error!;
        }

        Result<Shares> shares = Shares.Buying(value.Value, price);
        Result<Money> commissionPaid = amount.Subtract(value.Value);
        if (!shares.IsSuccess || !commissionPaid.IsSuccess)
        {
            return shares.IsSuccess ? commissionPaid.Error! : shares.Error!;
        }

        return Build(price, rate, commission, shares.Value, amount, commissionPaid.Value);
    }

    /// <summary>
    /// What <paramref name="shares"/> cost at <paramref name="price"/>, in the stock's currency and converted with <paramref name="rate"/>. The
    /// commission (<paramref name="fee"/>, none when <see langword="null"/>) is added to the value of the shares to give the total paid.
    /// </summary>
    public static Result<PartialPurchase> ByShares(Money price, Shares shares, ExchangeRate rate, FeePercent? fee = null)
    {
        ArgumentNullException.ThrowIfNull(shares);
        Error? invalid = Check(price, rate);
        if (invalid is not null)
        {
            return invalid;
        }

        FeePercent commission = fee ?? FeePercent.None;
        Result<Money> value = shares.Cost(price);
        Result<Money> commissionDue = value.IsSuccess ? value.Value.Times(commission.Fraction) : value;
        Result<Money> total = commissionDue.IsSuccess ? value.Value.Add(commissionDue.Value) : commissionDue;
        return total.IsSuccess ? Build(price, rate, commission, shares, total.Value, commissionDue.Value) : total.Error!;
    }

    private static Result<PartialPurchase> Build(Money price, ExchangeRate rate, FeePercent commission, Shares shares, Money total, Money fee)
    {
        Result<Money> priceConverted = rate.Convert(price);
        if (!priceConverted.IsSuccess)
        {
            return priceConverted.Error!;
        }

        Result<Money> totalConverted = rate.Convert(total);
        if (!totalConverted.IsSuccess)
        {
            return totalConverted.Error!;
        }

        Result<Money> feeConverted = rate.Convert(fee);
        return feeConverted.IsSuccess
            ? new PartialPurchase(price, rate, commission, shares, total, fee, priceConverted.Value, totalConverted.Value, feeConverted.Value)
            : feeConverted.Error!;
    }

    // The price and the rate must be in the same currency, and the price inside the calculator's own range.
    private static Error? Check(Money price, ExchangeRate rate)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(rate);

        if (price.Currency != rate.From)
        {
            return Error.Validation(MoneyErrors.CurrencyMismatch, $"A price in {price.Currency} cannot be converted with a rate that starts from {rate.From}.");
        }

        return price.Amount is < MinPrice or > MaxPrice
            ? Error.Validation(CalculatorErrors.PriceOutOfRange, string.Create(CultureInfo.InvariantCulture, $"The price of a share is between {MinPrice} and {MaxPrice:0}."))
            : null;
    }
}
