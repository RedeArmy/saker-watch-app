using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace SakerWatch.BuildingBlocks.Resilience;

/// <summary>Registration of named outbound HTTP clients with the standard Saker Watch policy.</summary>
public static class SakerHttpClientExtensions
{
    /// <summary>The transport every Saker Watch outbound client uses: no redirects, no cookies, connections recycled so DNS changes are noticed.</summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>
    /// Registers a named client with a host allow-list, response size limit and a resilience pipeline
    /// (outermost first: bulkhead, total timeout, retry with exponential backoff and jitter, circuit breaker,
    /// per-attempt timeout). Unsafe methods (POST...) are not retried.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="name">Client name; resolved through <c>IHttpClientFactory</c>.</param>
    /// <param name="baseAddress">Provider base address; its host must be in <c>AllowedHosts</c>.</param>
    /// <param name="configure">Policy for this client.</param>
    public static IHttpClientBuilder AddSakerHttpClient(
        this IServiceCollection services,
        string name,
        Uri baseAddress,
        Action<HttpClientResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HttpClientResilienceOptions>(name)
            .Configure(configure)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        IHttpClientBuilder builder = services.AddHttpClient(name, (provider, client) =>
        {
            HttpClientResilienceOptions options = provider.GetRequiredService<IOptionsMonitor<HttpClientResilienceOptions>>().Get(name);
            client.BaseAddress = baseAddress;
            client.MaxResponseContentBufferSize = options.MaxResponseBytes;
            // The resilience pipeline owns timeouts; keep the client's own above the total budget.
            client.Timeout = options.TotalTimeout + TimeSpan.FromSeconds(5);
        });

        // A redirect would be followed below the allow-list check and could reach a host that is not on it, so none is followed.
        // Pooled connections are recycled so a long-lived client (a singleton holding one HttpClient) still notices DNS changes.
        // Cookies are off: the handler lives as long as the client, so a Set-Cookie from a provider would otherwise be replayed on every
        // later call of that client, whoever triggered it.
        builder.ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler);

        builder.AddHttpMessageHandler(provider =>
        {
            HttpClientResilienceOptions options = provider.GetRequiredService<IOptionsMonitor<HttpClientResilienceOptions>>().Get(name);
            return new AllowedHostsHandler(options.AllowedHosts, options.AllowInsecureHttp);
        });

        builder.AddResilienceHandler($"{name}-resilience", (pipeline, context) =>
        {
            HttpClientResilienceOptions options = context.ServiceProvider.GetRequiredService<IOptionsMonitor<HttpClientResilienceOptions>>().Get(name);

            pipeline.AddConcurrencyLimiter(options.MaxConcurrency, queueLimit: options.MaxConcurrency);
            pipeline.AddTimeout(options.TotalTimeout);
            // Polly rejects zero retries, so "no retries" means no retry strategy at all.
            if (options.MaxRetries > 0)
            {
                var retry = new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = options.RetryBaseDelay,
                    ShouldHandle = args => ValueTask.FromResult(ShouldHandle(args.Outcome, options.RateLimitIsTransient)),
                };
                retry.DisableForUnsafeHttpMethods();
                pipeline.AddRetry(retry);
            }

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitFailureRatio,
                MinimumThroughput = options.CircuitMinimumThroughput,
                BreakDuration = options.CircuitBreakDuration,
                SamplingDuration = TimeSpan.FromTicks(options.CircuitBreakDuration.Ticks * 2),
                ShouldHandle = args => ValueTask.FromResult(ShouldHandle(args.Outcome, options.RateLimitIsTransient)),
            });
            pipeline.AddTimeout(options.AttemptTimeout);
        });

        return builder;
    }

    // The standard transient set (timeouts, 5xx, 408 and 429), minus 429 when the caller tracks the quota itself.
    private static bool ShouldHandle(Outcome<HttpResponseMessage> outcome, bool rateLimitIsTransient) =>
        (rateLimitIsTransient || outcome.Result is not { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
        && HttpClientResiliencePredicates.IsTransient(outcome);
}
