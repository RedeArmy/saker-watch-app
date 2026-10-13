using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Identity.Contracts;

namespace SakerWatch.Identity.Infrastructure.Clavaris;

/// <summary>
/// ASP.NET Core authentication scheme for <c>Authorization: Bearer</c> tokens, delegating all validation to
/// <see cref="IAuthProvider"/> so the API and any other caller share one validation path. A refused token yields a plain
/// <c>401</c> with <c>WWW-Authenticate: Bearer</c> and no detail about why (the reason goes to the metric and logs only).
/// </summary>
public sealed class ClavarisBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Name of the scheme.</summary>
    public const string SchemeName = "Bearer";

    private const string _failureKey = "saker.auth_failed";

    private readonly IAuthProvider _provider;

    /// <summary>Creates the handler.</summary>
    public ClavarisBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthProvider provider)
        : base(options, logger, encoder)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) || values.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count > 1 || !AuthenticationHeaderValue.TryParse(values[0], out AuthenticationHeaderValue? header))
        {
            return Refuse("malformed authorization header");
        }

        if (!string.Equals(header.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (string.IsNullOrEmpty(header.Parameter))
        {
            return Refuse("empty bearer token");
        }

        AuthResult result = await _provider.AuthenticateAsync(header.Parameter, Context.RequestAborted);
        if (result is not AuthResult.Authenticated authenticated)
        {
            return Refuse(((AuthResult.Rejected)result).Reason.ToString());
        }

        ValidatedIdentity identity = authenticated.Identity;
        var claims = new List<Claim>
        {
            new(SakerClaims.Subject, identity.Subject),
            new(SakerClaims.Issuer, identity.Issuer),
        };
        AddIfPresent(claims, SakerClaims.Email, identity.Email);
        AddIfPresent(claims, SakerClaims.Name, identity.DisplayName);
        AddIfPresent(claims, SakerClaims.Locale, identity.Locale);
        claims.AddRange(identity.Scopes.Select(scope => new Claim(SakerClaims.Scope, scope)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, nameType: SakerClaims.Subject, roleType: SakerClaims.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = Context.Items.ContainsKey(_failureKey)
            ? "Bearer realm=\"saker-watch\", error=\"invalid_token\""
            : "Bearer realm=\"saker-watch\"";
        await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required.", type: "urn:sakerwatch:error:auth.unauthorized", extensions: new Dictionary<string, object?> { ["code"] = "auth.unauthorized" })
            .ExecuteAsync(Context);
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Access denied.", type: "urn:sakerwatch:error:auth.forbidden", extensions: new Dictionary<string, object?> { ["code"] = "auth.forbidden" })
            .ExecuteAsync(Context);

    // Remembers that a credential was presented and refused, so the challenge can say invalid_token.
    private AuthenticateResult Refuse(string reason)
    {
        Context.Items[_failureKey] = true;
        return AuthenticateResult.Fail(reason);
    }

    private static void AddIfPresent(List<Claim> claims, string type, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            claims.Add(new Claim(type, value));
        }
    }
}
