using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>A source of daily exchange rates (ADR-0028). Failures use the same classification as the market data providers (<c>ProviderErrors</c>).</summary>
public interface IFxRateProvider
{
    /// <summary>The name stored with each rate and used in metrics (<c>banguat</c>).</summary>
    string Name { get; }

    /// <summary>
    /// The rates of <paramref name="baseCurrency"/> in <paramref name="quote"/> published from <paramref name="from"/> to <paramref name="to"/>,
    /// both included, oldest first and one per day. A range with no publication is an empty list, not an error. A pair the source does not
    /// serve is a validation failure.
    /// </summary>
    Task<Result<IReadOnlyList<FxRate>>> GetDailyAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
