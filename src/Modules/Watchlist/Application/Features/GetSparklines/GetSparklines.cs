using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application.Features.Portfolios;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;

namespace SakerWatch.Watchlist.Application.Features.GetSparklines;

/// <summary>
/// The shape of the recent price of every stock on the caller's watchlist, for the small charts next to each row (ADR-0038). Informational only.
/// </summary>
/// <param name="Days">How many of the last stored trading days each line has, 5 to 250.</param>
/// <param name="PortfolioId">Only the stocks of this portfolio of the caller (404 when it is not one of theirs).</param>
public sealed record GetSparklinesQuery(int Days, Guid? PortfolioId = null) : IQuery<SparklinesView>, IReadsDatabase;

/// <summary>One stock and its recent adjusted closes.</summary>
/// <param name="Stock">The catalog stock.</param>
/// <param name="Points">The adjusted closes of the last stored days, oldest first; empty while the stock has no price yet.</param>
public sealed record Sparkline(StockSummary Stock, IReadOnlyList<decimal> Points);

/// <summary>The sparklines of the watchlist.</summary>
/// <param name="Days">The number of days asked for.</param>
/// <param name="Items">One line per stock, ordered by ticker and market.</param>
public sealed record SparklinesView(int Days, IReadOnlyList<Sparkline> Items);

/// <summary>Checks the number of days.</summary>
public sealed class GetSparklinesValidator : AbstractValidator<GetSparklinesQuery>
{
    /// <summary>Fewest days a line may have.</summary>
    public const int MinDays = 5;

    /// <summary>Most days a line may have.</summary>
    public const int MaxDays = 250;

    /// <summary>Creates the validator.</summary>
    public GetSparklinesValidator() => RuleFor(x => x.Days).InclusiveBetween(MinDays, MaxDays);
}

/// <summary>Reads the closes of all the caller's stocks in a few statements, whatever their number.</summary>
public sealed class GetSparklinesHandler(ICurrentUser currentUser, IWatchlistRepository items, IPortfolioRepository portfolios, IMarketSnapshots market, IPriceHistory history)
    : IRequestHandler<GetSparklinesQuery, Result<SparklinesView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SparklinesView>> Handle(GetSparklinesQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        Result<IReadOnlyList<Domain.Items.WatchlistItem>> scoped = await PortfolioScope.ItemsAsync(items, portfolios, userId, request.PortfolioId, cancellationToken);
        if (!scoped.IsSuccess)
        {
            return scoped.Error!;
        }

        Guid[] ids = [.. scoped.Value.Select(item => item.StockId)];
        IReadOnlyDictionary<Guid, StockSummary> stocks = await market.FindManyAsync(ids, cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlyList<decimal>> recent = await history.RecentAdjustedClosesAsync([.. stocks.Keys], request.Days, cancellationToken);

        Sparkline[] lines =
        [
            .. stocks.Values
                .OrderBy(stock => stock.Ticker, StringComparer.Ordinal)
                .ThenBy(stock => stock.Market, StringComparer.Ordinal)
                .Select(stock => new Sparkline(stock, recent.TryGetValue(stock.Id, out IReadOnlyList<decimal>? points) ? points : [])),
        ];
        return new SparklinesView(request.Days, lines);
    }
}
