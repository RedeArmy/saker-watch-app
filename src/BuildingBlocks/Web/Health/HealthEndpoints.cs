using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace SakerWatch.BuildingBlocks.Web.Health;

/// <summary>Liveness and readiness endpoints (the only anonymous endpoints of the API).</summary>
public static class HealthEndpoints
{
    /// <summary>Tag that marks a health check as part of readiness.</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Maps <c>/health/live</c> (process is up, runs no checks) and <c>/health/ready</c> (runs checks tagged
    /// <see cref="ReadyTag"/>). Responses are status only: no check names or exception text reach the client.
    /// </summary>
    public static IEndpointRouteBuilder MapSakerHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous()
            .DisableRateLimiting();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) })
            .AllowAnonymous()
            .DisableRateLimiting();
        return endpoints;
    }
}
