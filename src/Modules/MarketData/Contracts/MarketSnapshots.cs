namespace SakerWatch.MarketData.Contracts;

/// <summary>How current the prices of a stock are.</summary>
public static class PriceStatuses
{
    /// <summary>The newest stored day is the latest session that has settled.</summary>
    public const string Current = "current";

    /// <summary>A session has settled since the newest stored day; the price shown is the last known one and <c>StaleSince</c> says since when.</summary>
    public const string Stale = "stale";

    /// <summary>The stock has no price history yet; its first load is on the way.</summary>
    public const string Pending = "pending";

    /// <summary>The stock has no price and loading it failed for good, so there is none to show until the cause is fixed.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The stock no longer trades on its market (ADR-0039): the last known price is shown and does not go stale, and nothing new is loaded.</summary>
    public const string Delisted = "delisted";
}

/// <summary>The newest stored bar of a stock: its close as traded, in the stock's currency (not the adjusted close the windows use).</summary>
/// <param name="Value">The close as traded.</param>
/// <param name="Date">The trading day.</param>
/// <param name="IsFinal">False while the day may still change.</param>
public sealed record PriceSnapshot(decimal Value, DateOnly Date, bool IsFinal);

/// <summary>One performance window: the change as a fraction (<c>0.0123</c> is 1.23 %) or the reason there is none (ADR-0030).</summary>
/// <param name="Window">One of <c>1D</c>, <c>1W</c>, <c>1M</c>, <c>3M</c>, <c>6M</c>, <c>YTD</c>, <c>1Y</c>, <c>5Y</c>.</param>
/// <param name="Change">The change, or <see langword="null"/> when there is a <paramref name="Reason"/>.</param>
/// <param name="BaselineDate">The day of the close the change is measured from, or <see langword="null"/>.</param>
/// <param name="Reason"><c>no_data</c>, <c>insufficient_history</c>, <c>history_gap</c> or <c>calendar_unavailable</c>; <see langword="null"/> when there is a change.</param>
public sealed record WindowSnapshot(string Window, decimal? Change, DateOnly? BaselineDate, string? Reason);

/// <summary>A stock with its newest price, how current it is and its performance windows.</summary>
/// <param name="Stock">The catalog stock.</param>
/// <param name="PriceStatus">One of <see cref="PriceStatuses"/>.</param>
/// <param name="Price">The newest stored bar, or <see langword="null"/> when there is none.</param>
/// <param name="StaleSince">When the price went stale, or <see langword="null"/> unless <paramref name="PriceStatus"/> is stale.</param>
/// <param name="Windows">Always the eight windows, in order.</param>
/// <param name="DelistedAt">When the stock stopped trading, when <paramref name="PriceStatus"/> is delisted.</param>
public sealed record StockSnapshot(StockSummary Stock, string PriceStatus, PriceSnapshot? Price, DateTimeOffset? StaleSince, IReadOnlyList<WindowSnapshot> Windows, DateTimeOffset? DelistedAt = null);

/// <summary>Read access to the shared catalog and market data for the modules that show stocks to a user (the watchlist, the portfolios).</summary>
public interface IMarketSnapshots
{
    /// <summary>The catalog stocks among <paramref name="stockIds"/>, by id. An id the catalog does not have is left out. Reads only the catalog.</summary>
    Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken);

    /// <summary>The snapshot of every stock among <paramref name="stockIds"/> that the catalog has, by id, in a few statements for all of them.</summary>
    Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken);
}
