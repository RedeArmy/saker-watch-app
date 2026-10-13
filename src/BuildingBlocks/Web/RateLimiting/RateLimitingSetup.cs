using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Web.Problems;

namespace SakerWatch.BuildingBlocks.Web.RateLimiting;

/// <summary>Limits per caller (authenticated user, otherwise client IP). Bound from <c>RateLimiting</c>.</summary>
public sealed class RateLimitingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>Requests per window for every endpoint.</summary>
    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 120;

    /// <summary>Requests per window for endpoints using the <see cref="RateLimitPolicies.Expensive"/> policy.</summary>
    [Range(1, 100_000)]
    public int ExpensivePermitLimit { get; set; } = 10;

    /// <summary>Window length in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Names of the rate-limit policies endpoints can opt into.</summary>
public static class RateLimitPolicies
{
    /// <summary>Stricter policy for costly endpoints (for example triggering an analysis).</summary>
    public const string Expensive = "expensive";
}

/// <summary>Rate limiting registration (ADR-0016, security requirements).</summary>
public static class RateLimitingSetup
{
    private static readonly Meter _meter = new("SakerWatch");
    private static readonly Counter<long> _rejected = _meter.CreateCounter<long>(
        "sakerwatch_http_rate_limited_total",
        description: "Requests rejected by the rate limiter.");

    /// <summary>
    /// Adds a global per-caller limiter plus the <see cref="RateLimitPolicies.Expensive"/> policy.
    /// Rejections are 429 ProblemDetails with <c>Retry-After</c>.
    /// </summary>
    public static IServiceCollection AddSakerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRejectionAsync;
        });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>>((limiter, settings) =>
        {
            RateLimitingOptions o = settings.Value;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => Window(o.PermitLimit, o.WindowSeconds)));
            limiter.AddPolicy(RateLimitPolicies.Expensive, context =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => Window(o.ExpensivePermitLimit, o.WindowSeconds)));
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions Window(int permits, int seconds) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromSeconds(seconds),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    // The user id comes only from the validated principal; the client IP is the fallback for anonymous callers.
    private static string PartitionKey(HttpContext context)
    {
        string? user = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return user is not null ? $"user:{user}" : $"ip:{context.Connection.RemoteIpAddress}";
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken _)
    {
        HttpContext http = context.HttpContext;
        _rejected.Add(1, new KeyValuePair<string, object?>("policy", http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global"));

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Detail = "Slow down and retry after the indicated delay.",
                Type = ResultHttpExtensions.ProblemTypeBase + "rate_limited",
            },
        });
    }
}
