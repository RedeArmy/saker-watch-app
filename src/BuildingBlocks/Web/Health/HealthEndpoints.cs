using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SakerWatch.BuildingBlocks.Web.Health;

/// <summary>Readiness settings, bound from the <c>Health</c> section.</summary>
public sealed class HealthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Health";

    /// <summary>
    /// How long a readiness result is reused. Readiness is anonymous and exempt from rate limiting, so without this every probe
    /// (or attacker) would run a query against each datastore. 0 turns the cache off.
    /// </summary>
    [Range(0, 60)]
    public int ReadinessCacheSeconds { get; set; } = 5;

    /// <summary>The longest a readiness run may take before it is reported unhealthy.</summary>
    [Range(1, 120)]
    public int ReadinessTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Runs the readiness checks at most once per <see cref="HealthOptions.ReadinessCacheSeconds"/>, however many requests ask. Concurrent
/// callers share the run in progress, so a burst of probes costs one query per datastore, not one each.
/// </summary>
/// <param name="health">The registered health checks.</param>
/// <param name="options">Cache and timeout settings.</param>
/// <param name="timeProvider">Injected clock.</param>
public sealed class ReadinessCache(HealthCheckService health, IOptions<HealthOptions> options, TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private Task<HealthStatus>? _current;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>The overall readiness status, from the cache while it is fresh.</summary>
    public Task<HealthStatus> GetAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<HealthStatus>? started = null;
        Task<HealthStatus> shared;
        lock (_gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (_current is { } running && (now < _expiresAt || !running.IsCompleted))
            {
                shared = running;
            }
            else
            {
                started = new TaskCompletionSource<HealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = _current = started.Task;
                _expiresAt = DateTimeOffset.MaxValue; // in flight: the window starts when the run ends, see RunAsync
            }
        }

        if (started is not null)
        {
            _ = RunAsync(started);
        }

        return shared.WaitAsync(cancellationToken);
    }

    // The run belongs to everyone waiting for it, so one caller going away must not cancel it. The cache window starts when the run
    // ends: a check that takes longer than the window would otherwise be born expired and every probe would wait for a fresh run.
    private async Task RunAsync(TaskCompletionSource<HealthStatus> result)
    {
        HealthStatus status;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.ReadinessTimeoutSeconds), timeProvider);
        try
        {
            HealthReport report = await health.CheckHealthAsync(check => check.Tags.Contains(HealthEndpoints.ReadyTag), timeout.Token);
            status = report.Status;
        }
        catch (Exception)
#pragma warning disable S2221 // Whatever goes wrong, everyone waiting on this run (and the cache) must get an answer, not hang forever.
        {
            status = HealthStatus.Unhealthy;
        }
#pragma warning restore S2221

        lock (_gate)
        {
            _expiresAt = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(options.Value.ReadinessCacheSeconds);
        }

        result.SetResult(status);
    }
}

/// <summary>Liveness and readiness endpoints (the only anonymous endpoints of the API).</summary>
public static class HealthEndpoints
{
    /// <summary>Tag that marks a health check as part of readiness.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Registers the readiness cache and its settings.</summary>
    public static IServiceCollection AddSakerReadiness(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HealthOptions>().BindConfiguration(HealthOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ReadinessCache>();
        return services;
    }

    /// <summary>
    /// Maps <c>/health/live</c> (process is up, runs no checks) and <c>/health/ready</c> (the result of the checks tagged
    /// <see cref="ReadyTag"/>, cached briefly). Responses are status only: no check names or exception text reach the client.
    /// </summary>
    public static IEndpointRouteBuilder MapSakerHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous()
            .DisableRateLimiting();
        endpoints.MapGet("/health/ready", async (ReadinessCache readiness, CancellationToken cancellationToken) =>
            {
                HealthStatus status = await readiness.GetAsync(cancellationToken);
                return Results.Text(status.ToString(), statusCode: status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
            })
            .ExcludeFromDescription()
            .AllowAnonymous()
            .DisableRateLimiting();
        return endpoints;
    }
}
