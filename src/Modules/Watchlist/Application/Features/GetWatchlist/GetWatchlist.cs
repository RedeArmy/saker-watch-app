using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application.Features.Portfolios;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;
using SakerWatch.Watchlist.Domain.Items;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application.Features.GetWatchlist;

/// <summary>
/// The caller's watchlist with each stock's newest price, how current it is and its performance windows, filtered, sorted and paged. A research
/// aid: nothing here is a recommendation, and no signal is offered until the analysis exists (Phase 4).
/// </summary>
/// <param name="Sort">One of <see cref="WatchlistSort.Keys"/>; <see langword="null"/> means <c>ticker</c>.</param>
/// <param name="Order"><c>asc</c> or <c>desc</c>; <see langword="null"/> means <c>asc</c>. Items without a value sort last in either order.</param>
/// <param name="Market">Only stocks of this canonical market.</param>
/// <param name="AssetType">Only this kind of instrument: <c>equity</c>, <c>etf</c> or <c>other</c>.</param>
/// <param name="Search">Only stocks whose ticker or name contains this text, ignoring case.</param>
/// <param name="PriceStatus">Only stocks whose prices are in this state (<see cref="PriceStatuses"/>).</param>
/// <param name="PortfolioId">Only the stocks of this portfolio of the caller (404 when it is not one of theirs).</param>
/// <param name="Limit">Items per page, 1 to 100.</param>
/// <param name="Offset">Items to skip.</param>
public sealed record GetWatchlistQuery(string? Sort, string? Order, string? Market, string? AssetType, string? Search, string? PriceStatus, int Limit, int Offset, Guid? PortfolioId = null)
    : IQuery<WatchlistPage>, IReadsDatabase;

/// <summary>A stock on the watchlist as the list shows it.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Stock">The catalog stock.</param>
/// <param name="Notes">The caller's notes, or <see langword="null"/>.</param>
/// <param name="TargetPrice">The caller's target price, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the stock was added.</param>
/// <param name="UpdatedAt">When the item last changed.</param>
/// <param name="PriceStatus">One of <see cref="PriceStatuses"/>.</param>
/// <param name="Price">The newest stored bar, or <see langword="null"/>.</param>
/// <param name="StaleSince">When the price went stale, when it did.</param>
/// <param name="Windows">The eight performance windows.</param>
public sealed record WatchlistEntry(
    Guid Id,
    StockSummary Stock,
    string? Notes,
    decimal? TargetPrice,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string PriceStatus,
    PriceSnapshot? Price,
    DateTimeOffset? StaleSince,
    IReadOnlyList<WindowSnapshot> Windows);

/// <summary>One page of the watchlist.</summary>
/// <param name="Items">The entries of the page.</param>
/// <param name="Total">How many stocks match the filters, in all pages.</param>
/// <param name="Limit">Items per page asked for.</param>
/// <param name="Offset">Items skipped.</param>
public sealed record WatchlistPage(IReadOnlyList<WatchlistEntry> Items, int Total, int Limit, int Offset);

/// <summary>The keys a watchlist can be sorted by.</summary>
public static class WatchlistSort
{
    /// <summary>Keys that need only the catalog and the item.</summary>
    public static readonly IReadOnlyList<string> CatalogKeys = ["ticker", "name", "market", "createdAt"];

    /// <summary>Keys that need the market data of every stock: the price and each performance window.</summary>
    public static readonly IReadOnlyList<string> MarketKeys = ["price", "1d", "1w", "1m", "3m", "6m", "ytd", "1y", "5y"];

    /// <summary>Every key.</summary>
    public static readonly IReadOnlyList<string> Keys = [.. CatalogKeys, .. MarketKeys];
}

/// <summary>Checks the paging, the sort and the filters, naming each field that is wrong.</summary>
public sealed class GetWatchlistValidator : AbstractValidator<GetWatchlistQuery>
{
    /// <summary>Largest page.</summary>
    public const int MaxLimit = 100;

    private static readonly string[] _orders = ["asc", "desc"];
    private static readonly string[] _assetTypes = ["equity", "etf", "other"];
    private static readonly string[] _priceStatuses = [PriceStatuses.Current, PriceStatuses.Stale, PriceStatuses.Pending, PriceStatuses.Unavailable];

    /// <summary>Creates the validator.</summary>
    public GetWatchlistValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, MaxLimit);
        RuleFor(x => x.Offset).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Sort).Must(sort => WatchlistSort.Keys.Contains(sort!, StringComparer.Ordinal)).When(x => x.Sort is not null)
            .WithMessage($"Sort must be one of: {string.Join(", ", WatchlistSort.Keys)}. A signal sort arrives with the analysis.");
        RuleFor(x => x.Order).Must(order => _orders.Contains(order!, StringComparer.Ordinal)).When(x => x.Order is not null)
            .WithMessage("Order must be asc or desc.");
        RuleFor(x => x.AssetType).Must(type => _assetTypes.Contains(type!, StringComparer.Ordinal)).When(x => x.AssetType is not null)
            .WithMessage("AssetType must be equity, etf or other.");
        RuleFor(x => x.PriceStatus).Must(status => _priceStatuses.Contains(status!, StringComparer.Ordinal)).When(x => x.PriceStatus is not null)
            .WithMessage($"PriceStatus must be one of: {string.Join(", ", _priceStatuses)}.");
        RuleFor(x => x.Market).MaximumLength(4);
        RuleFor(x => x.Search).MaximumLength(40);
    }
}

/// <summary>
/// Builds the page. A sort or a filter that depends on the market data reads it for every stock of the user (at most 500, in a few statements);
/// every other request reads it only for the stocks of the page, so a watchlist sorted by name costs the page, not the list.
/// </summary>
public sealed class GetWatchlistHandler(ICurrentUser currentUser, IWatchlistRepository items, IPortfolioRepository portfolios, IMarketSnapshots market)
    : IRequestHandler<GetWatchlistQuery, Result<WatchlistPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WatchlistPage>> Handle(GetWatchlistQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        IReadOnlyList<WatchlistItem> all = await items.ListAsync(userId, cancellationToken);
        if (request.PortfolioId is { } portfolioId)
        {
            var id = PortfolioId.Create(portfolioId);
            if (await portfolios.FindAsync(userId, id, cancellationToken) is null)
            {
                return PortfolioErrors.PortfolioNotFound();
            }

            HashSet<Guid> inPortfolio = [.. (await portfolios.MembersAsync(userId, id, cancellationToken)).Select(member => member.StockId)];
            all = [.. all.Where(item => inPortfolio.Contains(item.StockId))];
        }

        IReadOnlyDictionary<Guid, StockSummary> stocks = await market.FindManyAsync([.. all.Select(item => item.StockId)], cancellationToken);

        // An item whose stock the catalog cannot find is left out: it cannot be shown, and nothing deletes catalog rows.
        List<(WatchlistItem Item, StockSummary Stock)> candidates = [.. all.Where(item => stocks.ContainsKey(item.StockId)).Select(item => (item, stocks[item.StockId])).Where(pair => Matches(pair.Item2, request))];

        string sort = request.Sort ?? "ticker";
        bool descending = request.Order == "desc";
        bool needsMarket = request.PriceStatus is not null || WatchlistSort.MarketKeys.Contains(sort, StringComparer.Ordinal);

        IReadOnlyDictionary<Guid, StockSnapshot> snapshots = needsMarket
            ? await market.SnapshotsAsync([.. candidates.Select(pair => pair.Item.StockId)], cancellationToken)
            : new Dictionary<Guid, StockSnapshot>();
        if (request.PriceStatus is not null)
        {
            candidates = [.. candidates.Where(pair => snapshots.TryGetValue(pair.Item.StockId, out StockSnapshot? snapshot) && snapshot.PriceStatus == request.PriceStatus)];
        }

        List<(WatchlistItem Item, StockSummary Stock)> ordered = Order(candidates, snapshots, sort, descending);
        List<(WatchlistItem Item, StockSummary Stock)> page = [.. ordered.Skip(request.Offset).Take(request.Limit)];

        if (!needsMarket)
        {
            snapshots = await market.SnapshotsAsync([.. page.Select(pair => pair.Item.StockId)], cancellationToken);
        }

        WatchlistEntry[] entries = [.. page.Select(pair => ToEntry(pair.Item, pair.Stock, snapshots.GetValueOrDefault(pair.Item.StockId)))];
        return new WatchlistPage(entries, ordered.Count, request.Limit, request.Offset);
    }

    private static bool Matches(StockSummary stock, GetWatchlistQuery query)
    {
        if (query.Market is not null && !string.Equals(stock.Market, query.Market.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.AssetType is not null && stock.AssetType != query.AssetType)
        {
            return false;
        }

        string? text = query.Search?.Trim();
        return string.IsNullOrEmpty(text)
            || stock.Ticker.Contains(text, StringComparison.OrdinalIgnoreCase)
            || stock.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // Stocks without a value for the key (no price yet, a window with a reason) come last in either order; ties break by ticker and market so
    // the order is the same every time, which is what makes the ETag of a page stable.
    private static List<(WatchlistItem Item, StockSummary Stock)> Order(
        List<(WatchlistItem Item, StockSummary Stock)> candidates, IReadOnlyDictionary<Guid, StockSnapshot> snapshots, string sort, bool descending)
    {
        IOrderedEnumerable<(WatchlistItem Item, StockSummary Stock)> ordered;
        if (sort is "ticker" or "name" or "market" or "createdAt")
        {
            Func<(WatchlistItem Item, StockSummary Stock), IComparable> key = sort switch
            {
                "name" => pair => pair.Stock.Name.ToUpperInvariant(),
                "market" => pair => pair.Stock.Market,
                "createdAt" => pair => pair.Item.CreatedAt,
                _ => pair => pair.Stock.Ticker,
            };
            ordered = descending ? candidates.OrderByDescending(key) : candidates.OrderBy(key);
        }
        else
        {
            Func<(WatchlistItem Item, StockSummary Stock), decimal?> value = pair => ValueOf(snapshots.GetValueOrDefault(pair.Item.StockId), sort);
            IOrderedEnumerable<(WatchlistItem Item, StockSummary Stock)> missingLast = candidates.OrderBy(pair => value(pair) is null);
            ordered = descending ? missingLast.ThenByDescending(value) : missingLast.ThenBy(value);
        }

        return [.. ordered.ThenBy(pair => pair.Stock.Ticker, StringComparer.Ordinal).ThenBy(pair => pair.Stock.Market, StringComparer.Ordinal)];
    }

    private static decimal? ValueOf(StockSnapshot? snapshot, string sort)
    {
        if (snapshot is null)
        {
            return null;
        }

        return sort == "price"
            ? snapshot.Price?.Value
            : snapshot.Windows.FirstOrDefault(window => string.Equals(window.Window, sort, StringComparison.OrdinalIgnoreCase))?.Change;
    }

    private static WatchlistEntry ToEntry(WatchlistItem item, StockSummary stock, StockSnapshot? snapshot) =>
        new(
            item.Id.Value,
            stock,
            item.Notes,
            item.TargetPrice,
            item.CreatedAt,
            item.UpdatedAt,
            snapshot?.PriceStatus ?? PriceStatuses.Pending,
            snapshot?.Price,
            snapshot?.StaleSince,
            snapshot?.Windows ?? []);
}
