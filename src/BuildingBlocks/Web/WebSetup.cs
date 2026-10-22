using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Application.Deployment;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Web.Correlation;
using SakerWatch.BuildingBlocks.Web.Deployment;
using SakerWatch.BuildingBlocks.Web.Health;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Json;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;
using SakerWatch.BuildingBlocks.Web.Security;

namespace SakerWatch.BuildingBlocks.Web;

/// <summary>One-call registration of the HTTP building blocks used by <c>Host.Api</c>.</summary>
public static class WebSetup
{
    /// <summary>
    /// Registers <see cref="TimeProvider"/>, the id generator, ProblemDetails, rate limiting, forwarded headers, CORS, HSTS and health checks.
    /// </summary>
    public static IServiceCollection AddSakerWeb(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
        services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new DecimalAsStringConverter()));
        services.AddSakerProblemDetails();
        services.AddSakerRateLimiting(configuration);
        // The API processes count each other through Redis where there is one; the count replaces the declared one (ADR-0048).
        services.AddSingleton<ApiInstanceRegistry>();
        services.Replace(ServiceDescriptor.Singleton<IApiInstances>(provider => provider.GetRequiredService<ApiInstanceRegistry>()));
        services.AddHostedService(provider => provider.GetRequiredService<ApiInstanceRegistry>());
        services.AddSakerForwardedHeaders(configuration);
        services.AddSakerCors(configuration);
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });
        services.AddHealthChecks();
        services.AddSakerReadiness();
        return services;
    }

    /// <summary>Largest request body the API accepts (1 MiB): it only takes small JSON documents.</summary>
    public const long MaxRequestBodyBytes = 1_048_576;

    /// <summary>Removes the <c>Server</c> header and caps request bodies at <see cref="MaxRequestBodyBytes"/> (413 beyond it).</summary>
    public static IWebHostBuilder ConfigureSakerKestrel(this IWebHostBuilder webHost)
    {
        ArgumentNullException.ThrowIfNull(webHost);

        return webHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });
    }

    /// <summary>
    /// Adds the front of the request pipeline: forwarded headers, HSTS (outside Development), security headers, the correlation id
    /// (so errors carry it), then the exception handler and status-code pages. Call it first, before routing and authentication.
    /// </summary>
    public static IApplicationBuilder UseSakerWeb(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Forwarded headers first, so the scheme and client address everything below sees are the real ones.
        app.UseForwardedHeaders();
        if (!app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<IdempotencyReplayMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>Applies CORS. Call it after <c>UseRouting</c> and before authentication; with no configured origins nothing is allowed.</summary>
    public static IApplicationBuilder UseSakerCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseCors();
        return app;
    }

    /// <summary>
    /// Adds the per-caller rate limiter. It must run after <c>UseRouting</c> (so per-endpoint policies and
    /// <c>DisableRateLimiting</c> are honored) and after authentication (so callers are partitioned by user, not by IP).
    /// Requests refused earlier (invalid or missing token) never reach it; <c>UseSakerPreAuthenticationRateLimiting</c>
    /// limits those by client IP before authentication.
    /// </summary>
    public static IApplicationBuilder UseSakerRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRateLimiter();
        return app;
    }
}
