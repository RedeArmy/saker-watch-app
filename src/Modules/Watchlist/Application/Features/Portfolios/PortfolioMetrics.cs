using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application.Features.Portfolios;

/// <summary>How a portfolio did: the group's average and median change over each window, and its best and worst stock (ADR-0035).</summary>
/// <param name="Id">The portfolio.</param>
public sealed record GetPortfolioMetricsQuery(Guid Id) : IQuery<PortfolioMetrics>, IReadsDatabase;

/// <summary>A stock and its change over a window.</summary>
/// <param name="Stock">The catalog stock.</param>
/// <param name="Change">The change as a fraction (<c>0.0123</c> is 1.23 %).</param>
public sealed record StockChange(StockSummary Stock, decimal Change);

/// <summary>The figures of one window over the stocks that have a number for it.</summary>
/// <param name="Window">The window code.</param>
/// <param name="Count">How many stocks have a change for it; the other figures are over these only.</param>
/// <param name="Average">The equal-weighted average, or <see langword="null"/> when no stock has data.</param>
/// <param name="Median">The median, or <see langword="null"/>.</param>
/// <param name="Best">The stock that rose most, or <see langword="null"/>.</param>
/// <param name="Worst">The stock that rose least or fell most, or <see langword="null"/>.</param>
public sealed record WindowMetrics(string Window, int Count, decimal? Average, decimal? Median, StockChange? Best, StockChange? Worst);

/// <summary>How many stocks of a portfolio have prices in each state (see <see cref="PriceStatuses"/>).</summary>
/// <param name="Current">Prices up to date.</param>
/// <param name="Stale">The last known price is shown; a session has settled since.</param>
/// <param name="Pending">No history yet.</param>
/// <param name="Unavailable">No price and loading failed for good.</param>
public sealed record PriceStatusCounts(int Current, int Stale, int Pending, int Unavailable);

/// <summary>The aggregate numbers of a portfolio. Informational only: nothing here is a recommendation.</summary>
/// <param name="Portfolio">The portfolio.</param>
/// <param name="StockCount">How many stocks it holds.</param>
/// <param name="PriceStatuses">How many are in each price state.</param>
/// <param name="StaleSince">The oldest moment a stale price went stale, or <see langword="null"/> when none is.</param>
/// <param name="Windows">The eight windows, in order.</param>
public sealed record PortfolioMetrics(PortfolioView Portfolio, int StockCount, PriceStatusCounts PriceStatuses, DateTimeOffset? StaleSince, IReadOnlyList<WindowMetrics> Windows);

/// <summary>Reads the stocks of the portfolio with their windows and aggregates them with the one set of rules.</summary>
public sealed class GetPortfolioMetricsHandler(ICurrentUser currentUser, IPortfolioRepository portfolios, IMarketSnapshots market)
    : IRequestHandler<GetPortfolioMetricsQuery, Result<PortfolioMetrics>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PortfolioMetrics>> Handle(GetPortfolioMetricsQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.Id);
        if (await portfolios.FindAsync(userId, id, cancellationToken) is not { } found)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        IReadOnlyList<PortfolioMember> members = await portfolios.MembersAsync(userId, id, cancellationToken);
        IReadOnlyDictionary<Guid, StockSnapshot> snapshots = await market.SnapshotsAsync([.. members.Select(member => member.StockId)], cancellationToken);
        StockSnapshot[] stocks = [.. members.Where(member => snapshots.ContainsKey(member.StockId)).Select(member => snapshots[member.StockId])];

        // The label that names the best and the worst is "MARKET:TICKER", unique in the catalog, so a tie is always broken the same way.
        var byLabel = stocks.ToDictionary(snapshot => Label(snapshot.Stock), snapshot => snapshot.Stock);
        IReadOnlyList<WindowStatistics> statistics = PortfolioStatistics.Aggregate(
            [.. stocks.Select(snapshot => new StockChanges(Label(snapshot.Stock), snapshot.Windows.ToDictionary(window => window.Window, window => window.Change)))]);

        WindowMetrics[] windows = [.. statistics.Select(stat => new WindowMetrics(
            stat.Window, stat.Count, stat.Average, stat.Median, Named(stat.Best, byLabel), Named(stat.Worst, byLabel)))];
        var counts = new PriceStatusCounts(
            stocks.Count(s => s.PriceStatus == PriceStatuses.Current),
            stocks.Count(s => s.PriceStatus == PriceStatuses.Stale),
            stocks.Count(s => s.PriceStatus == PriceStatuses.Pending),
            stocks.Count(s => s.PriceStatus == PriceStatuses.Unavailable));
        DateTimeOffset[] staleMoments = [.. stocks.Where(s => s.StaleSince is not null).Select(s => s.StaleSince!.Value)];
        DateTimeOffset? staleSince = staleMoments.Length == 0 ? null : staleMoments.Min();

        return new PortfolioMetrics(PortfolioView.Of(found), stocks.Length, counts, staleSince, windows);
    }

    private static string Label(StockSummary stock) => $"{stock.Market}:{stock.Ticker}";

    private static StockChange? Named(WindowExtreme? extreme, Dictionary<string, StockSummary> byLabel) =>
        extreme is null ? null : new StockChange(byLabel[extreme.Stock], extreme.Change);
}
