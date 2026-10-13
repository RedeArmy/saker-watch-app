using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Persistence.Outbox;

/// <summary>Outbox dispatcher settings, bound from the <c>Outbox</c> section (Worker only).</summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Outbox";

    /// <summary>How long the dispatcher sleeps when no message is due.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Attempts before a message is parked as dead and no longer retried.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Delay after the first failure; it doubles per attempt.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest retry delay.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(10);
}
