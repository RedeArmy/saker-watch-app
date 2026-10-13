using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Web.Correlation;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;

namespace SakerWatch.BuildingBlocks.Web;

/// <summary>One-call registration of the HTTP building blocks used by <c>Host.Api</c>.</summary>
public static class WebSetup
{
    /// <summary>
    /// Registers <see cref="TimeProvider"/>, the id generator, ProblemDetails, rate limiting and health checks.
    /// </summary>
    public static IServiceCollection AddSakerWeb(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
        services.AddSakerProblemDetails();
        services.AddSakerRateLimiting(configuration);
        services.AddHealthChecks();
        return services;
    }

    /// <summary>
    /// Adds the front of the request pipeline: correlation id first (so errors carry it), then the exception
    /// handler and status-code pages. Call it first, before routing and authentication.
    /// </summary>
    public static IApplicationBuilder UseSakerWeb(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>
    /// Adds the rate limiter. It must run after <c>UseRouting</c> (so per-endpoint policies and
    /// <c>DisableRateLimiting</c> are honored) and after authentication (so callers are partitioned by user, not by IP).
    /// </summary>
    public static IApplicationBuilder UseSakerRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRateLimiter();
        return app;
    }
}
