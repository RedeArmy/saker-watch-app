using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary><c>GET /v1/me</c> through the real API host: authentication, just-in-time provisioning, row-level security and the contract.</summary>
[Trait("Category", "Integration")]
public sealed class MeEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Anonymous_Gets401WithAProblemBody()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();

        HttpResponseMessage response = await client.SendAsync(ApiTestHost.Get("/v1/me", null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be("auth.unauthorized");
    }

    [Fact]
    public async Task ValidToken_ReturnsTheProfile_AndCreatesTheUserOnce()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-me", Name = "Eder Prueba", Email = null, Locale = null });

        HttpResponseMessage first = await client.SendAsync(ApiTestHost.Get("/v1/me", token));
        HttpResponseMessage second = await client.SendAsync(ApiTestHost.Get("/v1/me", token));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement me = await BodyAsync(first);
        me.GetProperty("displayName").GetString().Should().Be("Eder Prueba");
        me.GetProperty("email").ValueKind.Should().Be(JsonValueKind.Null, "Clavaris does not put the email on the access token (A-022)");
        me.GetProperty("locale").GetString().Should().Be("es-GT");
        me.GetProperty("id").GetGuid().Version.Should().Be(7);
        (await BodyAsync(second)).GetProperty("id").GetGuid().Should().Be(me.GetProperty("id").GetGuid());
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
    }

    [Fact]
    public async Task EachCaller_SeesOnlyTheirOwnProfile()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();

        JsonElement a = await BodyAsync(await client.SendAsync(ApiTestHost.Get("/v1/me", api.Idp.Mint(new TokenSpec { Subject = "account-a", Name = "A" }))));
        JsonElement b = await BodyAsync(await client.SendAsync(ApiTestHost.Get("/v1/me", api.Idp.Mint(new TokenSpec { Subject = "account-b", Name = "B" }))));

        a.GetProperty("id").GetGuid().Should().NotBe(b.GetProperty("id").GetGuid());
        a.GetProperty("displayName").GetString().Should().Be("A");
        b.GetProperty("displayName").GetString().Should().Be("B");
    }

    [Fact]
    public async Task AccountPendingDeletion_Gets403_FromAHostThatHasNotCachedIt()
    {
        await using RealApiHost first = await RealApiHost.StartAsync(postgres);
        string token = first.Idp.Mint(new TokenSpec { Subject = "account-leaving" });
        using HttpClient firstClient = first.CreateClient();
        (await firstClient.SendAsync(ApiTestHost.Get("/v1/me", token))).StatusCode.Should().Be(HttpStatusCode.OK);
        await first.Db.Admin.ExecuteAsync("UPDATE identity.users SET status = 'pending_deletion'");

        // The user mapping is cached per process for five minutes (ResolvedUserCache), so a new process shows the real state.
        await using RealApiHost second = await RealApiHost.StartAsync(postgres, first);
        using HttpClient secondClient = second.CreateClient();
        HttpResponseMessage response = await secondClient.SendAsync(ApiTestHost.Get("/v1/me", token));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be("identity.account_inactive");
    }

    [Fact]
    public async Task OpenApiDocument_IsServedAnonymously_AndNamesTheVersionedPath()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"/v1/me\"");
    }

    [Fact]
    public async Task UnversionedAndUnknownVersions_AreNotServed()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-v" });

        (await client.SendAsync(ApiTestHost.Get("/me", token))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.SendAsync(ApiTestHost.Get("/v2/me", token))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
