using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using SakerWatch.BuildingBlocks.Web.Http;

namespace SakerWatch.Web.Tests;

public sealed class ConditionalResultsTests
{
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task TheAnswer_CarriesAnETag_AndIsPrivateAndMustBeRevalidated()
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await GetAsync(client, "/etag/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.IsWeak.Should().BeFalse();
        response.Headers.ETag.Tag.Should().MatchRegex("^\"[0-9a-f]{32}\"$");
        response.Headers.CacheControl!.ToString().Should().Be("no-cache, private");
        response.Headers.Vary.Should().Contain("Authorization");
        response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"version\":1,\"amount\":\"12.5\",\"items\":[\"a\",\"b\"]}");
        response.Content.Headers.ContentLength.Should().Be("{\"version\":1,\"amount\":\"12.5\",\"items\":[\"a\",\"b\"]}".Length);
    }

    [Fact]
    public async Task TheSameBody_HasTheSameTag_AndADifferentBodyDoesNot()
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();

        EntityTagHeaderValue first = (await GetAsync(client, "/etag/1")).Headers.ETag!;
        EntityTagHeaderValue again = (await GetAsync(client, "/etag/1")).Headers.ETag!;
        EntityTagHeaderValue other = (await GetAsync(client, "/etag/2")).Headers.ETag!;

        again.Tag.Should().Be(first.Tag);
        other.Tag.Should().NotBe(first.Tag);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("weak")]
    [InlineData("list")]
    [InlineData("star")]
    public async Task AMatchingIfNoneMatch_Gets304_WithTheTagAndNoBody(string form)
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();
        string tag = (await GetAsync(client, "/etag/1")).Headers.ETag!.Tag;
        string header = form switch
        {
            "exact" => tag,
            "weak" => "W/" + tag,
            "list" => "\"nothing\", " + tag + " , \"other\"",
            _ => "*",
        };

        HttpResponseMessage response = await GetAsync(client, "/etag/1", header);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        response.Headers.ETag!.Tag.Should().Be(tag);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"not-it\"")]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task ATagThatDoesNotMatch_GetsTheBodyAgain(string header)
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await GetAsync(client, "/etag/1", header);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"version\":1");
    }

    [Fact]
    public async Task ATagOfAnotherBody_DoesNotMatch()
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();
        string old = (await GetAsync(client, "/etag/1")).Headers.ETag!.Tag;

        HttpResponseMessage response = await GetAsync(client, "/etag/2", old);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OnlyAGetIsAnsweredWith304()
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();
        using var first = new HttpRequestMessage(HttpMethod.Post, "/etag/post");
        string tag = (await client.SendAsync(first)).Headers.ETag!.Tag;
        using var again = new HttpRequestMessage(HttpMethod.Post, "/etag/post");
        again.Headers.TryAddWithoutValidation("If-None-Match", tag);

        HttpResponseMessage response = await client.SendAsync(again);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null, "\"a\"", false)]
    [InlineData("", "\"a\"", false)]
    [InlineData("\"a\"", "\"a\"", true)]
    [InlineData("W/\"a\"", "\"a\"", true)]
    [InlineData("\"b\",W/\"a\"", "\"a\"", true)]
    [InlineData("*", "\"a\"", true)]
    [InlineData("\"A\"", "\"a\"", false)]
    [InlineData("a", "\"a\"", false)]
    public void Matches_FollowsRfc9110(string? header, string etag, bool expected) =>
        ConditionalResults.Matches(header, etag).Should().Be(expected);

    [Fact]
    public void TheTag_IsTheSameForTheSameBytesAndDiffersForOthers()
    {
        byte[] bytes = [1, 2, 3];

        ConditionalResults.ETagOf(bytes).Should().Be(ConditionalResults.ETagOf([1, 2, 3]));
        ConditionalResults.ETagOf(bytes).Should().NotBe(ConditionalResults.ETagOf([1, 2, 4]));
    }
}
