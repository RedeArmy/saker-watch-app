namespace SakerWatch.MarketData.Infrastructure.Providers.Edgar;

/// <summary>
/// Spaces requests at least a minimum time apart, whatever the number of callers (ADR-0040): the SEC's fair access policy allows ten a second,
/// and a caller that goes over it is blocked for a while. One gate belongs to one provider instance (a singleton), so every request of the
/// process waits its turn. The clock is injected, so a test needs no real waiting.
/// </summary>
internal sealed class RequestGate(TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Waits until at least <paramref name="minimum"/> has passed since the previous request left, then takes the turn.</summary>
    public async Task WaitAsync(TimeSpan minimum, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            TimeSpan wait = minimum - (clock.GetUtcNow() - _last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, cancellationToken);
            }

            _last = clock.GetUtcNow();
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _turn.Dispose();
}
