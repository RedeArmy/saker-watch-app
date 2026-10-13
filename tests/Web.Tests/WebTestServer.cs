using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Web;
using SakerWatch.BuildingBlocks.Web.Health;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;

namespace SakerWatch.Web.Tests;

/// <summary>An in-memory API wired exactly like <c>Host.Api</c> will be, with a few probe endpoints.</summary>
internal static class WebTestServer
{
    public const string TestUserHeader = "X-Test-User";

    public static async Task<IHost> StartAsync(Dictionary<string, string?>? settings = null, bool readyFails = false)
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings ?? []))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddSakerWeb(context.Configuration);
                    services.AddHealthChecks().AddCheck("db", () => readyFails ? HealthCheckResult.Unhealthy("db down") : HealthCheckResult.Healthy(), tags: [HealthEndpoints.ReadyTag]);
                })
                .Configure(app =>
                {
                    app.UseSakerWeb();
                    // Test stand-in for authentication: a header becomes the validated principal.
                    app.Use(async (http, next) =>
                    {
                        if (http.Request.Headers.TryGetValue(TestUserHeader, out var user))
                        {
                            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test"));
                        }

                        await next();
                    });
                    app.UseRouting();
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
                        endpoints.MapGet("/result/ok", () => Result.Success(5).ToHttpResult(v => Results.Ok(v)));
                        endpoints.MapDelete("/result/none", () => Result.Success().ToHttpResult());
                    });
                }))
            .Build();
        await host.StartAsync();
        return host;
    }
}
