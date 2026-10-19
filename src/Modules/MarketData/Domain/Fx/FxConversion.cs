using SakerWatch.BuildingBlocks.Kernel.Monetary;

namespace SakerWatch.MarketData.Domain.Fx;

/// <summary>
/// One step of a conversion: the rate applied to go from <see cref="From"/> to <see cref="To"/>, with the day and the source it was published
/// for. When the stored rate is the other way round (<see cref="Inverted"/>), <see cref="Rate"/> is its reciprocal.
/// </summary>
/// <param name="Rate">The rate as applied: units of <c>To</c> for one of <c>From</c>.</param>
/// <param name="AsOfDate">The day of the stored rate, never a day it was not published for.</param>
/// <param name="Source">Who published the stored rate.</param>
/// <param name="Inverted">Whether the stored rate was the other way round and was inverted.</param>
public sealed record FxLeg(ExchangeRate Rate, DateOnly AsOfDate, string Source, bool Inverted)
{
    /// <summary>The currency the step starts from.</summary>
    public CurrencyCode From => Rate.From;

    /// <summary>The currency the step ends in.</summary>
    public CurrencyCode To => Rate.To;
}

/// <summary>
/// The result of converting an amount (ADR-0028, ADR-0037): the money converted without any rounding (rounding is for the presentation boundary),
/// the path with the rate, day and source of each step, and whether any step is older than the allowed age. It is a reference, never a quote:
/// broker rates, fees and minimum order sizes differ, and only the Banco de Guatemala home page is an official source.
/// </summary>
/// <param name="Amount">The money converted, in its own currency.</param>
/// <param name="Converted">The money in the currency asked for.</param>
/// <param name="Legs">The steps, in order; empty when both currencies are the same.</param>
/// <param name="Rate">The combined rate, from the currency of <paramref name="Amount"/> to that of <paramref name="Converted"/>; the identity for the same currency.</param>
/// <param name="AsOfDate">The oldest day among the steps, or the requested day when there are none.</param>
/// <param name="IsStale">Whether any step is older than the maximum age for the requested day.</param>
public sealed record FxConversion(
    Money Amount,
    Money Converted,
    IReadOnlyList<FxLeg> Legs,
    ExchangeRate Rate,
    DateOnly AsOfDate,
    bool IsStale)
{
    /// <summary>Key of the notice every result carries: the rate is a reference. The interface turns it into text (Spanish, <c>es-GT</c>).</summary>
    public const string DisclaimerKey = "marketdata.fx.reference_only";

    /// <summary>The currency of <see cref="Amount"/>.</summary>
    public CurrencyCode From => Amount.Currency;

    /// <summary>The currency asked for.</summary>
    public CurrencyCode To => Converted.Currency;
}
