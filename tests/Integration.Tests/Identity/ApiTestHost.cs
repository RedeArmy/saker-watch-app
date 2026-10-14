using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Web;
using SakerWatch.BuildingBlocks.Web.Security;
using SakerWatch.Identity.Infrastructure.Clavaris;
using SakerWatch.Identity.Infrastructure.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Identity;

/// <summary>
/// The API pipeline as <c>Host.Api</c> composes it (correlation, authentication with the Clavaris scheme, user resolution,
/// authorization, rate limiting), on an in-memory server with a real PostgreSQL and a fake Clavaris.
/// </summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    public const string PepperBase64 = "q8Zy1p0m2V3x4W5e6R7t8Y9u0I1o2P3a4S5d6F7g8H9=";

    private ApiTestHost(IHost host, FakeIdentityProvider idp, MigratedDatabase db)
    {
        Host = host;
        Idp = idp;
        Db = db;
        Client = host.GetTestClient();
    }

    public IHost Host { get; }

    public FakeIdentityProvider Idp { get; }

    public MigratedDatabase Db { get; }

    public HttpClient Client { get; }

    public static async Task<ApiTestHost> StartAsync(TimescalePostgresFixture postgres, Action<IEndpointRouteBuilder>? mapEndpoints = null, Action<IServiceCollection>? configureServices = null, (string Authority, string Audience)? live = null)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        FakeIdentityProvider idp = await FakeIdentityProvider.StartAsync();

        IHost host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = db.Api,
                    ["Audit:ActorPepper"] = PepperBase64,
                    ["Clavaris:Authority"] = live?.Authority ?? idp.Issuer,
                    ["Clavaris:Audience"] = live?.Audience ?? FakeIdentityProvider.Audience,
                    ["Clavaris:RequireHttpsMetadata"] = "false",
                }))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddSakerWeb(context.Configuration);
                    services.AddApplicationPipeline(typeof(SakerWatch.Identity.Application.IAssemblyMarker).Assembly);
                    services.AddIntegrationEvents();
                    services.AddSakerPostgres(context.Configuration);
                    services.AddSakerDbContext<IdentityDbContext>(IdentityDbContext.Schema);
                    services.AddClavarisAuth(context.Configuration);
                    if (live is null)
                    {
                        services.AddHttpClient(ClavarisAuthProvider.MetadataClientName).ConfigurePrimaryHttpMessageHandler(idp.CreateHandler);
                    }

                    services.AddSakerCurrentUser();
                    services.AddSakerAuthorization();
                    configureServices?.Invoke(services);
                })
                .Configure(app =>
                {
                    app.UseSakerWeb();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseSakerUserResolution();
                    app.UseAuthorization();
                    app.UseSakerRateLimiting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/public", () => Results.Ok(new { ok = true })).AllowAnonymous();
                        endpoints.MapGet("/whoami", async (HttpContext http, IdentityDbContext db, ICurrentUser user) => Results.Ok(new
                        {
                            userId = user.UserId,
                            claimUserId = http.User.FindFirst(SakerClaims.UserId)?.Value,
                            subject = http.User.FindFirst(SakerClaims.Subject)?.Value,
                            visibleUsers = await db.Users.CountAsync(http.RequestAborted),
                        })).RequireAuthorization();
                        mapEndpoints?.Invoke(endpoints);
                    });
                }))
            .Build();
        await host.StartAsync();
        return new ApiTestHost(host, idp, db);
    }

    public static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    public async Task<JsonElement> WhoAmIAsync(string token)
    {
        HttpResponseMessage response = await Client.SendAsync(Get("/whoami", token));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        await Idp.DisposeAsync();
    }
}
