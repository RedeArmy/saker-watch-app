using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Web;

namespace SakerWatch.Web.Tests;

/// <summary>Security headers, trusted forwarded headers, HSTS and CORS of the HTTP front (OWASP ASVS V14, V13).</summary>
public sealed class HttpSecurityTests
{
    private static async Task<IHost> StartAsync(Dictionary<string, string?>? settings = null, string environment = "Production", IPAddress? remote = null)
    {
        IHost host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings ?? []))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddSakerWeb(context.Configuration);
                })
                .Configure(app =>
                {
                    app.Use((http, next) =>
                    {
                        http.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
                        return next(http);
                    });
                    app.UseSakerWeb();
                    app.UseRouting();
                    app.UseSakerCors();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/probe", (HttpContext http) => Results.Ok(new { scheme = http.Request.Scheme, ip = http.Connection.RemoteIpAddress?.ToString(), host = http.Request.Host.Value }));
                        endpoints.MapGet("/cached", (HttpContext http) =>
                        {
                            http.Response.Headers.CacheControl = "private, max-age=60";
                            return Results.Ok();
                        });
                    });
                }))
            .Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task EveryResponse_CarriesTheSecurityHeaders_EvenAnErrorOrAMissingRoute()
    {
        using IHost host = await StartAsync();
        HttpClient client = host.GetTestClient();

        foreach (string path in new[] { "/probe", "/nothing-here" })
        {
            HttpResponseMessage response = await client.GetAsync(path);

            response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
            response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
            response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
            response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Contain("default-src 'none'").And.Contain("frame-ancestors 'none'");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task AnEndpointThatSetsItsOwnCacheControl_IsNotOverridden()
    {
        using IHost host = await StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/cached");

        response.Headers.CacheControl!.NoStore.Should().BeFalse();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_AreIgnored()
    {
        using IHost host = await StartAsync(remote: IPAddress.Parse("203.0.113.9"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add("X-Forwarded-For", "198.51.100.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        string body = await (await host.GetTestClient().SendAsync(request)).Content.ReadAsStringAsync();

        body.Should().Contain("\"ip\":\"203.0.113.9\"").And.Contain("\"scheme\":\"http\"");
    }

    [Fact]
    public async Task ForwardedHeaders_FromAConfiguredProxyNetwork_AreApplied_OneHopDeep()
    {
        using IHost host = await StartAsync(new() { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" }, remote: IPAddress.Parse("10.1.2.3"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6, 198.51.100.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        string body = await (await host.GetTestClient().SendAsync(request)).Content.ReadAsStringAsync();

        body.Should().Contain("\"ip\":\"198.51.100.7\"", "only the hop added by the trusted proxy is believed; earlier entries are client-controlled")
            .And.Contain("\"scheme\":\"https\"");
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0")]
    [InlineData("Cors:AllowedOrigins:0", "*")]
    [InlineData("Cors:AllowedOrigins:0", "http://app.example.com")]
    [InlineData("Cors:AllowedOrigins:0", "https://app.example.com/")]
    [InlineData("Cors:AllowedOrigins:0", "https://app.example.com/path")]
    public async Task MalformedSecurityConfiguration_FailsStartup(string key, string value)
    {
        Func<Task> start = async () => (await StartAsync(new() { [key] = value })).Dispose();

        await start.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Hsts_IsSentOverHttps_InProduction()
    {
        using IHost host = await StartAsync();
        HttpClient client = host.GetTestClient();
        client.BaseAddress = new Uri("https://api.example.com");

        HttpResponseMessage response = await client.GetAsync("/probe");

        response.Headers.GetValues("Strict-Transport-Security").Should().ContainSingle().Which.Should().Be("max-age=31536000; includeSubDomains");
    }

    [Fact]
    public async Task Hsts_IsNotSentOverPlainHttp_NorInDevelopment()
    {
        using IHost production = await StartAsync();
        using IHost development = await StartAsync(environment: "Development");
        HttpClient devClient = development.GetTestClient();
        devClient.BaseAddress = new Uri("https://api.example.com");

        (await production.GetTestClient().GetAsync("/probe")).Headers.Contains("Strict-Transport-Security").Should().BeFalse();
        (await devClient.GetAsync("/probe")).Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/probe");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,idempotency-key,content-type");
        return request;
    }

    [Fact]
    public async Task Cors_IsClosedByDefault()
    {
        using IHost host = await StartAsync();

        HttpResponseMessage response = await host.GetTestClient().SendAsync(Preflight("https://evil.example"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Cors_AllowsOnlyTheListedOrigins_WithoutCredentials()
    {
        using IHost host = await StartAsync(new() { ["Cors:AllowedOrigins:0"] = "https://app.example.com" });
        HttpClient client = host.GetTestClient();

        HttpResponseMessage allowed = await client.SendAsync(Preflight("https://app.example.com"));
        HttpResponseMessage denied = await client.SendAsync(Preflight("https://evil.example"));

        allowed.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("https://app.example.com");
        allowed.Headers.GetValues("Access-Control-Allow-Headers").Single().ToLowerInvariant().Should().Contain("idempotency-key");
        allowed.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        denied.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
