using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Resilience;

namespace SakerWatch.Web.Tests;

/// <summary>The host allow-list must not be bypassable by a redirect from an allowed host (the primary handler must not follow redirects).</summary>
public sealed class OutboundRedirectTests
{
    [Fact]
    public async Task RedirectFromAnAllowedHost_ToAnotherHost_IsNotFollowed()
    {
        int forbiddenHits = 0;
        await using WebApplication app = WebApplication.CreateBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/start", (HttpContext http) => Results.Redirect($"http://localhost:{http.Connection.LocalPort}/secret"));
        app.MapGet("/secret", () =>
        {
            Interlocked.Increment(ref forbiddenHits);
            return Results.Ok("internal");
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var services = new ServiceCollection();
        services.AddSakerHttpClient("provider", new Uri(address), options =>
        {
            options.AllowedHosts = [new Uri(address).Authority];
            options.AllowInsecureHttp = true;
            options.MaxRetries = 0;
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("provider");

        HttpResponseMessage response = await client.GetAsync("start");

        response.StatusCode.Should().Be(HttpStatusCode.Found, "a redirect is returned to the caller, never followed");
        forbiddenHits.Should().Be(0, "the redirect target is not on the allow-list");
    }
}
