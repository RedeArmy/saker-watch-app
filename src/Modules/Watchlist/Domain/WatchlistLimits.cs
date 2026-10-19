namespace SakerWatch.Watchlist.Domain;

/// <summary>
/// How much one user may keep (ADR-0031, ADR-0026). The configured limits are enforced by the use cases; the database repeats the
/// <see cref="Ceilings"/> in triggers that count under a lock, so parallel requests cannot exceed them whatever the configuration says.
/// </summary>
/// <param name="MaxItems">Stocks on the watchlist.</param>
/// <param name="MaxPortfolios">Portfolios.</param>
/// <param name="MaxItemsPerPortfolio">Stocks in one portfolio.</param>
public sealed record WatchlistLimits(int MaxItems, int MaxPortfolios, int MaxItemsPerPortfolio)
{
    /// <summary>The limits the product ships with: the SLO is written for 500 stocks, ADR-0026 sets 20 portfolios of 100.</summary>
    public static WatchlistLimits Defaults { get; } = new(500, 20, 100);

    /// <summary>The values the database refuses to exceed. Raising one needs a migration.</summary>
    public static WatchlistLimits Ceilings { get; } = Defaults;

    /// <summary>True when every limit is positive and within the database ceilings.</summary>
    public bool IsWithinCeilings =>
        MaxItems is > 0 && MaxItems <= Ceilings.MaxItems
        && MaxPortfolios is > 0 && MaxPortfolios <= Ceilings.MaxPortfolios
        && MaxItemsPerPortfolio is > 0 && MaxItemsPerPortfolio <= Ceilings.MaxItemsPerPortfolio;
}
