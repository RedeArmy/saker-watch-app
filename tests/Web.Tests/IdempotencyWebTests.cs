using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using SakerWatch.BuildingBlocks.Web.Idempotency;

namespace SakerWatch.Web.Tests;

public sealed class IdempotencyWebTests
{
    private const string _exampleId = "0123456789abcdef-key";

    private static HttpRequestMessage Post(string path, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(IdempotencyKeyReader.HeaderName, key);
        }

        return request;
    }

    [Fact]
    public async Task Require_WithAValidKey_ReturnsTheKeyMethodAndRouteTemplate()
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().SendAsync(Post("/idem/42", _exampleId));
        JsonElement json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.GetProperty("key").GetString().Should().Be(_exampleId);
        json.GetProperty("method").GetString().Should().Be("POST");
        json.GetProperty("route").GetString().Should().Be("/idem/{id}", "the route template, not the concrete path, scopes the key");
    }

    [Fact]
    public async Task Require_WithoutTheHeader_Returns400WithAStableCode()
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().SendAsync(Post("/idem", null));
        JsonElement json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("code").GetString().Should().Be("idempotency.key_required");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space in the key 123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public async Task Require_WithAMalformedKey_Returns400(string key)
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().SendAsync(Post("/idem", key));
        JsonElement json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("code").GetString().Should().Be("idempotency.key_invalid");
    }

    [Fact]
    public async Task ReplayedResponses_CarryTheReplayHeader()
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage replayed = await client.GetAsync("/ok?replayed");
        HttpResponseMessage fresh = await client.GetAsync("/ok");

        replayed.Headers.GetValues(IdempotencyKeyReader.ReplayedHeaderName).Should().Equal("true");
        fresh.Headers.Contains(IdempotencyKeyReader.ReplayedHeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task InProgress_Returns409WithRetryAfter()
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/busy");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.GetValues("Retry-After").Should().Equal("1");
    }

    [Fact]
    public async Task KeyReuse_Returns422()
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/reuse");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
