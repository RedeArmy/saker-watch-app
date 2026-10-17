using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>The newest day stored for an ingestable listing, or none yet.</summary>
/// <param name="Stock">The listing.</param>
/// <param name="Latest">Its newest stored trading day, or <see langword="null"/> when it has no history yet.</param>
public sealed record ListingLatest(StockId Stock, DateOnly? Latest);

/// <summary>Read access to how recent each listing's prices are.</summary>
public interface IFreshnessReader
{
    /// <summary>The newest stored day of every ingestable listing of a market (active, and not given up on).</summary>
    Task<IReadOnlyList<ListingLatest>> LatestPerListingAsync(Mic market, CancellationToken cancellationToken);
}

/// <summary>
/// When a listing's prices went stale. A listing is stale from the moment the session after its newest stored day has settled (its close plus
/// the settle delay): from then on a newer price exists and the product does not have it. Pure, so the watchlist and the metrics agree.
/// </summary>
public static class Freshness
{
    /// <summary>
    /// The instant the prices became stale, or <see langword="null"/> when they are current or there is no history to be stale about.
    /// A provisional or missing latest day is the ingestion's business, not a staleness reading.
    /// </summary>
    public static DateTimeOffset? StaleSince(IExchangeCalendar calendar, DateOnly? latest, DateTimeOffset now, TimeSpan settleDelay)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (latest is not { } day)
        {
            return null;
        }

        DateOnly next = calendar.NextTradingDay(day);
        DateTimeOffset settledAt = calendar.CloseOf(next)!.Value + settleDelay;
        return now >= settledAt ? settledAt : null;
    }
}
