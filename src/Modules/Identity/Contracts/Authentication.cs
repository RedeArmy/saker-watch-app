namespace SakerWatch.Identity.Contracts;

/// <summary>Why a token was refused. For logs and metrics only: it is never returned to the client verbatim.</summary>
public enum AuthFailureReason
{
    /// <summary>Not a JWT, empty, or larger than the accepted size.</summary>
    Malformed,

    /// <summary>The signature does not verify with the key the token names.</summary>
    InvalidSignature,

    /// <summary>The token names a key the provider does not publish (even after a refresh).</summary>
    UnknownKey,

    /// <summary>The token is signed with an algorithm other than the allowed one (includes <c>none</c> and HMAC).</summary>
    UnsupportedAlgorithm,

    /// <summary>The token has expired.</summary>
    Expired,

    /// <summary>The token is not valid yet (<c>nbf</c> in the future).</summary>
    NotYetValid,

    /// <summary>The issuer is not the configured Organization.</summary>
    InvalidIssuer,

    /// <summary>The token was not issued for this API.</summary>
    InvalidAudience,

    /// <summary>The token has no usable <c>sub</c> claim.</summary>
    MissingSubject,

    /// <summary>The provider's signing keys could not be obtained; validation fails closed.</summary>
    /// <summary>The token is validly signed but is not an end-user access token: an ID token (no <c>scope</c>) or a delegated/impersonation token (<c>act</c>).</summary>
    NotAnAccessToken,

    /// <summary>Discovery or the key set could not be fetched; fail closed.</summary>
    KeyDiscoveryFailed,

    /// <summary>Any other validation failure.</summary>
    Invalid,
}

/// <summary>The caller proven by a valid access token. The pair <see cref="Issuer"/> and <see cref="Subject"/> is the identity key (ADR-0009).</summary>
/// <param name="Issuer">Organization issuer URL (<c>iss</c>).</param>
/// <param name="Subject">Stable account id (<c>sub</c>).</param>
/// <param name="Email">Email claim, if the token carries one; profile data, never a key.</param>
/// <param name="DisplayName">Display name, if any.</param>
/// <param name="Locale">Locale, if any.</param>
/// <param name="ExpiresAt">When the token stops being valid.</param>
/// <param name="Scopes">Granted OAuth scopes.</param>
public sealed record ValidatedIdentity(
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    string? Locale,
    DateTimeOffset ExpiresAt,
    IReadOnlyCollection<string> Scopes);

/// <summary>Outcome of validating an access token.</summary>
public abstract record AuthResult
{
    private AuthResult()
    {
    }

    /// <summary>True when the token is valid.</summary>
    public abstract bool IsSuccess { get; }

    /// <summary>The token is valid.</summary>
    /// <param name="Identity">The proven caller.</param>
    public sealed record Authenticated(ValidatedIdentity Identity) : AuthResult
    {
        /// <inheritdoc />
        public override bool IsSuccess => true;
    }

    /// <summary>The token is refused.</summary>
    /// <param name="Reason">Why, for logs and metrics.</param>
    public sealed record Rejected(AuthFailureReason Reason) : AuthResult
    {
        /// <inheritdoc />
        public override bool IsSuccess => false;
    }
}

/// <summary>
/// Validates access tokens issued by the identity provider (ADR-0009). Implementations never throw for a bad token and
/// fail closed when the provider's keys are unavailable.
/// </summary>
public interface IAuthProvider
{
    /// <summary>Validates <paramref name="accessToken"/> (the compact JWT, without the <c>Bearer</c> prefix).</summary>
    Task<AuthResult> AuthenticateAsync(string accessToken, CancellationToken cancellationToken);
}
