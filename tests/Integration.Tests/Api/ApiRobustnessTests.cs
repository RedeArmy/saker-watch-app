using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>Hostile and sloppy input to the real API host: it must answer with a 4xx problem, never a 500, and never leak internals.</summary>
[Trait("Category", "Integration")]
public sealed class ApiRobustnessTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static HttpRequestMessage Put(string token, string? body, string contentType = "application/json", string key = "robust-key-0000001")
    {
        HttpRequestMessage request = ApiTestHost.Get("/v1/me/preferences", token);
        request.Method = HttpMethod.Put;
        request.Headers.Add("Idempotency-Key", key);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return request;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"locale\": 123}")]
    [InlineData("{\"locale\": null}")]
    [InlineData("{\"locale\": [\"es-GT\"]}")]
    [InlineData("{\"locale\": \"es-GT\"")]
    [InlineData("not json at all")]
    public async Task MalformedBodies_AreClientErrors_NeverAServerError(string body)
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-fuzz" });

        HttpResponseMessage response = await client.SendAsync(Put(token, body));

        ((int)response.StatusCode).Should().BeInRange(400, 499, $"body '{body}' must not reach a 500");
        string text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("Exception").And.NotContain("   at ", "no stack trace or internal type name reaches the client");
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys")).Should().Be(0, "a rejected request leaves no key behind");
    }

    [Fact]
    public async Task WrongContentType_IsRefused_NotParsed()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-ct" });

        HttpResponseMessage response = await client.SendAsync(Put(token, "{\"locale\":\"en-US\"}", "text/plain"));

        ((int)response.StatusCode).Should().BeInRange(400, 499);
        (await api.Db.Admin.ScalarAsync<string>("SELECT locale FROM identity.users")).Should().Be("es-GT");
    }

    [Fact]
    public async Task ManyFirstRequestsAtOnce_CreateOneUserAndOneAuditEntry()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-race" });

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.SendAsync(ApiTestHost.Get("/v1/me", token))));

        responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.OK);
        JsonElement[] bodies = await Task.WhenAll(responses.Select(async r => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone()));
        bodies.Select(b => b.GetProperty("id").GetGuid()).Distinct().Should().ContainSingle();
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM identity.users")).Should().Be(1);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.audit_log WHERE event_type = 'user.provisioned'")).Should().Be(1);
    }

    [Fact]
    public async Task Preflight_FromAnyOrigin_GetsNoCorsHeaders_WithTheDefaultConfiguration()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/v1/me");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        HttpResponseMessage response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Theory]
    [InlineData("/v1/me")]
    [InlineData("/health/live")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/does-not-exist")]
    public async Task EveryKindOfResponse_CarriesTheSecurityHeaders_AndACorrelationId(string path)
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        response.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task AHostileCorrelationId_IsNotEchoedBack()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        HttpRequestMessage request = ApiTestHost.Get("/v1/me", null);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc <script>alert(1)</script>");

        HttpResponseMessage response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Single().Should().NotContain("<").And.NotContain("script");
    }
}
