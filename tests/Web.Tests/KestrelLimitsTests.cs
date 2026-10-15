using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Web;

namespace SakerWatch.Web.Tests;

/// <summary>The real Kestrel limits: bodies over 1 MiB are refused and the <c>Server</c> header is not sent.</summary>
public sealed class KestrelLimitsTests
{
    [Fact]
    public async Task OversizedBody_Gets413_AndNoServerHeaderIsSent()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureSakerKestrel();
        await using WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapPut("/echo", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            return Results.Ok((await reader.ReadToEndAsync()).Length);
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        HttpResponseMessage small = await client.PutAsync("/echo", new StringContent(new string('a', 1000)));
        HttpResponseMessage large = await client.PutAsync("/echo", new StringContent(new string('a', (int)WebSetup.MaxRequestBodyBytes + 1)));

        small.StatusCode.Should().Be(HttpStatusCode.OK);
        small.Headers.Contains("Server").Should().BeFalse();
        large.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }
}
