using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SakerWatch.Identity.Infrastructure.Clavaris;

/// <summary>
/// The provider's signing keys, discovered from <c>{Authority}/.well-known/openid-configuration</c> and its <c>jwks_uri</c>.
/// Keys are cached for <see cref="ClavarisOptions.MetadataLifetime"/>. A forced refresh (for an unknown <c>kid</c>) is allowed at
/// most once per <see cref="ClavarisOptions.MinimumMetadataRefresh"/>, so a caller inventing key ids cannot make us hammer the
/// provider, and the same spacing applies after a failed fetch. When the provider cannot be reached the last known keys keep
/// being served for up to <see cref="ClavarisOptions.MaximumKeyAge"/> (an outage of the provider must not lock every user out
/// while valid keys are in memory); after that the cache fails closed. A refresh is awaited by the caller (unlike the framework's
/// background refresh), so the first token signed with a freshly rotated key is already accepted. Reading fresh keys takes no lock;
/// while a refresh runs, callers that hold usable keys carry on with them instead of queueing behind it, and a refresh that
/// has keys to fall back on gives up after <see cref="ClavarisOptions.StaleRefreshTimeout"/>, so a slow provider delays at most one
/// request per refresh interval and only by that long. All timing uses the injected clock. If no keys can be obtained, callers fail closed.
/// </summary>
internal sealed class SigningKeyCache(HttpClient http, ClavarisOptions options, TimeProvider timeProvider, Action<Exception, TimeSpan>? onStaleKeysServed = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Replaced as a whole under the gate and read without it, so the common case (fresh keys) never waits.
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public void Dispose() => _gate.Dispose();

    /// <summary>Keys for validation: the cache while it is fresh, otherwise a new fetch.</summary>
    /// <exception cref="InvalidOperationException">No keys could be obtained.</exception>
    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken cancellationToken)
    {
        Snapshot current = _snapshot;
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (current.IsFresh(now, options.MetadataLifetime))
        {
            return current.Keys;
        }

        // Usable keys exist: if another caller is already refreshing, carry on with them rather than queueing behind a slow provider.
        bool usable = current.IsFresh(now, options.MaximumKeyAge);
        if (usable)
        {
            if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                return current.Keys;
            }
        }
        else
        {
            await _gate.WaitAsync(cancellationToken);
        }

        try
        {
            current = _snapshot;
            now = timeProvider.GetUtcNow();
            if (current.IsFresh(now, options.MetadataLifetime))
            {
                return current.Keys;
            }

            // A fetch failed moments ago: do not make every request wait for another one.
            if (_lastAttempt != DateTimeOffset.MinValue && now - _lastAttempt < options.MinimumMetadataRefresh)
            {
                return current.IsFresh(now, options.MaximumKeyAge)
                    ? current.Keys
                    : throw new InvalidOperationException("The identity provider's keys were unavailable moments ago; not retrying yet.");
            }

            return await FetchAsync(now, mustSucceed: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-reads the keys because a token named an unknown <c>kid</c>. Rate limited: inside the minimum interval the current
    /// keys are returned unchanged. A failed refresh keeps serving the current keys.
    /// </summary>
    public async Task<IReadOnlyList<SecurityKey>> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (now - _lastAttempt < options.MinimumMetadataRefresh)
            {
                return _snapshot.Keys;
            }

            return await FetchAsync(now, mustSucceed: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> FetchAsync(DateTimeOffset now, bool mustSucceed, CancellationToken cancellationToken)
    {
        _lastAttempt = now;
        Snapshot previous = _snapshot;

        // With keys to fall back on there is no reason to wait for a slow provider for the full client budget.
        using var timeout = new CancellationTokenSource(options.StaleRefreshTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken fetchToken = previous.HasKeys ? linked.Token : cancellationToken;
        try
        {
            string authority = options.Authority.TrimEnd('/');
            using var discovery = JsonDocument.Parse(await http.GetStringAsync($"{authority}/.well-known/openid-configuration", fetchToken));
            string? issuer = discovery.RootElement.TryGetProperty("issuer", out JsonElement i) ? i.GetString() : null;
            string? jwksUri = discovery.RootElement.TryGetProperty("jwks_uri", out JsonElement j) ? j.GetString() : null;
            if (!string.Equals(issuer?.TrimEnd('/'), authority, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(jwksUri))
            {
                throw new InvalidOperationException("The discovery document does not describe the configured Authority.");
            }

            var jwks = new JsonWebKeySet(await http.GetStringAsync(jwksUri, fetchToken));
            SecurityKey[] keys = [.. jwks.GetSigningKeys().Where(IsUsable)];
            if (keys.Length == 0)
            {
                throw new InvalidOperationException("The identity provider published no usable RS256 signing keys.");
            }

            _snapshot = new Snapshot(keys, now, HasKeys: true);
            return keys;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && previous.HasKeys && (!mustSucceed || now - previous.FetchedAt < options.MaximumKeyAge))
        {
            // The refresh failed (or timed out) but we still hold usable keys: keep serving them (bounded by MaximumKeyAge) and say so.
            onStaleKeysServed?.Invoke(ex, now - previous.FetchedAt);
            return previous.Keys;
        }
    }

    // Only RSA keys meant for signatures; a published key for another purpose or algorithm is ignored.
    private static bool IsUsable(SecurityKey key) =>
        key is RsaSecurityKey or JsonWebKey { Kty: JsonWebAlgorithmsKeyTypes.RSA, Use: null or "sig" };

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, DateTimeOffset FetchedAt, bool HasKeys)
    {
        public static readonly Snapshot Empty = new([], DateTimeOffset.MinValue, HasKeys: false);

        public bool IsFresh(DateTimeOffset now, TimeSpan lifetime) => HasKeys && now - FetchedAt < lifetime;
    }
}
