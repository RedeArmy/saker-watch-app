using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Resilience;

/// <summary>
/// Policy of one outbound HTTP client: where it may call, how long it waits, when it retries and when it stops
/// calling a failing provider (security requirements and ADR-0005: allow-listed hosts, timeouts, size limits).
/// </summary>
public sealed class HttpClientResilienceOptions
{
    /// <summary>
    /// Hosts this client may call (exact, case-insensitive): <c>host</c> allows only the default port of the scheme, <c>host:port</c> one
    /// specific port. Anything else is refused before leaving the process.
    /// </summary>
    [Required, MinLength(1)]
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Permit plain <c>http</c>. Only for local fakes; production clients stay HTTPS-only.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Timeout of a single attempt.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:05:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout of the whole call including retries.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after the first attempt (idempotent methods only).</summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay of the exponential backoff (with jitter).</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Failure ratio that opens the circuit.</summary>
    [Range(0.01, 1.0)]
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>Calls needed in the sampling window before the circuit may open.</summary>
    [Range(2, 10_000)]
    public int CircuitMinimumThroughput { get; set; } = 10;

    /// <summary>How long the circuit stays open.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum concurrent calls (bulkhead).</summary>
    [Range(1, 1000)]
    public int MaxConcurrency { get; set; } = 20;

    /// <summary>Maximum response body the client will buffer.</summary>
    [Range(1, 100_000_000)]
    public long MaxResponseBytes { get; set; } = 5_000_000;
}
