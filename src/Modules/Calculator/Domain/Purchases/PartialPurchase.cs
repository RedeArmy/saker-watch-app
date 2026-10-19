using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Calculator.Domain.Purchases;

/// <summary>Codes of the failures of the calculation. A bad input is an outcome, never an exception.</summary>
public static class CalculatorErrors
{
    /// <summary>The price is zero, negative, below the smallest price kept or above the largest accepted.</summary>
    public const string PriceInvalid = "calculator.price_invalid";

    /// <summary>The amount to invest is zero, negative or above the largest accepted.</summary>
    public const string AmountInvalid = "calculator.amount_invalid";

    /// <summary>The number of shares is zero, negative, above the largest accepted or has more than six decimals.</summary>
    public const string SharesInvalid = "calculator.shares_invalid";

    /// <summary>The exchange rate is zero, negative or outside the range the calculator accepts.</summary>
    public const string RateInvalid = "calculator.rate_invalid";
}

/// <summary>
/// The outcome of a partial purchase (ADR-0006, ADR-0036): how many shares an amount buys, or what a number of shares costs, in the stock's own
/// currency and in quetzales at a given rate. Every figure is exact (<see cref="decimal"/>, nothing rounded); <see cref="ForDisplay"/> applies
/// the rounding of the presentation boundary. A reference only: a broker's rate, fees and minimum order sizes differ.
/// </summary>
/// <param name="QuoteCurrency">The stock's currency (ISO 4217).</param>
/// <param name="PriceNative">The price of one share in <paramref name="QuoteCurrency"/>.</param>
/// <param name="FxRate">Quetzales for one unit of <paramref name="QuoteCurrency"/>.</param>
/// <param name="Shares">The shares bought, as a fraction.</param>
/// <param name="AmountNative">The money spent, in <paramref name="QuoteCurrency"/>.</param>
/// <param name="CostOneShareGtq">The price of one share in quetzales.</param>
/// <param name="AmountGtq">The money spent, in quetzales.</param>
public sealed record PartialPurchase(
    string QuoteCurrency,
    decimal PriceNative,
    decimal FxRate,
    decimal Shares,
    decimal AmountNative,
    decimal CostOneShareGtq,
    decimal AmountGtq)
{
    /// <summary>Decimals of a share fraction on display.</summary>
    public const int ShareDecimals = 6;

    /// <summary>Minor units of the quetzal.</summary>
    public const int GtqDecimals = 2;

    /// <summary>
    /// The figures as they are shown: money rounded to the minor units of its currency with banker's rounding, and shares cut at six decimals
    /// towards zero, so a purchase is never shown as buying more than the money pays for.
    /// </summary>
    public PartialPurchase ForDisplay()
    {
        int nativeDecimals = CurrencyMinorUnits.Of(QuoteCurrency);
        return this with
        {
            Shares = Math.Round(Shares, ShareDecimals, MidpointRounding.ToZero),
            AmountNative = Math.Round(AmountNative, nativeDecimals, MidpointRounding.ToEven),
            CostOneShareGtq = Math.Round(CostOneShareGtq, GtqDecimals, MidpointRounding.ToEven),
            AmountGtq = Math.Round(AmountGtq, GtqDecimals, MidpointRounding.ToEven),
        };
    }
}

/// <summary>The minor units (decimals) of a currency, from the one table the money kit keeps (<see cref="CurrencyCode.MinorUnits"/>).</summary>
public static class CurrencyMinorUnits
{
    /// <summary>The number of decimals money of <paramref name="currency"/> is shown with: 0, 2 or 3 (ISO 4217); two for a code the kit does not accept.</summary>
    public static int Of(string currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return CurrencyCode.Create(currency) is { IsSuccess: true } code ? code.Value.MinorUnits : 2;
    }
}

/// <summary>
/// The partial-purchase arithmetic with no I/O: the same two formulas serve the endpoint and any client that does it by itself. The bounds keep
/// every product and quotient inside <see cref="decimal"/> and inside what a price, an amount or a rate can honestly be.
/// </summary>
public static class PartialPurchaseCalculator
{
    /// <summary>Smallest price: the precision prices are stored with (six decimals).</summary>
    public const decimal MinPrice = 0.000001m;

    /// <summary>Largest price of one share.</summary>
    public const decimal MaxPrice = 1_000_000_000m;

    /// <summary>Largest amount to invest, in the stock's currency.</summary>
    public const decimal MaxAmount = 1_000_000_000_000m;

    /// <summary>Largest number of shares.</summary>
    public const decimal MaxShares = 1_000_000_000_000m;

    /// <summary>Smallest exchange rate: the precision rates are stored with (eight decimals).</summary>
    public const decimal MinRate = 0.00000001m;

    /// <summary>Largest exchange rate.</summary>
    public const decimal MaxRate = 1_000_000m;

    /// <summary>What <paramref name="amount"/> of the stock's currency buys at <paramref name="price"/>, and what that is in quetzales.</summary>
    public static Result<PartialPurchase> ByAmount(string quoteCurrency, decimal price, decimal amount, decimal fxRate)
    {
        Error? invalid = CheckPrice(price) ?? CheckRate(fxRate);
        if (invalid is not null)
        {
            return invalid;
        }

        if (amount is <= 0m or > MaxAmount)
        {
            return Error.Validation(CalculatorErrors.AmountInvalid, string.Create(CultureInfo.InvariantCulture, $"The amount to invest is more than 0 and at most {MaxAmount:0}."));
        }

        return Build(quoteCurrency, price, fxRate, amount / price, amount);
    }

    /// <summary>What <paramref name="shares"/> shares cost at <paramref name="price"/>, in the stock's currency and in quetzales.</summary>
    public static Result<PartialPurchase> ByShares(string quoteCurrency, decimal price, decimal shares, decimal fxRate)
    {
        Error? invalid = CheckPrice(price) ?? CheckRate(fxRate);
        if (invalid is not null)
        {
            return invalid;
        }

        // More than six decimals would be cut on display and the purchase would not be the one asked for, so it is refused instead.
        if (shares is <= 0m or > MaxShares || decimal.Round(shares, PartialPurchase.ShareDecimals) != shares)
        {
            return Error.Validation(CalculatorErrors.SharesInvalid, string.Create(CultureInfo.InvariantCulture, $"The shares are more than 0, at most {MaxShares:0}, with at most {PartialPurchase.ShareDecimals} decimals."));
        }

        return Build(quoteCurrency, price, fxRate, shares, shares * price);
    }

    private static PartialPurchase Build(string quoteCurrency, decimal price, decimal fxRate, decimal shares, decimal amountNative) =>
        new(quoteCurrency, price, fxRate, shares, amountNative, price * fxRate, amountNative * fxRate);

    private static Error? CheckPrice(decimal price) =>
        price is < MinPrice or > MaxPrice
            ? Error.Validation(CalculatorErrors.PriceInvalid, string.Create(CultureInfo.InvariantCulture, $"The price of a share is between {MinPrice} and {MaxPrice:0}."))
            : null;

    private static Error? CheckRate(decimal rate) =>
        rate is < MinRate or > MaxRate
            ? Error.Validation(CalculatorErrors.RateInvalid, string.Create(CultureInfo.InvariantCulture, $"The exchange rate is between {MinRate} and {MaxRate:0}."))
            : null;
}
