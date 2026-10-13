using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Web.Problems;

namespace SakerWatch.BuildingBlocks.Web.Security;

/// <summary>The caller of the current request, set once by <see cref="UserResolutionMiddleware"/> from the validated token and never from client input.</summary>
public sealed class CurrentUserAccessor : ICurrentUser
{
    /// <inheritdoc />
    public Guid? UserId { get; set; }
}

/// <summary>Stable error codes of user resolution.</summary>
public static class UserResolutionErrors
{
    /// <summary>The account exists but cannot be used (pending deletion or deleted) (403).</summary>
    public const string AccountInactive = "identity.account_inactive";
}

/// <summary>
/// After authentication, maps the token's identity to the internal user (creating it on first sight), sets
/// <see cref="ICurrentUser"/> for the rest of the request (so row-level security, idempotency and the rate limiter see the
/// real user) and adds the internal id as a claim. An account that is not active is refused with <c>403</c>. Anonymous
/// requests pass through untouched; authorization decides what they may reach.
/// </summary>
/// <param name="next">Next middleware.</param>
public sealed class UserResolutionMiddleware(RequestDelegate next)
{
    /// <summary>Processes one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst(SakerClaims.UserId) is null)
        {
            var identity = new ExternalIdentity(
                context.User.FindFirstValue(SakerClaims.Issuer) ?? string.Empty,
                context.User.FindFirstValue(SakerClaims.Subject) ?? string.Empty,
                context.User.FindFirstValue(SakerClaims.Email),
                context.User.FindFirstValue(SakerClaims.Name),
                context.User.FindFirstValue(SakerClaims.Locale));

            Result<ResolvedUser> resolved = await context.RequestServices.GetRequiredService<IUserResolver>().ResolveAsync(identity, context.RequestAborted);
            if (!resolved.IsSuccess)
            {
                await resolved.Error!.ToProblem().ExecuteAsync(context);
                return;
            }

            if (resolved.Value.Status != UserAccountStatus.Active)
            {
                await Error.Forbidden(UserResolutionErrors.AccountInactive, "This account cannot be used.").ToProblem().ExecuteAsync(context);
                return;
            }

            context.RequestServices.GetRequiredService<CurrentUserAccessor>().UserId = resolved.Value.UserId;
            ((ClaimsIdentity)context.User.Identity).AddClaim(new Claim(SakerClaims.UserId, resolved.Value.UserId.ToString("D")));
        }

        await next(context);
    }
}

/// <summary>Registration of the per-request current user.</summary>
public static class UserResolutionExtensions
{
    /// <summary>
    /// Makes <see cref="ICurrentUser"/> resolve to the request's <see cref="CurrentUserAccessor"/>. Call it after the pipeline and
    /// persistence registrations, so it replaces their anonymous default.
    /// </summary>
    public static IServiceCollection AddSakerCurrentUser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<CurrentUserAccessor>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUserAccessor>());
        return services;
    }

    /// <summary>Adds the user-resolution middleware. Place it right after <c>UseAuthentication</c> and before authorization and the rate limiter.</summary>
    public static IApplicationBuilder UseSakerUserResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<UserResolutionMiddleware>();
    }
}
