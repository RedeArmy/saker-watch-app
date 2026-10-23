using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.Identity.Contracts;

namespace SakerWatch.Identity.Infrastructure.Clavaris;

/// <summary>
/// Validates Clavaris access tokens (ADR-0009): RS256 only (so <c>none</c> and HMAC confusion are refused before any key is
/// touched), the issuer must be exactly the configured Organization, the audience must be this API, expiry and not-before
/// use a clock skew of at most 60 seconds, and signing keys come from the discovered JWKS, cached and refreshed on an unknown
/// <c>kid</c> no more often than <see cref="ClavarisOptions.MinimumMetadataRefresh"/>. When the keys cannot be obtained the
/// token is refused (fail closed).
/// </summary>
public sealed partial class ClavarisAuthProvider : IAuthProvider, IDisposable
{
    /// <summary>Name of the HTTP client that fetches discovery and JWKS documents (host allow-listed to the Authority).</summary>
    public const string MetadataClientName = "clavaris-metadata";

    private const int _maxTokenLength = 8192;
    private const int _maxScopeLength = 320;

    private static readonly Counter<long> _attempts = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_auth_attempts_total",
        description: "Access token validations by outcome (success) or refusal reason.");

    private readonly ClavarisOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClavarisAuthProvider> _logger;
    private readonly SigningKeyCache _keys;
    private volatile ParametersForKeys? _parameters;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>Creates the provider.</summary>
    public ClavarisAuthProvider(
        IOptions<ClavarisOptions> options,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<ClavarisAuthProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        _keys = new SigningKeyCache(
            httpClientFactory.CreateClient(MetadataClientName),
            _options,
            timeProvider,
            (exception, age) => StaleKeysServed(exception, (int)age.TotalMinutes));
    }

    /// <inheritdoc />
    public void Dispose() => _keys.Dispose();

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(string accessToken, CancellationToken cancellationToken)
    {
        AuthResult result = await ValidateAsync(accessToken, cancellationToken);
        Record(result);
        return result;
    }

    [SuppressMessage("Major Code Smell", "S138:Functions should not have too many lines of code", Justification = "Predates the size rule (ADR-0049): split it the next time it changes.")]
    private async Task<AuthResult> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > _maxTokenLength)
        {
            return Rejected(AuthFailureReason.Malformed);
        }

        JsonWebToken token;
        try
        {
            token = new JsonWebToken(accessToken);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return Rejected(AuthFailureReason.Malformed);
        }

        // Checked before any key is looked up: this is what shuts out "alg: none" and HS256-with-the-public-key attacks.
        if (!string.Equals(token.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            return Rejected(AuthFailureReason.UnsupportedAlgorithm);
        }

        IReadOnlyList<SecurityKey> keys;
        try
        {
            keys = await _keys.GetKeysAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes a client-side timeout: it is a provider failure (fail closed with 401), not the caller going away.
            DiscoveryFailed(ex);
            return Rejected(AuthFailureReason.KeyDiscoveryFailed);
        }

        TokenValidationResult validation = await _handler.ValidateTokenAsync(accessToken, Parameters(keys));
        if (!validation.IsValid && validation.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The provider may have rotated its keys: re-read them (rate limited) and try once more with any new ones.
            IReadOnlyList<SecurityKey> refreshed = await _keys.RefreshAsync(cancellationToken);
            if (!ReferenceEquals(refreshed, keys))
            {
                validation = await _handler.ValidateTokenAsync(accessToken, Parameters(refreshed));
            }
        }

        if (validation.IsValid)
        {
            return ToIdentity(validation);
        }

        AuthFailureReason reason = Reason(validation.Exception);
        if (reason == AuthFailureReason.Invalid)
        {
            // A rejection we have no specific reason for: keep the exception type so it can be classified later.
            UnclassifiedRejection(validation.Exception!, validation.Exception?.GetType().Name ?? "none");
        }

        return Rejected(reason);
    }

    // The parameters depend only on the options and the key set, so they are built once per key set and shared by every request
    // (the handler only reads them); a refreshed key set is a new list instance and gets new parameters.
    private TokenValidationParameters Parameters(IReadOnlyList<SecurityKey> keys)
    {
        ParametersForKeys? cached = _parameters;
        if (cached is not null && ReferenceEquals(cached.Keys, keys))
        {
            return cached.Parameters;
        }

        var built = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Authority.TrimEnd('/'),
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = _options.ClockSkew,
            LifetimeValidator = ValidateLifetime,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };
        _parameters = new ParametersForKeys(keys, built);
        return built;
    }

    private sealed record ParametersForKeys(IReadOnlyList<SecurityKey> Keys, TokenValidationParameters Parameters);

    private static AuthResult ToIdentity(TokenValidationResult validation)
    {
        var token = (JsonWebToken)validation.SecurityToken;
        string? subject = Claim(token, "sub", ExternalIdentityLimits.MaxSubjectLength);
        if (subject is null)
        {
            return Rejected(AuthFailureReason.MissingSubject);
        }

        // Clavaris signs ID tokens with the same key and the same audience (the client id), so signature and audience alone cannot
        // tell them from an access token (verified live: an ID token was accepted). An access token always carries "scope"; an
        // ID token never does. A delegated token ("act", operator impersonation) is refused until the API can attribute it.
        if (!token.TryGetClaim("scope", out _) || token.TryGetClaim("act", out _))
        {
            return Rejected(AuthFailureReason.NotAnAccessToken);
        }

        string[] scopes = Scopes(token);
        return new AuthResult.Authenticated(new ValidatedIdentity(
            token.Issuer,
            subject,
            Claim(token, "email", ExternalIdentityLimits.MaxProfileTextLength),
            Claim(token, "name", ExternalIdentityLimits.MaxProfileTextLength) ?? Claim(token, "preferred_username", ExternalIdentityLimits.MaxProfileTextLength),
            Claim(token, "locale", ExternalIdentityLimits.MaxLocaleLength),
            new DateTimeOffset(token.ValidTo, TimeSpan.Zero),
            scopes));
    }

    // IdentityModel reads the system clock; ours comes from the injected TimeProvider so tests (and clock-drift reasoning) are exact.
    private bool ValidateLifetime(DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        if (expires is null)
        {
            throw new SecurityTokenNoExpirationException("The token has no expiry.");
        }

        // Compare against now +/- skew rather than shifting the token's times, which can overflow when a time is
        // DateTime.MinValue (no nbf) or MaxValue.
        if (expires.Value < now - _options.ClockSkew)
        {
            throw new SecurityTokenExpiredException("The token has expired.") { Expires = expires.Value };
        }

        if (notBefore is { } start && start > now + _options.ClockSkew)
        {
            throw new SecurityTokenNotYetValidException("The token is not valid yet.") { NotBefore = start };
        }

        return true;
    }

    // Clavaris emits "scope" as a JSON array (verified against a running instance, A-022); RFC 9068 and most providers use a
    // space-delimited string. Accept both so a provider change cannot silently strip every scope.
    private static string[] Scopes(JsonWebToken token)
    {
        IEnumerable<string> raw = [];
        if (token.TryGetPayloadValue("scope", out JsonElement element))
        {
            raw = element.ValueKind switch
            {
                JsonValueKind.String => (element.GetString() ?? string.Empty).Split(' '),
                JsonValueKind.Array => element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty),
                _ => [],
            };
        }
        else if (token.TryGetPayloadValue("scope", out string text))
        {
            // Not dead: the typed read as a JsonElement answers only for a claim that is an array or an object, and a space-delimited string
            // (RFC 9068) is read as a string. Removing this branch strips every scope of a provider that sends the string form.
            raw = text.Split(' ');
        }

        return raw.Select(scope => scope.Trim()).Where(scope => scope.Length is > 0 and <= _maxScopeLength && !scope.Any(char.IsControl)).Distinct(StringComparer.Ordinal).ToArray();
    }

    // Claims become profile data and log context; bound and clean them before they travel further. The bounds are the ones the user
    // table enforces, so a value that gets past here can always be provisioned: an oversized profile field is dropped (it is only
    // profile data) and an oversized subject leaves the token without a usable one.
    private static string? Claim(JsonWebToken token, string name, int maxLength) =>
        token.TryGetPayloadValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl)
            ? value
            : null;

    private static AuthFailureReason Reason(Exception? exception) => exception switch
    {
        SecurityTokenExpiredException => AuthFailureReason.Expired,
        SecurityTokenNotYetValidException => AuthFailureReason.NotYetValid,
        SecurityTokenInvalidIssuerException => AuthFailureReason.InvalidIssuer,
        SecurityTokenInvalidAudienceException => AuthFailureReason.InvalidAudience,
        SecurityTokenSignatureKeyNotFoundException => AuthFailureReason.UnknownKey,
        SecurityTokenInvalidSignatureException => AuthFailureReason.InvalidSignature,
        SecurityTokenInvalidAlgorithmException => AuthFailureReason.UnsupportedAlgorithm,
        _ => AuthFailureReason.Invalid,
    };

    private static AuthResult.Rejected Rejected(AuthFailureReason reason) => new(reason);

    private static void Record(AuthResult result) =>
        _attempts.Add(1, new KeyValuePair<string, object?>("outcome", result is AuthResult.Rejected rejected ? rejected.Reason.ToString() : "success"));

    [LoggerMessage(EventId = 9602, Level = LogLevel.Warning, Message = "Access token rejected for an unclassified reason ({ExceptionType})")]
    private partial void UnclassifiedRejection(Exception exception, string exceptionType);

    [LoggerMessage(EventId = 9603, Level = LogLevel.Warning, Message = "Could not refresh the identity provider's signing keys; serving the cached keys, fetched {AgeMinutes} minutes ago")]
    private partial void StaleKeysServed(Exception exception, int ageMinutes);

    [LoggerMessage(EventId = 9601, Level = LogLevel.Error, Message = "Could not obtain the identity provider's signing keys; tokens are refused until they can be fetched")]
    private partial void DiscoveryFailed(Exception exception);
}
