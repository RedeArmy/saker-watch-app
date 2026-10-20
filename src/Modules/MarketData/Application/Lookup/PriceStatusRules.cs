using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// How current the prices of a stock are (ADR-0033): the one rule behind the watchlist, the portfolios and the price charts, so they cannot
/// disagree. A stock with no price is pending, or unavailable once loading failed for good; one with a price is current, or stale since the
/// session that settled after it (ADR-0029).
/// </summary>
public static class PriceStatusRules
{
    /// <summary>The status of a stock and, when it is stale, since when.</summary>
    public static (string Status, DateTimeOffset? StaleSince) Of(ExchangeCalendars calendars, Mic market, LatestBar? bar, bool failed, DateTimeOffset now, TimeSpan settle)
    {
        ArgumentNullException.ThrowIfNull(calendars);
        ArgumentNullException.ThrowIfNull(market);

        if (bar is null)
        {
            return (failed ? PriceStatuses.Unavailable : PriceStatuses.Pending, null);
        }

        Result<IExchangeCalendar> calendar = calendars.TryGet(market);
        DateTimeOffset? since = calendar.IsSuccess ? Freshness.StaleSince(calendar.Value, bar.Date, now, settle) : null;
        return since is null ? (PriceStatuses.Current, null) : (PriceStatuses.Stale, since);
    }
}
