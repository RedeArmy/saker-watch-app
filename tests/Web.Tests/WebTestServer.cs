using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Web;
using SakerWatch.BuildingBlocks.Web.Health;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;

namespace SakerWatch.Web.Tests;

/// <summary>An in-memory API wired exactly like <c>Host.Api</c> will be, with a few probe endpoints.</summary>
internal static class WebTestServer
{
    public const string TestUserHeader = "X-Test-User";

    /// <summary>Sets the connection's remote address, which a real host takes from the socket (or from trusted forwarded headers).</summary>
    public const string TestRemoteIpHeader = "X-Test-Remote-Ip";

    public static async Task<IHost> StartAsync(Dictionary<string, string?>? settings = null, bool readyFails = false, ReadyCheckCounter? counter = null, TimeProvider? clock = null)
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings ?? []))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddSakerWeb(context.Configuration);
                    // Registered after AddSakerWeb so it replaces the system clock.
                    if (clock is not null)
                    {
                        services.AddSingleton(clock);
                    }

                    services.AddHttpContextAccessor();
                    // Stands in for the idempotency behavior: ?replayed marks the response as replayed.
                    services.AddScoped<IIdempotencyContext>(provider => new FakeIdempotencyContext(
                        provider.GetRequiredService<IHttpContextAccessor>().HttpContext!.Request.Query.ContainsKey("replayed")));
                    services.AddHealthChecks().AddCheck("db", () =>
                    {
                        counter?.Increment();
                        return readyFails ? HealthCheckResult.Unhealthy("db down") : HealthCheckResult.Healthy();
                    }, tags: [HealthEndpoints.ReadyTag]);
                })
                .Configure(app =>
                {
                    app.UseSakerWeb();
                    // Test stand-in for authentication: a header becomes the validated principal.
                    app.Use(async (http, next) =>
                    {
                        if (http.Request.Headers.TryGetValue(TestRemoteIpHeader, out var remote))
                        {
                            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remote.ToString());
                        }

                        if (http.Request.Headers.TryGetValue(TestUserHeader, out var user))
                        {
                            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test"));
                        }

                        await next();
                    });
                    app.UseRouting();
                    app.UseSakerPreAuthenticationRateLimiting();
                    app.UseSakerRateLimiting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapSakerHealth();
                        endpoints.MapGet("/ok", () => Results.Ok(new { ok = true }));
                        endpoints.MapGet("/boom", () => { throw new InvalidOperationException("secret internal detail"); });
                        endpoints.MapGet("/expensive", () => Results.Ok()).RequireRateLimiting(RateLimitPolicies.Expensive);
                        endpoints.MapGet("/fail/{type}", (ErrorType type) =>
                            (type == ErrorType.Validation
                                ? Error.Validation("demo.invalid", "Bad input", new Dictionary<string, string[]> { ["name"] = ["required"] })
                                : new Error("demo.error", "Something", type)).ToProblem());
                        endpoints.MapPost("/idem", (HttpContext http) => IdempotencyKeyReader.Require(http).ToHttpResult(key => Results.Ok(key)));
                        endpoints.MapPost("/idem/{id}", (HttpContext http, string id) => IdempotencyKeyReader.Require(http).ToHttpResult(key => Results.Ok(key)));
                        endpoints.MapGet("/busy", () => Error.Conflict(IdempotencyErrors.InProgress, "still running").ToProblem());
                        endpoints.MapGet("/reuse", () => Error.Unprocessable(IdempotencyErrors.KeyReuse, "different request").ToProblem());
                        endpoints.MapGet("/result/ok", () => Result.Success(5).ToHttpResult(v => Results.Ok(v)));
                        endpoints.MapDelete("/result/none", () => Result.Success().ToHttpResult());
                    });
                }))
            .Build();
        await host.StartAsync();
        return host;
    }
}

internal sealed class FakeIdempotencyContext(bool replayed) : IIdempotencyContext
{
    public bool Replayed => replayed;
}

/// <summary>Counts how many times the readiness check really ran.</summary>
internal sealed class ReadyCheckCounter
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public void Increment() => Interlocked.Increment(ref _runs);
}
