namespace SakerWatch.Watchlist.Domain;

/// <summary>
/// The most one user may keep: the value in the triggers of the database and the one every other place that states a limit takes from here (the ranges of
/// the options, the size of a list to reorder), so raising one is a change of this line and of its migration and no other place can drift from it.
/// </summary>
public static class LimitCeilings
{
    /// <summary>Stocks on the watchlist (the SLO is written for 500).</summary>
    public const int Items = 500;

    /// <summary>Portfolios (ADR-0026).</summary>
    public const int Portfolios = 20;

    /// <summary>Stocks in one portfolio (ADR-0026).</summary>
    public const int ItemsPerPortfolio = 100;
}

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
    public static WatchlistLimits Defaults { get; } = new(LimitCeilings.Items, LimitCeilings.Portfolios, LimitCeilings.ItemsPerPortfolio);

    /// <summary>The values the database refuses to exceed. Raising one needs a migration.</summary>
    public static WatchlistLimits Ceilings { get; } = Defaults;

    /// <summary>True when every limit is positive and within the database ceilings.</summary>
    public bool IsWithinCeilings =>
        MaxItems is > 0 && MaxItems <= Ceilings.MaxItems
        && MaxPortfolios is > 0 && MaxPortfolios <= Ceilings.MaxPortfolios
        && MaxItemsPerPortfolio is > 0 && MaxItemsPerPortfolio <= Ceilings.MaxItemsPerPortfolio;
}
