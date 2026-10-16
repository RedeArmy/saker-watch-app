using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary><c>PUT /v1/me/preferences</c>, the example of a mutating endpoint with an <c>Idempotency-Key</c> (ADR-0017), through the real API host.</summary>
[Trait("Category", "Integration")]
public sealed class PreferencesEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _key = "pref-key-0000000001";

    private static HttpRequestMessage Put(string token, string? key, string locale)
    {
        HttpRequestMessage request = ApiTestHost.Get("/v1/me/preferences", token);
        request.Method = HttpMethod.Put;
        request.Content = JsonContent.Create(new { locale });
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task ValidRequest_UpdatesTheLocale_AndTheProfileShowsIt()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-prefs" });

        HttpResponseMessage response = await client.SendAsync(Put(token, _key, "en-US"));
        HttpResponseMessage me = await client.SendAsync(ApiTestHost.Get("/v1/me", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).GetProperty("locale").GetString().Should().Be("en-US");
        (await BodyAsync(me)).GetProperty("locale").GetString().Should().Be("en-US");
        response.Headers.Contains("Idempotent-Replayed").Should().BeFalse("the first attempt is not a replay");
    }

    [Fact]
    public async Task RepeatWithTheSameKeyAndBody_ReplaysTheFirstResponse_WithoutChangingTheRow()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-replay" });
        await client.SendAsync(Put(token, _key, "en-US"));
        await api.Db.Admin.ExecuteAsync("UPDATE identity.users SET locale = 'es-GT'");

        HttpResponseMessage replay = await client.SendAsync(Put(token, _key, "en-US"));

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotent-Replayed").Should().ContainSingle().Which.Should().Be("true");
        (await BodyAsync(replay)).GetProperty("locale").GetString().Should().Be("en-US", "the stored first response is returned");
        (await api.Db.Admin.ScalarAsync<string>("SELECT locale FROM identity.users")).Should().Be("es-GT", "the handler did not run again");
    }

    [Fact]
    public async Task SameKeyWithADifferentBody_Is422()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-reuse" });
        await client.SendAsync(Put(token, _key, "en-US"));

        HttpResponseMessage response = await client.SendAsync(Put(token, _key, "es-GT"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be("idempotency.key_reuse");
    }

    [Fact]
    public async Task ParallelIdenticalRequests_HaveOneEffect()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-parallel" });
        await client.SendAsync(ApiTestHost.Get("/v1/me", token));

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAsync(Put(token, _key, "en-US"))));

        responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.OK || status == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(1);
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys")).Should().Be(1);
        (await api.Db.Admin.ScalarAsync<string>("SELECT locale FROM identity.users")).Should().Be("en-US");
    }

    [Theory]
    [InlineData(null, "idempotency.key_required")]
    [InlineData("short", "idempotency.key_invalid")]
    [InlineData("has spaces in the key!!", "idempotency.key_invalid")]
    public async Task MissingOrMalformedKey_Is400(string? key, string code)
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-nokey" });

        HttpResponseMessage response = await client.SendAsync(Put(token, key, "en-US"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be(code);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("")]
    [InlineData("es-gt")]
    public async Task UnsupportedLocale_Is400_AndLeavesNoKeyBehind(string locale)
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string token = api.Idp.Mint(new TokenSpec { Subject = "account-badlocale" });

        HttpResponseMessage response = await client.SendAsync(Put(token, _key, locale));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonElement errors = (await BodyAsync(response)).GetProperty("errors");
        errors.TryGetProperty("locale", out _).Should().BeTrue("the field is reported under the name the JSON body uses, not the C# property name");
        errors.TryGetProperty("Locale", out _).Should().BeFalse();
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys")).Should().Be(0, "only successes are stored, so the client can correct and retry");
    }

    [Fact]
    public async Task Anonymous_Is401()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        HttpRequestMessage request = Put("x", _key, "en-US");
        request.Headers.Authorization = null;

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheSameKeyFromTwoUsers_IsTwoIndependentRequests()
    {
        await using RealApiHost api = await RealApiHost.StartAsync(postgres);
        using HttpClient client = api.CreateClient();
        string a = api.Idp.Mint(new TokenSpec { Subject = "account-ka" });
        string b = api.Idp.Mint(new TokenSpec { Subject = "account-kb" });

        HttpResponseMessage first = await client.SendAsync(Put(a, _key, "en-US"));
        HttpResponseMessage second = await client.SendAsync(Put(b, _key, "es-GT"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "keys are scoped per user");
        (await BodyAsync(second)).GetProperty("locale").GetString().Should().Be("es-GT");
        (await api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys")).Should().Be(2);
    }
}
