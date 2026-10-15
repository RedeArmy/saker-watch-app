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
/// background refresh), so the first token signed with a freshly rotated key is already accepted. All timing uses the injected
/// clock. If no keys can be obtained, callers fail closed.
/// </summary>
internal sealed class SigningKeyCache(HttpClient http, ClavarisOptions options, TimeProvider timeProvider, Action<Exception, TimeSpan>? onStaleKeysServed = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private bool _hasKeys;

    public void Dispose() => _gate.Dispose();

    /// <summary>Keys for validation: the cache while it is fresh, otherwise a new fetch.</summary>
    /// <exception cref="InvalidOperationException">No keys could be obtained.</exception>
    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (_hasKeys && now - _fetchedAt < options.MetadataLifetime)
            {
                return _keys;
            }

            // A fetch failed moments ago: do not make every request wait for another one.
            if (_lastAttempt != DateTimeOffset.MinValue && now - _lastAttempt < options.MinimumMetadataRefresh)
            {
                return _hasKeys && now - _fetchedAt < options.MaximumKeyAge
                    ? _keys
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
                return _keys;
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
        try
        {
            string authority = options.Authority.TrimEnd('/');
            using var discovery = JsonDocument.Parse(await http.GetStringAsync($"{authority}/.well-known/openid-configuration", cancellationToken));
            string? issuer = discovery.RootElement.TryGetProperty("issuer", out JsonElement i) ? i.GetString() : null;
            string? jwksUri = discovery.RootElement.TryGetProperty("jwks_uri", out JsonElement j) ? j.GetString() : null;
            if (!string.Equals(issuer?.TrimEnd('/'), authority, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(jwksUri))
            {
                throw new InvalidOperationException("The discovery document does not describe the configured Authority.");
            }

            var jwks = new JsonWebKeySet(await http.GetStringAsync(jwksUri, cancellationToken));
            SecurityKey[] keys = [.. jwks.GetSigningKeys().Where(IsUsable)];
            if (keys.Length == 0)
            {
                throw new InvalidOperationException("The identity provider published no usable RS256 signing keys.");
            }

            _keys = keys;
            _fetchedAt = now;
            _hasKeys = true;
            return keys;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _hasKeys && (!mustSucceed || now - _fetchedAt < options.MaximumKeyAge))
        {
            // The refresh failed but we still hold usable keys: keep serving them (bounded by MaximumKeyAge) and say so.
            onStaleKeysServed?.Invoke(ex, now - _fetchedAt);
            return _keys;
        }
    }

    // Only RSA keys meant for signatures; a published key for another purpose or algorithm is ignored.
    private static bool IsUsable(SecurityKey key) =>
        key is RsaSecurityKey or JsonWebKey { Kty: JsonWebAlgorithmsKeyTypes.RSA, Use: null or "sig" };
}
