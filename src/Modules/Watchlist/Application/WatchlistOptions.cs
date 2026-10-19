using System.ComponentModel.DataAnnotations;
using SakerWatch.Watchlist.Domain;

namespace SakerWatch.Watchlist.Application;

/// <summary>What one user may keep. Bound from <c>Watchlist:Limits</c>; the database holds the ceilings of <see cref="WatchlistLimits.Ceilings"/> whatever this says.</summary>
public sealed class WatchlistOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Watchlist:Limits";

    /// <summary>Stocks on the watchlist.</summary>
    [Range(1, 500)]
    public int MaxItems { get; set; } = WatchlistLimits.Defaults.MaxItems;

    /// <summary>Portfolios.</summary>
    [Range(1, 20)]
    public int MaxPortfolios { get; set; } = WatchlistLimits.Defaults.MaxPortfolios;

    /// <summary>Stocks in one portfolio.</summary>
    [Range(1, 100)]
    public int MaxItemsPerPortfolio { get; set; } = WatchlistLimits.Defaults.MaxItemsPerPortfolio;

    /// <summary>The limits as a value.</summary>
    public WatchlistLimits ToLimits() => new(MaxItems, MaxPortfolios, MaxItemsPerPortfolio);
}
