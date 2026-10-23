using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application.Features.Portfolios;

/// <summary>A portfolio as the API shows it.</summary>
/// <param name="Id">The portfolio id.</param>
/// <param name="Name">The trimmed name, unique for the user ignoring letter case.</param>
/// <param name="Description">The description, or <see langword="null"/>.</param>
/// <param name="Position">Where it sits among the user's portfolios, from zero.</param>
/// <param name="ItemCount">How many stocks it holds.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record PortfolioView(Guid Id, string Name, string? Description, int Position, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Builds the view of a stored portfolio.</summary>
    public static PortfolioView Of(PortfolioSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Portfolio p = summary.Portfolio;
        return new PortfolioView(p.Id.Value, p.Name.Value, p.Description, p.Position, summary.ItemCount, p.CreatedAt, p.UpdatedAt);
    }
}

/// <summary>A stock in a portfolio.</summary>
/// <param name="Stock">The catalog stock.</param>
/// <param name="Position">Where it sits in the portfolio, from zero.</param>
/// <param name="AddedAt">When it was put in the portfolio.</param>
public sealed record PortfolioItemView(StockSummary Stock, int Position, DateTimeOffset AddedAt);

/// <summary>A portfolio with its stocks.</summary>
/// <param name="Portfolio">The portfolio.</param>
/// <param name="Items">Its stocks by position.</param>
public sealed record PortfolioDetail(PortfolioView Portfolio, IReadOnlyList<PortfolioItemView> Items);

/// <summary>Errors of the portfolio use cases.</summary>
public static class PortfolioErrors
{
    /// <summary>The portfolio does not exist for the caller.</summary>
    public const string NotFound = "watchlist.portfolio.not_found";

    /// <summary>Another portfolio of the caller has that name.</summary>
    public const string NameTaken = "watchlist.portfolio.name_taken";

    /// <summary>The caller has the most portfolios allowed.</summary>
    public const string LimitReached = "watchlist.portfolio.limit_reached";

    /// <summary>The portfolio holds the most stocks allowed.</summary>
    public const string ItemLimitReached = "watchlist.portfolio.items_limit_reached";

    /// <summary>The stock is not on the caller's watchlist.</summary>
    public const string ItemNotOnWatchlist = "watchlist.portfolio.item_not_on_watchlist";

    /// <summary>The delete would take stocks off the watchlist and was not confirmed.</summary>
    public const string DeleteNeedsConfirmation = "watchlist.portfolio.delete_requires_confirmation";

    /// <summary>A parallel request changed the membership that this request was answering about.</summary>
    public const string ItemChanged = "watchlist.portfolio.item_changed";

    /// <summary>A confirmed delete did not carry the token of the deletion preview.</summary>
    public const string RemovalTokenRequired = "watchlist.portfolio.removal_token_required";

    /// <summary>The stocks that the delete would take off the watchlist changed after the caller looked.</summary>
    public const string RemovalChanged = "watchlist.portfolio.removal_changed";

    /// <summary>The list given to reorder is not exactly what exists.</summary>
    public const string OrderMismatch = "watchlist.order_mismatch";

    /// <summary>The portfolio does not exist.</summary>
    public static Error PortfolioNotFound() => Error.NotFound(NotFound, "The portfolio was not found.");

    /// <summary>The name is taken.</summary>
    public static Error PortfolioNameTaken() => Error.Conflict(NameTaken, "You already have a portfolio with that name.");

    /// <summary>Too many portfolios.</summary>
    public static Error PortfolioLimit() => Error.Conflict(LimitReached, "You have the most portfolios allowed. Delete one before creating another.");
}
