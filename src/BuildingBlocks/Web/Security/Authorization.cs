using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.BuildingBlocks.Web.Security;

/// <summary>Names of the authorization policies.</summary>
public static class SakerPolicies
{
    /// <summary>The caller presented a valid token. Same as the default and the fallback; named so endpoints can say it explicitly.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>The caller is a resolved, active internal user (the user-resolution middleware ran and accepted the account).</summary>
    public const string ActiveUser = "active-user";

    /// <summary>The caller owns the resource being accessed; evaluated with <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c>.</summary>
    public const string Owner = "owner";
}

/// <summary>A resource that belongs to one user (a watchlist item, a setting...).</summary>
public interface IOwnedResource
{
    /// <summary>Internal id of the owner.</summary>
    Guid OwnerId { get; }
}

/// <summary>Requirement that the caller is the owner of an <see cref="IOwnedResource"/>.</summary>
public sealed class OwnerRequirement : IAuthorizationRequirement;

/// <summary>Requirement that the token carries a given OAuth scope.</summary>
/// <param name="scope">The required scope.</param>
public sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    /// <summary>The required scope.</summary>
    public string Scope { get; } = scope;
}

/// <summary>Succeeds when the internal user id of the caller equals the resource's owner.</summary>
public sealed class ResourceOwnerHandler : AuthorizationHandler<OwnerRequirement, IOwnedResource>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerRequirement requirement, IOwnedResource resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);

        if (Guid.TryParse(context.User.FindFirst(SakerClaims.UserId)?.Value, out Guid caller) && caller == resource.OwnerId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Succeeds when the caller's token carries the required scope.</summary>
public sealed class ScopeHandler : AuthorizationHandler<ScopeRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.FindAll(SakerClaims.Scope).Any(claim => string.Equals(claim.Value, requirement.Scope, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Authorization setup and the helpers endpoints use.</summary>
public static class AuthorizationSetup
{
    /// <summary>Error code returned when a resource is not visible to the caller.</summary>
    public const string ResourceNotFound = "resource.not_found";

    /// <summary>
    /// Registers authorization with a <b>default-deny</b> fallback policy: an endpoint that declares nothing requires an
    /// authenticated caller, so only endpoints explicitly marked anonymous (health and liveness) are open (ADR-0009).
    /// </summary>
    public static IServiceCollection AddSakerAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            AuthorizationPolicy authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.DefaultPolicy = authenticated;
            options.FallbackPolicy = authenticated;
            options.AddPolicy(SakerPolicies.Authenticated, authenticated);
            options.AddPolicy(SakerPolicies.ActiveUser, policy => policy.RequireAuthenticatedUser().RequireClaim(SakerClaims.UserId));
            options.AddPolicy(SakerPolicies.Owner, policy => policy.RequireAuthenticatedUser().AddRequirements(new OwnerRequirement()));
        });
        services.AddSingleton<IAuthorizationHandler, ResourceOwnerHandler>();
        services.AddSingleton<IAuthorizationHandler, ScopeHandler>();
        return services;
    }

    /// <summary>Adds a policy named <paramref name="name"/> that requires an authenticated caller whose token carries <paramref name="scope"/>.</summary>
    public static AuthorizationOptions AddScopePolicy(this AuthorizationOptions options, string name, string scope)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(name, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(scope)));
        return options;
    }

    /// <summary>
    /// Checks that the caller owns <paramref name="resource"/>. A resource that belongs to someone else is reported as
    /// <b>not found</b>, not forbidden, so the API does not reveal that it exists (OWASP API1, broken object-level authorization).
    /// </summary>
    public static async Task<Result> RequireOwnerAsync(this IAuthorizationService authorization, System.Security.Claims.ClaimsPrincipal user, IOwnedResource resource)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        AuthorizationResult result = await authorization.AuthorizeAsync(user, resource, SakerPolicies.Owner);
        return result.Succeeded ? Result.Success() : Error.NotFound(ResourceNotFound, "The resource was not found.");
    }
}
