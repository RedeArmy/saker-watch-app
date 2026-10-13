using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Web.Correlation;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Identity;

/// <summary>Just-in-time provisioning through the real authentication pipeline (ADR-0009): the internal user, the audit trail and the row-level-security context.</summary>
[Trait("Category", "Integration")]
public sealed class JitProvisioningTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    [Fact]
    public async Task FirstValidRequest_CreatesTheUser_AndAuditsItOnce()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-1", Email = "eder@example.com", Name = "Eder" });

        JsonElement me = await api.WhoAmIAsync(token);

        var userId = me.GetProperty("userId").GetGuid();
        me.GetProperty("claimUserId").GetString().Should().Be(userId.ToString("D"));
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
        (await api.Db.Admin.ScalarAsync<string>("SELECT external_issuer FROM identity.users WHERE id = @id", ("id", userId))).Should().Be(api.Idp.Issuer);
        (await api.Db.Admin.ScalarAsync<string>("SELECT external_subject FROM identity.users WHERE id = @id", ("id", userId))).Should().Be("account-1");
        (await api.Db.Admin.ScalarAsync<string>("SELECT email FROM identity.users WHERE id = @id", ("id", userId))).Should().Be("eder@example.com");
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log WHERE event_type = 'user.provisioned'")).Should().Be(1);
        (await api.Db.Worker.AsUserScalarAsync<long?>(null, "SELECT platform.verify_audit_chain()")).Should().BeNull();
    }

    [Fact]
    public async Task TheInternalId_IsAUuidV7_NotTheProvidersSubject()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);

        JsonElement me = await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "account-uuid" }));

        var userId = me.GetProperty("userId").GetGuid();
        userId.Version.Should().Be(7);
        me.GetProperty("subject").GetString().Should().Be("account-uuid");
        userId.ToString().Should().NotContain("account-uuid");
    }

    [Fact]
    public async Task TheAuditEntry_NeverContainsTheUserIdEmailOrSubject()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        JsonElement me = await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "account-pii", Email = "private@example.com", Name = "Private Person" }));
        var userId = me.GetProperty("userId").GetGuid();

        string row = (await api.Db.Admin.ScalarAsync<string>("SELECT row_to_json(a)::text FROM platform.audit_log a WHERE event_type = 'user.provisioned'"))!;
        string actor = (await api.Db.Admin.ScalarAsync<string>("SELECT actor_ref FROM platform.audit_log WHERE event_type = 'user.provisioned'"))!;

        row.Should().NotContain(userId.ToString("D")).And.NotContain("private@example.com").And.NotContain("Private Person").And.NotContain("account-pii");
        actor.Should().MatchRegex("^[0-9a-f]{64}$", "the actor is a keyed hash");
    }

    [Fact]
    public async Task SecondRequest_ReusesTheUser_AndDoesNotAuditAgain()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-2" });

        var first = (await api.WhoAmIAsync(token)).GetProperty("userId").GetGuid();
        var second = (await api.WhoAmIAsync(token)).GetProperty("userId").GetGuid();

        second.Should().Be(first);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log")).Should().Be(1);
    }

    [Fact]
    public async Task ParallelFirstRequests_CreateOneUserAndOneAuditEntry()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-race" });

        JsonElement[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => api.WhoAmIAsync(token)));

        results.Select(r => r.GetProperty("userId").GetGuid()).Distinct().Should().HaveCount(1);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log WHERE event_type = 'user.provisioned'")).Should().Be(1);
        (await api.Db.Worker.AsUserScalarAsync<long?>(null, "SELECT platform.verify_audit_chain()")).Should().BeNull();
    }

    [Fact]
    public async Task EachUser_SeesOnlyTheirOwnRow_BecauseTheRequestRunsWithTheirRlsContext()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);

        JsonElement alice = await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "alice" }));
        JsonElement bob = await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "bob" }));

        alice.GetProperty("userId").GetGuid().Should().NotBe(bob.GetProperty("userId").GetGuid());
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(2);
        alice.GetProperty("visibleUsers").GetInt32().Should().Be(1);
        bob.GetProperty("visibleUsers").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task TheSameSubjectFromAnotherOrganization_IsADifferentUser()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        await api.Db.Admin.ExecuteAsync(
            "INSERT INTO identity.users (id, external_issuer, external_subject, locale, status, created_at, updated_at) VALUES (@id, 'https://other.example/o/org-9', 'account-same', 'es-GT', 'active', now(), now())",
            ("id", Guid.CreateVersion7()));

        JsonElement me = await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "account-same" }));

        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(2);
        me.GetProperty("visibleUsers").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ProfileData_IsRefreshedForAnActiveAccount_WhenTheMappingIsResolvedAgain()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        var existing = Guid.CreateVersion7();
        await api.Db.Admin.ExecuteAsync(
            "INSERT INTO identity.users (id, external_issuer, external_subject, email, locale, status, created_at, updated_at) VALUES (@id, @iss, 'account-old', 'old@example.com', 'es-GT', 'active', now(), now())",
            ("id", existing), ("iss", api.Idp.Issuer));

        var userId = (await api.WhoAmIAsync(api.Idp.Mint(new TokenSpec { Subject = "account-old", Email = "new@example.com" }))).GetProperty("userId").GetGuid();

        userId.Should().Be(existing);
        (await api.Db.Admin.ScalarAsync<string>("SELECT email FROM identity.users WHERE id = @id", ("id", existing))).Should().Be("new@example.com");
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log")).Should().Be(0, "an existing account is not audited as provisioned");
    }

    [Theory]
    [InlineData("pending_deletion")]
    [InlineData("deleted")]
    public async Task AnInactiveAccount_IsRefusedWith403_AndItsProfileIsNotTouched(string status)
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        await api.Db.Admin.ExecuteAsync(
            "INSERT INTO identity.users (id, external_issuer, external_subject, email, locale, status, created_at, updated_at) VALUES (@id, @iss, 'account-gone', 'keep@example.com', 'es-GT', @status, now(), now())",
            ("id", Guid.CreateVersion7()), ("iss", api.Idp.Issuer), ("status", status));

        HttpResponseMessage response = await api.Client.SendAsync(ApiTestHost.Get("/whoami", api.Idp.Mint(new TokenSpec { Subject = "account-gone", Email = "changed@example.com" })));
        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("code").GetString().Should().Be("identity.account_inactive");
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        (await api.Db.Admin.ScalarAsync<string>("SELECT email FROM identity.users")).Should().Be("keep@example.com");
    }

    [Fact]
    public async Task RequestsWithoutAValidToken_CreateNothing()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        string[] tokens =
        [
            api.Idp.Mint(new TokenSpec { ExpiresIn = TimeSpan.FromMinutes(-10) }),
            api.Idp.Mint(new TokenSpec { Audience = "someone-else" }),
            api.Idp.Mint(new TokenSpec { TamperAfterSigning = true }),
            api.Idp.Mint(new TokenSpec { Algorithm = "none" }),
            "garbage",
        ];

        foreach (string token in tokens)
        {
            (await api.Client.SendAsync(ApiTestHost.Get("/whoami", token))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await api.Client.SendAsync(ApiTestHost.Get("/whoami", null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(0);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log")).Should().Be(0);
    }

    [Fact]
    public async Task Unauthorized_IsAProblemWithBearerChallengeAndNoReasonLeak()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);

        HttpResponseMessage anonymous = await api.Client.SendAsync(ApiTestHost.Get("/whoami", null));
        HttpResponseMessage expired = await api.Client.SendAsync(ApiTestHost.Get("/whoami", api.Idp.Mint(new TokenSpec { ExpiresIn = TimeSpan.FromMinutes(-10) })));
        string expiredBody = await expired.Content.ReadAsStringAsync();

        anonymous.Headers.WwwAuthenticate.ToString().Should().Be("Bearer realm=\"saker-watch\"");
        expired.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"invalid_token\"");
        expired.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        expiredBody.Should().NotContainEquivalentOf("expired").And.NotContainEquivalentOf("signature");
        expired.Headers.Contains(CorrelationIdMiddleware.HeaderName).Should().BeTrue();
    }

    [Fact]
    public async Task AnonymousEndpoints_NeedNoToken()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);

        (await api.Client.SendAsync(ApiTestHost.Get("/public", null))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ANonBearerAuthorizationHeader_IsTreatedAsNoCredential()
    {
        await using ApiTestHost api = await ApiTestHost.StartAsync(postgres);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic dXNlcjpwYXNz");

        HttpResponseMessage response = await api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer realm=\"saker-watch\"");
    }
}
