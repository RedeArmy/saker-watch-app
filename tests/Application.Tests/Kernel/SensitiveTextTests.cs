using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class SensitiveTextTests
{
    [Theory]
    [InlineData("Host=db;Username=app;Password=hunter2;Database=saker", "hunter2")]
    [InlineData("failed with pwd: 'p@ss word' for app", "p@ss word")]
    [InlineData("client_secret=placeholder-value rejected", "placeholder-value")]
    [InlineData("{\"api_key\": \"sk-live-123456\"}", "sk-live-123456")]
    [InlineData("Authorization: Bearer abcdef123456789 refused", "abcdef123456789")]
    [InlineData("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJlMTIz rejected", "eyJhbGci")]
    public void Credentials_AreMasked_WhateverTheShape(string text, string secret)
    {
        string scrubbed = SensitiveText.Scrub(text);

        scrubbed.Should().NotContain(secret).And.Contain(SensitiveText.Mask);
    }

    [Fact]
    public void OrdinaryText_IsReturnedAsTheSameInstance()
    {
        const string text = "Quote for NVDA at 123.45 failed after 3 attempts to /v1/watchlist";

        SensitiveText.Scrub(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Emails_AreMaskedOnlyWhenAsked()
    {
        const string text = "duplicate key for eder@example.com in users";

        SensitiveText.Scrub(text).Should().Be(text);
        SensitiveText.Scrub(text, includeEmails: true).Should().NotContain("eder@example.com").And.Contain("users");
    }

    [Fact]
    public void NamesThatMerelyContainASecretWord_AreNotMaskedWithoutAValue()
    {
        SensitiveText.Scrub("the token budget was exceeded for this request").Should().Be("the token budget was exceeded for this request");
    }
}
