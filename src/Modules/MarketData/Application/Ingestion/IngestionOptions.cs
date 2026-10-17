using System.ComponentModel.DataAnnotations;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>How ingestion behaves. Bound from <c>MarketData:Ingestion</c>.</summary>
public sealed class IngestionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData:Ingestion";

    /// <summary>
    /// Years of history a new listing is backfilled with. The longest performance window is five years; ten leaves room, and stays far below
    /// the 5 000 rows one request holds (about twenty years), so a backfill is never truncated.
    /// </summary>
    [Range(1, 19)]
    public int BackfillYears { get; set; } = 10;

    /// <summary>
    /// How long after a session closes its bar is trusted as final. Twelve Data's bar had 1.5 million shares 45 minutes after the close and the
    /// full day seven hours later (ADR-0027), so three hours is the default.
    /// </summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromHours(3);

    /// <summary>Attempts the queue gives a backfill before it is parked as dead (rate limits and outages are retried with backoff).</summary>
    [Range(1, 20)]
    public int BackfillMaxAttempts { get; set; } = 8;

    /// <summary>Whether the worker schedules the daily refresh of each enabled market.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the scheduler looks for a market whose latest session has settled and has no refresh yet.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Days before the newest stored one that a daily refresh asks for again. They catch a provisional day that settled and a late correction, and
    /// they are what the refresh compares against to notice that a dividend or a split rewrote the adjusted history.
    /// </summary>
    [Range(2, 60)]
    public int RefreshOverlapDays { get; set; } = 10;

    /// <summary>Relative difference in a stored adjusted close, beyond which the provider is taken to have rewritten the history.</summary>
    [Range(0.0000001, 0.1)]
    public double RewriteTolerance { get; set; } = 0.000001;

    /// <summary>How often the freshness gauges are measured.</summary>
    public TimeSpan FreshnessInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times one job may put itself back in the queue to wait for the provider's call budget before it counts as a failure. Each wait
    /// lasts until the exhausted window ends (up to a minute for the per-minute budget, until midnight UTC for the daily one), so the default
    /// outlasts a full day of waiting.
    /// </summary>
    [Range(1, 5000)]
    public int MaxRateLimitWaits { get; set; } = 1500;

    /// <summary>Seconds, at most, added to a wait so the jobs that ran out of budget together do not all return at the same instant.</summary>
    [Range(0, 600)]
    public int RateLimitJitterSeconds { get; set; } = 30;
}
