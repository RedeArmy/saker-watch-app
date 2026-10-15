using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Observability.Logging;
using Serilog;
using Serilog.Events;

namespace SakerWatch.Observability.Tests;

public sealed class RedactionTests
{
    private const string _jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJlMTIz";

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("ClientSecret")]
    [InlineData("access_token")]
    [InlineData("RefreshToken")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("ApiKey")]
    [InlineData("ConnectionString")]
    [InlineData("Email")]
    [InlineData("DisplayName")]
    [InlineData("Prompt")]
    [InlineData("RawResponse")]
    [InlineData("RequestBody")]
    public void IsSensitiveName_RecognizesSecretsAndPersonalData(string name)
    {
        RedactionEnricher.IsSensitiveName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("UserId")]
    [InlineData("UseCase")]
    [InlineData("StatusCode")]
    [InlineData("Ticker")]
    [InlineData("CorrelationId")]
    public void IsSensitiveName_LeavesOrdinaryPropertiesAlone(string name)
    {
        RedactionEnricher.IsSensitiveName(name).Should().BeFalse();
    }

    [Fact]
    public void SensitiveProperty_IsMasked()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Information("Login for {Email} with {Password}", "eder@example.com", "hunter2");

        string text = sink.RenderedText();
        text.Should().NotContain("eder@example.com").And.NotContain("hunter2");
        sink.Events.Single().Properties["Email"].ToString().Should().Contain(RedactionEnricher.Mask);
    }

    [Fact]
    public void SensitiveMembersOfDestructuredObjects_AreMasked()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Information("Request {@Request}", new { Ticker = "NVDA", ApiKey = "sk-live-123", Nested = new { RefreshToken = "r-456" } });

        string text = sink.RenderedText();
        text.Should().Contain("NVDA").And.NotContain("sk-live-123").And.NotContain("r-456");
    }

    [Fact]
    public void JwtAndBearerValues_AreScrubbedFromOrdinaryStrings()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Warning("Rejected header {Header} and token {Raw}", "Bearer abcdef123456789", _jwt);

        string text = sink.RenderedText();
        text.Should().NotContain("abcdef123456789").And.NotContain("eyJhbGci").And.Contain(RedactionEnricher.Mask);
    }

    [Fact]
    public void SecretPairsInsideAnExceptionMessage_AreScrubbedFromOrdinaryStrings()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Warning("Database said {Reason}", "could not connect: Host=db;Username=app;Password=hunter2;Database=saker");

        string text = sink.RenderedText();
        text.Should().NotContain("hunter2").And.Contain("Host=db");
    }

    [Fact]
    public void SequencesAndDictionaries_AreRedactedRecursively()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Information(
            "Batch {@Items} {@Map}",
            new[] { new { Password = "p1", Name = "ok" } },
            new Dictionary<string, string> { ["authorization"] = "Bearer zzzzzzzzzz", ["route"] = "/v1/me" });

        string text = sink.RenderedText();
        text.Should().NotContain("p1").And.NotContain("zzzzzzzzzz").And.Contain("/v1/me").And.Contain("ok");
    }

    [Fact]
    public void OrdinaryValues_PassThroughUntouched()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Information("Quote {Ticker} at {Price} for {UserId}", "NVDA", 123.45m, Guid.Empty);

        LogEvent logged = sink.Events.Single();
        logged.Properties["Ticker"].ToString().Should().Be("\"NVDA\"");
        logged.Properties["Price"].ToString().Should().Be("123.45");
    }

    [Fact]
    public void Redaction_AlsoCoversPropertiesAddedByOtherEnrichers()
    {
        var sink = new CollectingSink();
        // Enrichers registered before the defaults run before redaction, which is the supported order.
        Serilog.Core.Logger logger = new LoggerConfiguration()
            .Enrich.WithProperty("LeakedToken", "should-not-appear")
            .ApplySakerDefaults("svc", writeToConsole: false)
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("hello");

        sink.RenderedText().Should().NotContain("should-not-appear");
    }
}
