using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Application.Idempotency;

/// <summary>Idempotency settings, bound from the <c>Idempotency</c> section.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Idempotency";

    /// <summary>How long a stored key is kept before the Retention job may purge it (ADR-0017: 24-72 hours).</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(48);
}
