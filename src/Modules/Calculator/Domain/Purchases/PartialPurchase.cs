using System.Globalization;
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
}

/// <summary>
/// The outcome of a partial purchase (ADR-0006, ADR-0036, ADR-0037): how many shares an amount buys, or what a number of shares costs, in the
/// stock's own currency and converted with an exchange rate (quetzales, for the endpoint). Every figure is exact; <see cref="ForDisplay"/> is the
/// one place they are rounded, and what it returns cannot be calculated with. A reference only: a broker's rate, fees and minimum order sizes differ.
/// </summary>
/// <param name="Price">The price of one share, in the stock's currency.</param>
/// <param name="Rate">The exchange rate applied, from the stock's currency.</param>
/// <param name="Shares">The shares bought, as a fraction.</param>
/// <param name="Amount">The money spent, in the stock's currency.</param>
/// <param name="PriceConverted">The price of one share in the currency of <paramref name="Rate"/>.</param>
/// <param name="AmountConverted">The money spent, in the currency of <paramref name="Rate"/>.</param>
public sealed record PartialPurchase(Money Price, ExchangeRate Rate, Shares Shares, Money Amount, Money PriceConverted, Money AmountConverted)
{
    /// <summary>
    /// The figures as they are shown: money rounded to the minor units of its currency with banker's rounding, and shares cut at six decimals
    /// towards zero, so a purchase is never shown as buying more than the money pays for. The price and the rate are echoed as they were given.
    /// </summary>
    public PartialPurchaseDisplay ForDisplay() =>
        new(Price, Rate, Shares.ToDisplay(), Amount.ToDisplay(), PriceConverted.ToDisplay(), AmountConverted.ToDisplay());
}

/// <summary>A partial purchase as it is shown. It carries no arithmetic on purpose (see <see cref="DisplayMoney"/>).</summary>
/// <param name="Price">The price of one share, as given.</param>
/// <param name="Rate">The exchange rate, as applied.</param>
/// <param name="Shares">The shares, cut at six decimals.</param>
/// <param name="Amount">The money spent, in the stock's currency.</param>
/// <param name="PriceConverted">The price of one share, converted.</param>
/// <param name="AmountConverted">The money spent, converted.</param>
public sealed record PartialPurchaseDisplay(
    Money Price,
    ExchangeRate Rate,
    DisplayShares Shares,
    DisplayMoney Amount,
    DisplayMoney PriceConverted,
    DisplayMoney AmountConverted);

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

    /// <summary>What <paramref name="amount"/> buys at <paramref name="price"/>, and what that is converted with <paramref name="rate"/>.</summary>
    public static Result<PartialPurchase> ByAmount(Money price, Money amount, ExchangeRate rate)
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

        Result<Shares> shares = Shares.Buying(amount, price);
        return shares.IsSuccess ? Build(price, rate, shares.Value, amount) : shares.Error!;
    }

    /// <summary>What <paramref name="shares"/> cost at <paramref name="price"/>, in the stock's currency and converted with <paramref name="rate"/>.</summary>
    public static Result<PartialPurchase> ByShares(Money price, Shares shares, ExchangeRate rate)
    {
        ArgumentNullException.ThrowIfNull(shares);
        Error? invalid = Check(price, rate);
        if (invalid is not null)
        {
            return invalid;
        }

        Result<Money> amount = shares.Cost(price);
        return amount.IsSuccess ? Build(price, rate, shares, amount.Value) : amount.Error!;
    }

    private static Result<PartialPurchase> Build(Money price, ExchangeRate rate, Shares shares, Money amount)
    {
        Result<Money> priceConverted = rate.Convert(price);
        if (!priceConverted.IsSuccess)
        {
            return priceConverted.Error!;
        }

        Result<Money> amountConverted = rate.Convert(amount);
        return amountConverted.IsSuccess
            ? new PartialPurchase(price, rate, shares, amount, priceConverted.Value, amountConverted.Value)
            : amountConverted.Error!;
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
