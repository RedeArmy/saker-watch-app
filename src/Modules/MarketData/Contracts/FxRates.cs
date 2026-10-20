using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Contracts;

/// <summary>One step of an exchange path: the rate as applied, the day it was published for and who published it.</summary>
/// <param name="Rate">The rate as applied, from the currency the step starts from to the one it ends in.</param>
/// <param name="AsOfDate">The day of the stored rate, never a day it was not published for.</param>
/// <param name="Source">Who published the stored rate.</param>
/// <param name="Inverted">Whether the stored rate was the other way round and was inverted.</param>
public sealed record FxQuoteLeg(ExchangeRate Rate, DateOnly AsOfDate, string Source, bool Inverted);

/// <summary>
/// The exchange rate between two currencies with how it was reached (ADR-0028). It is a reference, never a quote: a broker's rate, fees and
/// minimum order sizes differ.
/// </summary>
/// <param name="Rate">The combined rate, exact; the identity when both currencies are the same.</param>
/// <param name="Legs">The steps, in order (a direct pair, its reciprocal or a path through the pivot); empty for the same currency.</param>
/// <param name="AsOfDate">The oldest day among the steps, or today in Guatemala when there are none.</param>
/// <param name="IsStale">Whether any step is older than the allowed age.</param>
public sealed record FxQuote(ExchangeRate Rate, IReadOnlyList<FxQuoteLeg> Legs, DateOnly AsOfDate, bool IsStale);

/// <summary>Read access to the stored exchange rates for the modules that convert money (the calculator).</summary>
public interface IFxRates
{
    /// <summary>
    /// The rate from <paramref name="from"/> to <paramref name="to"/> in force today in Guatemala: the newest one published on or before it,
    /// directly, inverted or through USD (ADR-0018). A pair with no rate stored is a not-found failure.
    /// </summary>
    Task<Result<FxQuote>> QuoteAsync(CurrencyCode from, CurrencyCode to, CancellationToken cancellationToken);
}
