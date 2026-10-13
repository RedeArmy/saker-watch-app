using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace SakerWatch.BuildingBlocks.Resilience;

/// <summary>Registration of named outbound HTTP clients with the standard Saker Watch policy.</summary>
public static class SakerHttpClientExtensions
{
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
            var retry = new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
            };
            retry.DisableForUnsafeHttpMethods();
            pipeline.AddRetry(retry);
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitFailureRatio,
                MinimumThroughput = options.CircuitMinimumThroughput,
                BreakDuration = options.CircuitBreakDuration,
                SamplingDuration = TimeSpan.FromTicks(options.CircuitBreakDuration.Ticks * 2),
            });
            pipeline.AddTimeout(options.AttemptTimeout);
        });

        return builder;
    }
}
