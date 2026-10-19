namespace SakerWatch.BuildingBlocks.Kernel.Monetary;

/// <summary>Codes of the failures of money arithmetic. Every one is an outcome (a <c>Result</c>), never an exception (ADR-0037).</summary>
public static class MoneyErrors
{
    /// <summary>The text is not an ISO 4217 code.</summary>
    public const string CurrencyInvalid = "money.currency_invalid";

    /// <summary>Two amounts, or an amount and a rate, are in different currencies.</summary>
    public const string CurrencyMismatch = "money.currency_mismatch";

    /// <summary>The amount, or the result of an operation on it, is beyond what money can honestly be (see <see cref="Money.MaxMagnitude"/>).</summary>
    public const string AmountOutOfRange = "money.amount_out_of_range";

    /// <summary>A price is zero or negative.</summary>
    public const string PriceInvalid = "money.price_invalid";

    /// <summary>A number of shares is zero, negative, too large, or has more decimals than it may.</summary>
    public const string SharesInvalid = "money.shares_invalid";

    /// <summary>An exchange rate is zero, negative, outside its range, or relates a currency to itself.</summary>
    public const string RateInvalid = "money.rate_invalid";

    /// <summary>A division by zero was asked for.</summary>
    public const string DivisionByZero = "money.division_by_zero";

    /// <summary>A change is measured from a baseline that is not positive.</summary>
    public const string BaselineInvalid = "money.baseline_invalid";
}
