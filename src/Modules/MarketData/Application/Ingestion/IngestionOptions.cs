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
}
