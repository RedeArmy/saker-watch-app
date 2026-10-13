namespace SakerWatch.BuildingBlocks.Kernel.Time;

/// <summary>
/// Retry delay that doubles with every attempt up to a cap, with random jitter so a burst of failures does not retry in
/// lockstep (ADR-0005). The random source is injectable to keep tests deterministic.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _cap;
    private readonly double _jitterRatio;
    private readonly Func<double> _nextRandom;

    /// <summary>Creates a backoff policy.</summary>
    /// <param name="baseDelay">Delay after the first failure.</param>
    /// <param name="cap">Longest delay.</param>
    /// <param name="jitterRatio">Fraction of the delay that is randomized, between 0 and 1 (0.2 means plus or minus 20%).</param>
    /// <param name="nextRandom">Returns a value in [0, 1); defaults to <see cref="Random.Shared"/>.</param>
    public ExponentialBackoff(TimeSpan baseDelay, TimeSpan cap, double jitterRatio = 0.2, Func<double>? nextRandom = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, baseDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterRatio);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterRatio, 1);

        _baseDelay = baseDelay;
        _cap = cap;
        _jitterRatio = jitterRatio;
        _nextRandom = nextRandom ?? Random.Shared.NextDouble;
    }

    /// <summary>Delay before the next try, given how many attempts have already failed (1 for the first failure).</summary>
    public TimeSpan Delay(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);

        double seconds = _baseDelay.TotalSeconds * Math.Pow(2, Math.Min(failedAttempts - 1, 30));
        double capped = Math.Min(seconds, _cap.TotalSeconds);
        double jitter = 1 + (((_nextRandom() * 2) - 1) * _jitterRatio);
        return TimeSpan.FromSeconds(Math.Min(capped * jitter, _cap.TotalSeconds));
    }
}
