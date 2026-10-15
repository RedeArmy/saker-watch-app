using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Identity;

/// <summary>
/// Opt-in end-to-end check with a <b>real</b> Clavaris access token through the real <c>Host.Api</c>: authentication, just-in-time provisioning,
/// <c>GET /v1/me</c>, the idempotent <c>PUT /v1/me/preferences</c>, audit and row-level security.
/// Needs <c>CLAVARIS_LIVE_AUTHORITY</c>, <c>CLAVARIS_LIVE_AUDIENCE</c> and <c>CLAVARIS_LIVE_TOKEN_FILE</c>; otherwise it does nothing.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "ClavarisLive")]
public sealed class ClavarisLiveApiTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    [Fact]
    public async Task RealClavarisToken_ProvisionsTheUser_AndIsAuditedOnce()
    {
        string? authority = Environment.GetEnvironmentVariable("CLAVARIS_LIVE_AUTHORITY");
        string? audience = Environment.GetEnvironmentVariable("CLAVARIS_LIVE_AUDIENCE");
        string? tokenFile = Environment.GetEnvironmentVariable("CLAVARIS_LIVE_TOKEN_FILE");
        if (string.IsNullOrEmpty(authority) || string.IsNullOrEmpty(audience) || string.IsNullOrEmpty(tokenFile))
        {
            return;
        }

        string token = JsonDocument.Parse(await File.ReadAllTextAsync(tokenFile)).RootElement.GetProperty("access_token").GetString()!;
        await using Api.RealApiHost api = await Api.RealApiHost.StartLiveAsync(postgres, authority, audience);
        using HttpClient client = api.CreateClient();

        HttpResponseMessage firstResponse = await client.SendAsync(ApiTestHost.Get("/v1/me", token));
        HttpResponseMessage secondResponse = await client.SendAsync(ApiTestHost.Get("/v1/me", token));
        HttpRequestMessage update = ApiTestHost.Get("/v1/me/preferences", token);
        update.Method = HttpMethod.Put;
        update.Content = System.Net.Http.Json.JsonContent.Create(new { locale = "en-US" });
        update.Headers.Add("Idempotency-Key", "live-check-0000000001");
        HttpResponseMessage updated = await client.SendAsync(update);

        firstResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        JsonElement first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync()).RootElement;
        JsonElement second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync()).RootElement;
        var userId = first.GetProperty("id").GetGuid();
        userId.Version.Should().Be(7);
        second.GetProperty("id").GetGuid().Should().Be(userId);
        first.GetProperty("locale").GetString().Should().Be("es-GT");
        updated.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("locale").GetString().Should().Be("en-US");
        firstResponse.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle();
        (await api.Db.Admin.ScalarAsync<string>("SELECT external_issuer FROM identity.users WHERE id = @id", ("id", userId))).Should().Be(authority);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log WHERE event_type = 'user.provisioned'")).Should().Be(1);
    }
}
