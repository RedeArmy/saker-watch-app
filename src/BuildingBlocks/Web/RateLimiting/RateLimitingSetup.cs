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
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
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

    /// <summary>
    /// Requests per window and client address (an IPv6 client counts by its /64) before authentication. It stops floods of invalid tokens, which would otherwise cost
    /// signature validation each; it is higher than <see cref="PermitLimit"/> because several users may share an address.
    /// </summary>
    [Range(1, 1_000_000)]
    public int PreAuthenticationPermitLimit { get; set; } = 600;

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
    private static readonly Counter<long> _rejected = SakerTelemetry.Meter.CreateCounter<long>(
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

        services.AddSingleton<PreAuthenticationRateLimiter>();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (rejected, _) => WriteRejectionAsync(
                rejected.HttpContext,
                rejected.Lease,
                rejected.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global");
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

    /// <summary>
    /// Adds the limiter that runs before authentication, keyed by client IP only (no token has been looked at yet). Place it after
    /// <c>UseRouting</c> so endpoints that opt out with <c>DisableRateLimiting</c> (health probes) are skipped.
    /// </summary>
    public static IApplicationBuilder UseSakerPreAuthenticationRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is null)
            {
                using RateLimitLease lease = await context.RequestServices.GetRequiredService<PreAuthenticationRateLimiter>()
                    .AcquireAsync(context, context.RequestAborted);
                if (!lease.IsAcquired)
                {
                    await WriteRejectionAsync(context, lease, "pre-authentication");
                    return;
                }
            }

            await next(context);
        });
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
        string? user = context.User.FindFirstValue(SakerClaims.UserId) ?? context.User.FindFirstValue(SakerClaims.Subject) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return user is not null ? $"user:{user}" : $"ip:{ClientAddressKey.For(context.Connection.RemoteIpAddress)}";
    }

    private static async ValueTask WriteRejectionAsync(HttpContext http, RateLimitLease lease, string policy)
    {
        _rejected.Add(1, new KeyValuePair<string, object?>("policy", policy));

        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
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

/// <summary>
/// The key a client address is limited under. An IPv4 address is its own key; an IPv6 address is reduced to its /64 prefix, because
/// one subscriber usually owns a whole /64 and could otherwise rotate through billions of addresses, each with a fresh allowance
/// (and a fresh limiter held in memory). IPv4-mapped IPv6 addresses count as the IPv4 address they carry.
/// </summary>
public static class ClientAddressKey
{
    /// <summary>The limiter key for <paramref name="address"/>; <c>unknown</c> when the connection has none.</summary>
    public static string For(System.Net.IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return $"{Convert.ToHexStringLower(bytes[..8])}::/64";
    }
}

/// <summary>Fixed-window limiter per client IP for requests that have not been authenticated yet.</summary>
public sealed class PreAuthenticationRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    /// <summary>Creates the limiter from the configured limits.</summary>
    public PreAuthenticationRateLimiter(IOptions<RateLimitingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RateLimitingOptions o = options.Value;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
            $"ip:{ClientAddressKey.For(context.Connection.RemoteIpAddress)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = o.PreAuthenticationPermitLimit,
                Window = TimeSpan.FromSeconds(o.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    /// <summary>Takes one permit for the caller's address.</summary>
    public ValueTask<RateLimitLease> AcquireAsync(HttpContext context, CancellationToken cancellationToken) =>
        _limiter.AcquireAsync(context, 1, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}
