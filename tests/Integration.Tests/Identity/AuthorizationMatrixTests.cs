using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Web.Security;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Identity;

/// <summary>The authorization matrix: who may call what (default-deny, scopes, object ownership), through the real pipeline.</summary>
[Trait("Category", "Integration")]
public sealed class AuthorizationMatrixTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _scopedPolicy = "needs-watchlist-read";
    private const string _readScope = "watchlist:read";

    private sealed record OwnedItem(Guid OwnerId) : IOwnedResource;

    private Task<ApiTestHost> StartAsync() => ApiTestHost.StartAsync(
        postgres,
        endpoints =>
        {
            // Declares no authorization at all: the fallback policy must protect it.
            endpoints.MapGet("/unannotated", () => Results.Ok());
            endpoints.MapGet("/scoped", () => Results.Ok()).RequireAuthorization(_scopedPolicy);
            endpoints.MapGet("/items/{ownerId:guid}", async (Guid ownerId, HttpContext http, IAuthorizationService authorization) =>
            {
                var result = await authorization.RequireOwnerAsync(http.User, new OwnedItem(ownerId));
                return result.IsSuccess ? Results.Ok() : SakerWatch.BuildingBlocks.Web.Problems.ResultHttpExtensions.ToProblem(result.Error!);
            });
        },
        services => services.Configure<AuthorizationOptions>(options => options.AddScopePolicy(_scopedPolicy, _readScope)));

    [Theory]
    [InlineData("/unannotated")]
    [InlineData("/whoami")]
    [InlineData("/scoped")]
    public async Task Anonymous_IsRefusedWith401AndAProblemBody_OnEveryProtectedEndpoint(string path)
    {
        await using ApiTestHost api = await StartAsync();

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get(path, null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer realm=\"saker-watch\"");
        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().Should().Be("auth.unauthorized");
    }

    [Fact]
    public async Task PublicEndpoint_IsOpenToAnonymous()
    {
        await using ApiTestHost api = await StartAsync();

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get("/public", null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InvalidToken_IsRefusedWith401_AndInvalidTokenChallenge()
    {
        await using ApiTestHost api = await StartAsync();

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get("/unannotated", "not-a-jwt"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    [Fact]
    public async Task ValidToken_ReachesAnEndpointThatDeclaresNothing()
    {
        await using ApiTestHost api = await StartAsync();

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get("/unannotated", api.Idp.Mint(new TokenSpec { Subject = "account-a" })));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ScopePolicy_WithoutTheScope_Is403_WithAProblemBody()
    {
        await using ApiTestHost api = await StartAsync();

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get("/scoped", api.Idp.Mint(new TokenSpec { Subject = "account-b" })));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().Should().Be("auth.forbidden");
    }

    [Fact]
    public async Task ScopePolicy_WithTheScope_Succeeds()
    {
        await using ApiTestHost api = await StartAsync();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-c", Scope = $"openid {_readScope}" });

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get("/scoped", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_CanReachTheirResource()
    {
        await using ApiTestHost api = await StartAsync();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-owner" });
        var ownerId = (await api.WhoAmIAsync(token)).GetProperty("userId").GetGuid();

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get($"/items/{ownerId}", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnotherUsersResource_IsReportedAsNotFound_NotForbidden()
    {
        await using ApiTestHost api = await StartAsync();
        var ownerId = (await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "account-victim" }))).GetProperty("userId").GetGuid();
        string attacker = api.Idp.Mint(new TokenSpec { Subject = "account-attacker" });

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get($"/items/{ownerId}", attacker));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a 403 would confirm that the object exists");
    }
}
