using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Watchlist.Domain.Items;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application.Features.Portfolios;

/// <summary>The one way a read of the watchlist is narrowed to a portfolio (ADR-0035), so the list, the metrics and the sparklines agree.</summary>
public static class PortfolioScope
{
    /// <summary>
    /// The caller's items, or only those in the portfolio when one is given. A portfolio that is not the caller's, or does not exist, is a
    /// not-found failure and nothing is revealed about it.
    /// </summary>
    public static async Task<Result<IReadOnlyList<WatchlistItem>>> ItemsAsync(
        IWatchlistRepository items, IPortfolioRepository portfolios, Guid userId, Guid? portfolioId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(portfolios);

        IReadOnlyList<WatchlistItem> all = await items.ListAsync(userId, cancellationToken);
        if (portfolioId is not { } requested)
        {
            return Result.Success(all);
        }

        var id = PortfolioId.Create(requested);
        if (!await portfolios.ExistsAsync(userId, id, cancellationToken))
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        HashSet<Guid> inPortfolio = [.. (await portfolios.MembersAsync(userId, id, cancellationToken)).Select(member => member.StockId)];
        return Result.Success<IReadOnlyList<WatchlistItem>>([.. all.Where(item => inPortfolio.Contains(item.StockId))]);
    }
}
