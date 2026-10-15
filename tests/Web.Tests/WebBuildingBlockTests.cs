using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Web.Correlation;
using SakerWatch.BuildingBlocks.Web.Problems;

namespace SakerWatch.Web.Tests;

public sealed class WebBuildingBlockTests
{
    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Request_WithoutCorrelationId_GetsOneGeneratedAndEchoed()
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/ok");

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Request_WithWellFormedCorrelationId_KeepsIt()
    {
        using var host = await WebTestServer.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "client-abc_123.4");

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().Should().Be("client-abc_123.4");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("inject\"quote")]
    [InlineData("semi;colon")]
    public async Task Request_WithUnsafeCorrelationId_IsReplaced(string incoming)
    {
        using var host = await WebTestServer.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, incoming);

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().Should().NotBe(incoming).And.MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Request_WithOverlongCorrelationId_IsReplaced()
    {
        using var host = await WebTestServer.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, new string('a', 65));

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().Should().HaveLength(32);
    }

    [Fact]
    public async Task UnhandledException_Returns500ProblemWithoutLeakingDetails()
    {
        using var host = await WebTestServer.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "trace-me");

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body.Should().NotContain("secret internal detail").And.NotContain("InvalidOperationException").And.NotContain("at SakerWatch");
        JsonElement json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("correlationId").GetString().Should().Be("trace-me");
        json.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        json.GetProperty("type").GetString().Should().Be("urn:sakerwatch:error:internal_error");
    }

    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Unprocessable, 422)]
    [InlineData(ErrorType.Failure, 500)]
    public async Task ErrorTypes_MapToExpectedStatusAndProblemShape(ErrorType type, int expectedStatus)
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"/fail/{type}");
        JsonElement json = await ReadJson(response);

        ((int)response.StatusCode).Should().Be(expectedStatus);
        ResultHttpExtensions.StatusFor(type).Should().Be(expectedStatus);
        json.GetProperty("code").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        json.TryGetProperty("traceId", out _).Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task ValidationError_IncludesFieldErrors()
    {
        using var host = await WebTestServer.StartAsync();

        JsonElement json = await ReadJson(await host.GetTestClient().GetAsync("/fail/Validation"));

        json.GetProperty("errors").GetProperty("name")[0].GetString().Should().Be("required");
    }

    [Fact]
    public async Task UnexpectedFailure_DoesNotExposeItsMessage()
    {
        using var host = await WebTestServer.StartAsync();

        string body = await (await host.GetTestClient().GetAsync("/fail/Failure")).Content.ReadAsStringAsync();

        body.Should().NotContain("Something");
    }

    [Fact]
    public async Task ResultMapping_Success_ProducesOkAndNoContent()
    {
        using var host = await WebTestServer.StartAsync();
        HttpClient client = host.GetTestClient();

        (await client.GetAsync("/result/ok")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync("/result/none")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task NotFound_UsesProblemDetailsWithCorrelationId()
    {
        using var host = await WebTestServer.StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/does-not-exist");
        JsonElement json = await ReadJson(response);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        json.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RateLimit_ExceedingPermits_Returns429ProblemWithRetryAfter()
    {
        using var host = await WebTestServer.StartAsync(new() { ["RateLimiting:PermitLimit"] = "2", ["RateLimiting:WindowSeconds"] = "60" });
        HttpClient client = host.GetTestClient();

        (await client.GetAsync("/ok")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/ok")).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage limited = await client.GetAsync("/ok");
        JsonElement json = await ReadJson(limited);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        json.GetProperty("type").GetString().Should().Be("urn:sakerwatch:error:rate_limited");
        json.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PreAuthenticationLimit_StopsAFloodBeforeAnyTokenIsLooked_AndSparesTheHealthProbes()
    {
        using var host = await WebTestServer.StartAsync(new() { ["RateLimiting:PreAuthenticationPermitLimit"] = "3" });
        HttpClient client = host.GetTestClient();

        for (int i = 0; i < 3; i++)
        {
            (await client.GetAsync("/ok")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        HttpResponseMessage limited = await client.GetAsync("/ok");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadJson(limited)).GetProperty("type").GetString().Should().Be("urn:sakerwatch:error:rate_limited");
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RateLimit_IsPerUser_SoOneUserCannotExhaustAnothers()
    {
        using var host = await WebTestServer.StartAsync(new() { ["RateLimiting:PermitLimit"] = "1" });
        HttpClient client = host.GetTestClient();

        async Task<HttpStatusCode> CallAs(string user)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
            request.Headers.Add(WebTestServer.TestUserHeader, user);
            return (await client.SendAsync(request)).StatusCode;
        }

        (await CallAs("alice")).Should().Be(HttpStatusCode.OK);
        (await CallAs("alice")).Should().Be(HttpStatusCode.TooManyRequests);
        (await CallAs("bob")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpensivePolicy_IsStricterThanTheGlobalLimit()
    {
        using var host = await WebTestServer.StartAsync(new() { ["RateLimiting:PermitLimit"] = "50", ["RateLimiting:ExpensivePermitLimit"] = "1" });
        HttpClient client = host.GetTestClient();

        (await client.GetAsync("/expensive")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/expensive")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await client.GetAsync("/ok")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthEndpoints_AreNeverRateLimited()
    {
        using var host = await WebTestServer.StartAsync(new() { ["RateLimiting:PermitLimit"] = "1" });
        HttpClient client = host.GetTestClient();

        for (int i = 0; i < 5; i++)
        {
            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Readiness_WhenACheckFails_Returns503WithoutDetailsAndLivenessStaysUp()
    {
        using var host = await WebTestServer.StartAsync(readyFails: true);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        string body = await ready.Content.ReadAsStringAsync();

        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Be("Unhealthy");
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_WhenHealthy_Returns200()
    {
        using var host = await WebTestServer.StartAsync();

        (await host.GetTestClient().GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InvalidRateLimitSettings_FailFastAtStartup()
    {
        Func<Task> start = async () => (await WebTestServer.StartAsync(new() { ["RateLimiting:PermitLimit"] = "0" })).Dispose();

        await start.Should().ThrowAsync<Exception>().WithMessage("*PermitLimit*");
    }
}
