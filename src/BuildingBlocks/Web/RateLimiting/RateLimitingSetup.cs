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
using SakerWatch.BuildingBlocks.Application.Deployment;
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
    /// Requests that authentication refuses (401) per window and client address (an IPv6 client counts by its /64). It stops floods of invalid tokens,
    /// which would otherwise cost signature validation each; requests that are authenticated do not count, so users that share an address do not use it up.
    /// </summary>
    [Range(1, 1_000_000)]
    public int PreAuthenticationPermitLimit { get; set; } = 600;

    /// <summary>Window length in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Where the counts are kept. <see cref="RateLimitBackend.Auto"/> counts in Redis when <c>Deployment:ApiReplicas</c> is more than one (ADR-0048).</summary>
    public RateLimitBackend Backend { get; set; } = RateLimitBackend.Auto;

    /// <summary>True when the counts are kept in Redis with this many API processes: asked for, or chosen because there is more than one.</summary>
    /// <param name="apiInstances">How many API processes there are now (<see cref="IApiInstances.Count"/>).</param>
    public bool UsesRedis(int apiInstances) => Backend == RateLimitBackend.Redis || (Backend == RateLimitBackend.Auto && apiInstances > 1);
}

/// <summary>Where the rate limiters keep their counts.</summary>
public enum RateLimitBackend
{
    /// <summary>Redis when more than one API process runs (<c>Deployment:ApiReplicas</c>), memory otherwise.</summary>
    Auto,

    /// <summary>In the memory of each process: right for a single instance (ADR-0021), and no Redis round trip on each request.</summary>
    Memory,

    /// <summary>In Redis, shared by every API process.</summary>
    Redis,
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

        services.AddSakerDeployment(configuration);
        services.AddSingleton<PreAuthenticationRateLimiter>();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (rejected, _) => WriteRejectionAsync(
                rejected.HttpContext,
                rejected.Lease,
                rejected.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global");
        });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>, IServiceProvider>((limiter, settings, provider) =>
        {
            RateLimitingOptions o = settings.Value;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => RateLimitPartition.Get(PartitionKey(context), key => NewLimiter(provider, o, "global", key, o.PermitLimit)));
            limiter.AddPolicy(RateLimitPolicies.Expensive, context =>
                RateLimitPartition.Get(PartitionKey(context), key => NewLimiter(provider, o, "expensive", key, o.ExpensivePermitLimit)));
        });
        return services;
    }

    /// <summary>
    /// Adds the limiter that runs before authentication, keyed by client IP only (no token has been looked at yet) and charged for each request that
    /// authentication then answers with 401, so authenticated traffic never uses it up. Place it after
    /// <c>UseRouting</c> so endpoints that opt out with <c>DisableRateLimiting</c> (health probes) are skipped.
    /// </summary>
    public static IApplicationBuilder UseSakerPreAuthenticationRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
            {
                await next(context);
                return;
            }

            var limiter = context.RequestServices.GetRequiredService<PreAuthenticationRateLimiter>();
            using (RateLimitLease open = await limiter.PeekAsync(context, context.RequestAborted))
            {
                if (!open.IsAcquired)
                {
                    await WriteRejectionAsync(context, open, "pre-authentication");
                    return;
                }
            }

            await next(context);

            // Only a request that authentication refused is counted: the allowance is for floods of missing or invalid tokens. Counting every
            // request would make all users behind one address (the BFF, or the proxy until ForwardedHeaders is set, A-023) share one bucket.
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                using RateLimitLease counted = await limiter.TakeAsync(context, context.RequestAborted);
            }
        });
    }

    // The limiter of one caller in one scope. Where it counts (the memory of the process, or Redis when several API processes run) is asked for each
    // request, so a second process that shows up is counted with from then on. A host with no Redis counts in memory whatever the setting says.
    internal static RateLimiter NewLimiter(IServiceProvider provider, RateLimitingOptions options, string scope, string partitionKey, int permits)
    {
        var instances = provider.GetRequiredService<IApiInstances>();
        return new CallerRateLimiter(
            provider.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
            () => options.UsesRedis(instances.Count),
            scope,
            partitionKey,
            permits,
            TimeSpan.FromSeconds(options.WindowSeconds),
            provider.GetRequiredService<TimeProvider>());
    }

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

/// <summary>Limiter per client IP for the requests that authentication refuses (a missing or invalid token).</summary>
public sealed class PreAuthenticationRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    /// <summary>Creates the limiter from the configured limits.</summary>
    public PreAuthenticationRateLimiter(IOptions<RateLimitingOptions> options, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);

        RateLimitingOptions o = options.Value;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.Get(
            $"ip:{ClientAddressKey.For(context.Connection.RemoteIpAddress)}",
            key => RateLimitingSetup.NewLimiter(provider, o, "preauth", key, o.PreAuthenticationPermitLimit)));
    }

    /// <summary>Asks whether the caller's address still has allowance, without using any: a lease that is acquired means yes. It answers at once either way.</summary>
    public ValueTask<RateLimitLease> PeekAsync(HttpContext context, CancellationToken cancellationToken) => _limiter.AcquireAsync(context, 0, cancellationToken);

    /// <summary>Takes one permit for the caller's address.</summary>
    public ValueTask<RateLimitLease> TakeAsync(HttpContext context, CancellationToken cancellationToken) => _limiter.AcquireAsync(context, 1, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}
