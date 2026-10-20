using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Kernel.Monetary;

/// <summary>
/// The change of a price between two closes, as a fraction (<c>0.0123</c> is 1.23 %), exact (ADR-0030, ADR-0037). It is measured from a baseline
/// that must be positive, so a change from nothing is never invented, and it is never rounded: showing it as a percentage is the client's job.
/// </summary>
public sealed class RelativeChange : ValueObject
{
    private RelativeChange(decimal fraction) => Fraction = fraction;

    /// <summary>The change as a fraction of the baseline: <c>latest / baseline - 1</c>.</summary>
    public decimal Fraction { get; }

    /// <summary>The change from <paramref name="baseline"/> to <paramref name="latest"/>; both are positive prices of the same currency (the caller's guarantee).</summary>
    public static Result<RelativeChange> Between(decimal baseline, decimal latest)
    {
        if (baseline <= 0m)
        {
            return Error.Validation(MoneyErrors.BaselineInvalid, "A change is measured from a price above zero.");
        }

        if (latest < 0m)
        {
            return Error.Validation(MoneyErrors.PriceInvalid, "A price is not negative.");
        }

        // Both are bounded by the price range of their source, so the quotient cannot overflow; one that could is a bad input and not a change.
        try
        {
            return new RelativeChange((latest / baseline) - 1m);
        }
        catch (OverflowException)
        {
            return Error.Validation(MoneyErrors.AmountOutOfRange, "The two prices are too far apart to compare.");
        }
    }

    /// <inheritdoc />
    public override string ToString() => Fraction.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Fraction;
    }
}
