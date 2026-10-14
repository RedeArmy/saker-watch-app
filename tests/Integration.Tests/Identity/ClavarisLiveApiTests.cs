using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Identity;

/// <summary>
/// Opt-in end-to-end check with a <b>real</b> Clavaris access token: authentication, just-in-time provisioning, audit and row-level security.
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
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres, live: (authority, audience));

        JsonElement first = await api.WhoAmIAsync(token);
        JsonElement second = await api.WhoAmIAsync(token);

        var userId = first.GetProperty("userId").GetGuid();
        second.GetProperty("userId").GetGuid().Should().Be(userId);
        first.GetProperty("visibleUsers").GetInt32().Should().Be(1, "row-level security shows the caller only their own row");
        (await api.Db.Admin.ScalarAsync<string>("SELECT external_issuer FROM identity.users WHERE id = @id", ("id", userId))).Should().Be(authority);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log WHERE event_type = 'user.provisioned'")).Should().Be(1);
    }
}
