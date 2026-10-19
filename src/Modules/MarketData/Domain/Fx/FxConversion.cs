using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Domain.Fx;

/// <summary>
/// One step of a conversion: the rate applied to go from <see cref="From"/> to <see cref="To"/>, with the day and the source it was published
/// for. When the stored rate is the other way round (<see cref="Inverted"/>), <see cref="Rate"/> is its reciprocal.
/// </summary>
/// <param name="From">The currency the step starts from.</param>
/// <param name="To">The currency the step ends in.</param>
/// <param name="Rate">Units of <paramref name="To"/> for one of <paramref name="From"/>, as applied.</param>
/// <param name="AsOfDate">The day of the stored rate, never a day it was not published for.</param>
/// <param name="Source">Who published the stored rate.</param>
/// <param name="Inverted">Whether the stored rate was <paramref name="To"/> to <paramref name="From"/> and was inverted.</param>
public sealed record FxLeg(CurrencyCode From, CurrencyCode To, decimal Rate, DateOnly AsOfDate, string Source, bool Inverted);

/// <summary>
/// The result of converting an amount (ADR-0028): the amount converted without any rounding (rounding is for the presentation boundary), the
/// path with the rate, day and source of each step, and whether any step is older than the allowed age. It is a reference, never a quote:
/// broker rates, fees and minimum order sizes differ, and only the Banco de Guatemala home page is an official source.
/// </summary>
/// <param name="Amount">The amount converted.</param>
/// <param name="From">The currency of <paramref name="Amount"/>.</param>
/// <param name="To">The currency asked for.</param>
/// <param name="Converted">The amount in <paramref name="To"/>.</param>
/// <param name="Legs">The steps, in order; empty when both currencies are the same.</param>
/// <param name="Rate">The combined rate: <paramref name="Converted"/> divided by <paramref name="Amount"/>'s unit, or one for the same currency.</param>
/// <param name="AsOfDate">The oldest day among the steps, or the requested day when there are none.</param>
/// <param name="IsStale">Whether any step is older than the maximum age for the requested day.</param>
public sealed record FxConversion(
    decimal Amount,
    CurrencyCode From,
    CurrencyCode To,
    decimal Converted,
    IReadOnlyList<FxLeg> Legs,
    decimal Rate,
    DateOnly AsOfDate,
    bool IsStale)
{
    /// <summary>Key of the notice every result carries: the rate is a reference. The interface turns it into text (Spanish, <c>es-GT</c>).</summary>
    public const string DisclaimerKey = "marketdata.fx.reference_only";
}
