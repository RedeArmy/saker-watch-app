using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Identity.Contracts;
using SakerWatch.Identity.Infrastructure.Clavaris;
using SakerWatch.TestSupport;

namespace SakerWatch.Identity.Tests;

/// <summary>JWT validation contract of ADR-0009: what must be accepted, and the negative matrix of what must never be.</summary>
public sealed class ClavarisAuthProviderTests
{
    private static async Task<AuthFailureReason> RejectionOf(AuthHarness harness, TokenSpec spec)
    {
        AuthResult result = await harness.AuthenticateAsync(spec);
        return result.Should().BeOfType<AuthResult.Rejected>().Subject.Reason;
    }

    [Fact]
    public async Task ValidToken_IsAuthenticated_WithTheClaimsMapped()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.AuthenticateAsync(new TokenSpec { Subject = "account-42", Email = "eder@example.com", Name = "Eder", Locale = "es-GT", Scope = "openid profile email" });

        ValidatedIdentity identity = result.Should().BeOfType<AuthResult.Authenticated>().Subject.Identity;
        identity.Issuer.Should().Be(harness.Idp.Issuer);
        identity.Subject.Should().Be("account-42");
        identity.Email.Should().Be("eder@example.com");
        identity.DisplayName.Should().Be("Eder");
        identity.Locale.Should().Be("es-GT");
        identity.Scopes.Should().BeEquivalentTo("openid", "profile", "email");
        identity.ExpiresAt.Should().Be(harness.Clock.GetUtcNow().AddMinutes(5).ToUniversalTime().AddTicks(-(harness.Clock.GetUtcNow().AddMinutes(5).Ticks % TimeSpan.TicksPerSecond)));
        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scope_IsReadFromAJsonArray_AndFromASpaceDelimitedString(bool asArray)
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.AuthenticateAsync(new TokenSpec { Subject = "account-scope", Scope = "openid  watchlist:read watchlist:read", ScopeAsArray = asArray });

        result.Should().BeOfType<AuthResult.Authenticated>().Subject.Identity.Scopes.Should().BeEquivalentTo("openid", "watchlist:read");
    }

    [Fact]
    public async Task IdToken_WithoutScope_IsRefused_EvenWithTheRightSignatureAndAudience()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { Subject = "account-id-token", Scope = null, AsIdToken = true })).Should().Be(AuthFailureReason.NotAnAccessToken);
    }

    [Fact]
    public async Task DelegatedToken_WithAnActClaim_IsRefused()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { Subject = "account-impersonated", Delegated = true })).Should().Be(AuthFailureReason.NotAnAccessToken);
    }

    [Fact]
    public async Task TokenWithoutEmailOrName_IsAuthenticated_BecauseClavarisDoesNotEmitThem()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.AuthenticateAsync(new TokenSpec { Subject = "account-bare", Email = null, Name = null, Locale = null });

        ValidatedIdentity identity = result.Should().BeOfType<AuthResult.Authenticated>().Subject.Identity;
        identity.Email.Should().BeNull();
        identity.DisplayName.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.e30.")]
    public async Task GarbageInput_IsRefused(string token)
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.Provider.AuthenticateAsync(token, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task OversizedToken_IsMalformedWithoutBeingParsed()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.Provider.AuthenticateAsync(new string('a', 9000), CancellationToken.None);

        result.Should().BeOfType<AuthResult.Rejected>().Which.Reason.Should().Be(AuthFailureReason.Malformed);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS384")]
    public async Task AnAlgorithmOtherThanRs256_IsRefusedBeforeAnyKeyIsUsed(string algorithm)
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthFailureReason reason = await RejectionOf(harness, new TokenSpec { Algorithm = algorithm });

        reason.Should().Be(AuthFailureReason.UnsupportedAlgorithm);
        harness.Idp.DiscoveryRequests.Should().Be(0, "the token is refused before the provider is even contacted");
    }

    [Fact]
    public async Task WrongIssuer_IsRefused()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { Issuer = "http://fake-idp.test/o/another-org" })).Should().Be(AuthFailureReason.InvalidIssuer);
        (await RejectionOf(harness, new TokenSpec { Issuer = "http://evil.test/o/org-1" })).Should().Be(AuthFailureReason.InvalidIssuer);
    }

    [Fact]
    public async Task WrongOrMissingAudience_IsRefused()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { Audience = "some-other-api" })).Should().Be(AuthFailureReason.InvalidAudience);
        (await RejectionOf(harness, new TokenSpec { Audience = string.Empty })).Should().Be(AuthFailureReason.InvalidAudience);
    }

    [Fact]
    public async Task ExpiredToken_IsRefused_ButWithinTheSkewItIsAccepted()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { ExpiresIn = TimeSpan.FromMinutes(-2) })).Should().Be(AuthFailureReason.Expired);
        (await harness.AuthenticateAsync(new TokenSpec { ExpiresIn = TimeSpan.FromSeconds(-30) })).IsSuccess.Should().BeTrue("30 s past expiry is inside the 60 s skew");
    }

    [Fact]
    public async Task TokenIsRefusedOnceTheClockPassesItsExpiryPlusTheSkew()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        string token = harness.Idp.Mint(new TokenSpec { ExpiresIn = TimeSpan.FromMinutes(5) });

        (await harness.Provider.AuthenticateAsync(token, CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(59));
        (await harness.Provider.AuthenticateAsync(token, CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        (await harness.Provider.AuthenticateAsync(token, CancellationToken.None)).Should().BeOfType<AuthResult.Rejected>().Which.Reason.Should().Be(AuthFailureReason.Expired);
    }

    [Fact]
    public async Task TokenNotYetValid_IsRefused_ButWithinTheSkewItIsAccepted()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { NotBefore = TimeSpan.FromMinutes(2) })).Should().Be(AuthFailureReason.NotYetValid);
        (await harness.AuthenticateAsync(new TokenSpec { NotBefore = TimeSpan.FromSeconds(30) })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task TokenWithoutExpiry_IsRefused()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.AuthenticateAsync(new TokenSpec { ExpiresIn = null });

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TamperedPayload_FailsTheSignature()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { TamperAfterSigning = true })).Should().Be(AuthFailureReason.InvalidSignature);
    }

    [Fact]
    public async Task TokenSignedWithAnUnrelatedKey_UnderAPublishedKid_FailsTheSignature()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { SignWithForeignKey = true })).Should().Be(AuthFailureReason.InvalidSignature);
    }

    [Fact]
    public async Task UnknownKid_IsRefused_AndRefreshesTheKeysAtMostOnce()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        for (int i = 0; i < 20; i++)
        {
            (await RejectionOf(harness, new TokenSpec { KeyId = $"made-up-{i}", SignWithForeignKey = true })).Should().Be(AuthFailureReason.UnknownKey, string.Join(" | ", harness.Logs.GetSnapshot().Select(r => r.Exception?.ToString() ?? r.Message)));
        }

        harness.Idp.JwksRequests.Should().BeLessThanOrEqualTo(2, "an attacker rotating kid values must not make us hammer the provider");
    }

    [Fact]
    public async Task KeyRotation_IsPickedUpImmediatelyOnAnUnknownKid_AndRetiredKeysStopWorking()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        string oldKid = harness.Idp.CurrentKeyId;
        string oldToken = harness.Idp.Mint();
        (await harness.Provider.AuthenticateAsync(oldToken, CancellationToken.None)).IsSuccess.Should().BeTrue();

        string newKid = harness.Idp.AddKey();
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        string newToken = harness.Idp.Mint(new TokenSpec { KeyId = newKid });
        (await harness.Provider.AuthenticateAsync(newToken, CancellationToken.None)).IsSuccess.Should().BeTrue("the first token signed with the new key is accepted: the refresh is awaited");

        harness.Idp.RetireKey(oldKid);
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        string unknown = harness.Idp.Mint(new TokenSpec { KeyId = "never-published", SignWithForeignKey = true });
        await harness.Provider.AuthenticateAsync(unknown, CancellationToken.None);
        (await harness.Provider.AuthenticateAsync(oldToken, CancellationToken.None)).Should().BeOfType<AuthResult.Rejected>("after a refresh the retired key is gone");
    }

    [Fact]
    public async Task KeyRefreshes_AreSpacedByTheMinimumInterval_EvenWhenManyUnknownKidsArrive()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        await harness.AuthenticateAsync();
        int afterFirstFetch = harness.Idp.JwksRequests;

        for (int i = 0; i < 10; i++)
        {
            await harness.AuthenticateAsync(new TokenSpec { KeyId = $"unknown-{i}", SignWithForeignKey = true });
        }

        harness.Idp.JwksRequests.Should().Be(afterFirstFetch, "no refresh is allowed inside the 30 s minimum interval");
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        await harness.AuthenticateAsync(new TokenSpec { KeyId = "unknown-after", SignWithForeignKey = true });
        harness.Idp.JwksRequests.Should().Be(afterFirstFetch + 1, "one refresh is allowed once the interval has passed");
    }

    [Fact]
    public async Task KeysAreRefetchedWhenTheirLifetimeEnds()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync(new Dictionary<string, string?> { ["Clavaris:MetadataLifetime"] = "00:10:00" });
        await harness.AuthenticateAsync();
        int first = harness.Idp.JwksRequests;

        await harness.AuthenticateAsync();
        harness.Idp.JwksRequests.Should().Be(first, "cached keys are reused");

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.Idp.Mint();
        (await harness.Provider.AuthenticateAsync(harness.Idp.Mint(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Idp.JwksRequests.Should().Be(first + 1);
    }

    [Fact]
    public async Task AnAuthorityWithATrailingSlash_StillMatchesTheIssuer()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync(new Dictionary<string, string?> { ["Clavaris:Authority"] = "http://fake-idp.test/o/org-1/" });

        (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ADiscoveryDocumentThatNamesAnotherIssuer_IsNotTrusted()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        harness.Idp.DiscoveryIssuerOverride = "http://fake-idp.test/o/someone-else";

        (await RejectionOf(harness, new TokenSpec())).Should().Be(AuthFailureReason.KeyDiscoveryFailed);
    }

    [Fact]
    public async Task WhenTheProviderIsDown_ValidationFailsClosed_AndRecoversWhenItReturns()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        harness.Idp.Unavailable = true;

        (await RejectionOf(harness, new TokenSpec())).Should().Be(AuthFailureReason.KeyDiscoveryFailed);

        (await RejectionOf(harness, new TokenSpec())).Should().Be(AuthFailureReason.KeyDiscoveryFailed, "a failed fetch is not retried on every request");

        harness.Idp.Unavailable = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        (await harness.AuthenticateAsync(new TokenSpec())).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WhenTheProviderGoesDownAfterTheKeysExpire_TheLastKeysKeepWorking_ForAWhile_ThenFailClosed()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Clavaris:MetadataLifetime"] = "00:10:00",
            ["Clavaris:MaximumKeyAge"] = "02:00:00",
        });
        (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue();
        harness.Idp.Unavailable = true;

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue("the keys are stale but the provider is down");
        harness.Logs.GetSnapshot().Should().Contain(r => r.Id.Id == 9603, "serving stale keys is logged");

        int requests = harness.Idp.JwksRequests;
        for (int i = 0; i < 5; i++)
        {
            (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue();
        }

        harness.Idp.JwksRequests.Should().Be(requests, "a failed refresh is not retried on every request");

        harness.Clock.Advance(TimeSpan.FromHours(2));
        (await RejectionOf(harness, new TokenSpec())).Should().Be(AuthFailureReason.KeyDiscoveryFailed, "keys older than MaximumKeyAge are not trusted");

        harness.Idp.Unavailable = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue("the provider is back");
    }

    [Fact]
    public async Task ASlowProvider_DelaysOnlyTheRefreshingRequest_AndOnlyForTheStaleRefreshTimeout()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Clavaris:MetadataLifetime"] = "00:10:00",
            ["Clavaris:StaleRefreshTimeout"] = "00:00:02",
        });
        (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue();
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        var neverAnswers = new TaskCompletionSource();
        harness.Idp.Stall = neverAnswers.Task;

        Task<AuthResult> refreshing = harness.AuthenticateAsync();
        await harness.Idp.WaitForStalledRequestsAsync();

        AuthResult[] others = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => harness.AuthenticateAsync())).WaitAsync(TimeSpan.FromSeconds(5));
        others.Should().OnlyContain(r => r.IsSuccess, "callers with usable keys do not queue behind a slow refresh");
        refreshing.IsCompleted.Should().BeFalse("the refreshing request is still waiting for the provider");

        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        (await refreshing.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess.Should().BeTrue("after the timeout it carries on with the cached keys");
        harness.Logs.GetSnapshot().Should().Contain(r => r.Id.Id == 9603);
        neverAnswers.SetResult();
    }

    [Fact]
    public async Task KeysOlderThanTheMaximumAge_AndAProviderThatHangs_FailClosedInsteadOfThrowing()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Clavaris:MetadataLifetime"] = "00:10:00",
            ["Clavaris:MaximumKeyAge"] = "02:00:00",
        });
        (await harness.AuthenticateAsync()).IsSuccess.Should().BeTrue();
        harness.Clock.Advance(TimeSpan.FromHours(3));
        var neverAnswers = new TaskCompletionSource();
        harness.Idp.Stall = neverAnswers.Task;

        Task<AuthResult> attempt = harness.AuthenticateAsync();
        await harness.Idp.WaitForStalledRequestsAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(40));

        AuthResult result = await attempt.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().BeOfType<AuthResult.Rejected>().Which.Reason.Should().Be(AuthFailureReason.KeyDiscoveryFailed, "an unusable cache and a hanging provider is a 401, not an unhandled exception");
        neverAnswers.SetResult();
    }

    [Fact]
    public async Task WithoutAnyCachedKeys_CallersWaitForTheFirstFetch()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        var release = new TaskCompletionSource();
        harness.Idp.Stall = release.Task;

        Task<AuthResult> first = harness.AuthenticateAsync();
        await harness.Idp.WaitForStalledRequestsAsync();
        Task<AuthResult> second = harness.AuthenticateAsync();

        second.IsCompleted.Should().BeFalse("there is nothing to validate with yet");
        release.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess.Should().BeTrue();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess.Should().BeTrue();
        harness.Idp.JwksRequests.Should().Be(1, "the second caller reuses the first fetch");
    }

    [Fact]
    public async Task TokenWithoutASubject_IsRefused()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { Subject = null })).Should().Be(AuthFailureReason.MissingSubject);
    }

    [Fact]
    public async Task TheBoundsOfTheUserTable_AreEnforcedHere_SoAnAcceptedTokenCanAlwaysBeProvisioned()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        (await RejectionOf(harness, new TokenSpec { Subject = new string('s', ExternalIdentityLimits.MaxSubjectLength + 1) })).Should().Be(AuthFailureReason.MissingSubject);

        var longestAllowed = new TokenSpec { Subject = new string('s', ExternalIdentityLimits.MaxSubjectLength), Locale = new string('l', ExternalIdentityLimits.MaxLocaleLength) };
        (await harness.AuthenticateAsync(longestAllowed)).Should().BeOfType<AuthResult.Authenticated>().Subject.Identity.Locale.Should().HaveLength(ExternalIdentityLimits.MaxLocaleLength);

        var oversizedProfile = new TokenSpec { Locale = new string('l', ExternalIdentityLimits.MaxLocaleLength + 1), Name = new string('n', ExternalIdentityLimits.MaxProfileTextLength + 1) };
        ValidatedIdentity identity = (await harness.AuthenticateAsync(oversizedProfile)).Should().BeOfType<AuthResult.Authenticated>().Subject.Identity;
        identity.Locale.Should().BeNull("profile data that does not fit is dropped, not allowed to block the login");
        identity.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task ProfileClaimsWithControlCharacters_AreDropped_NotPropagated()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();

        AuthResult result = await harness.AuthenticateAsync(new TokenSpec { Email = "a@example.com\r\nBcc: x@evil.test", Name = new string('n', 400) });

        ValidatedIdentity identity = result.Should().BeOfType<AuthResult.Authenticated>().Subject.Identity;
        identity.Email.Should().BeNull();
        identity.DisplayName.Should().BeNull();
        identity.Subject.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TheMetadataClient_CanOnlyReachTheAuthoritysHost()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        HttpClient client = harness.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClavarisAuthProvider.MetadataClientName);

        Func<Task> act = () => client.GetAsync("http://evil.test/steal");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
    }

    [Theory]
    [InlineData("http://fake-idp.test:9999/steal")]
    [InlineData("http://fake-idp.test:8080/steal")]
    public async Task TheMetadataClient_CanOnlyReachTheAuthoritysPort(string url)
    {
        await using AuthHarness harness = await AuthHarness.StartAsync();
        HttpClient client = harness.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClavarisAuthProvider.MetadataClientName);

        Func<Task> act = () => client.GetAsync(url);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
    }

    [Fact]
    public async Task AnAuthorityOnANonDefaultPort_AllowsThatPort()
    {
        await using AuthHarness harness = await AuthHarness.StartAsync(new Dictionary<string, string?> { ["Clavaris:Authority"] = "http://localhost:8080/o/org-1" });
        HttpClient client = harness.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClavarisAuthProvider.MetadataClientName);

        Func<Task> otherPort = () => client.GetAsync("http://localhost:9090/o/org-1");

        await otherPort.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
        harness.Services.GetRequiredService<IOptionsMonitor<SakerWatch.BuildingBlocks.Resilience.HttpClientResilienceOptions>>()
            .Get(ClavarisAuthProvider.MetadataClientName).AllowedHosts.Should().Equal("localhost:8080");
    }

    [Theory]
    [InlineData("", "api", "must be an absolute URL")]
    [InlineData("not a url", "api", "must be an absolute URL")]
    [InlineData("http://clavaris.example/o/x", "api", "must use https")]
    [InlineData("https://clavaris.example/o/x?tenant=1", "api", "query string")]
    [InlineData("https://clavaris.example/o/x", "", "Audience")]
    public void InvalidConfiguration_IsRejectedWhenTheOptionsAreFirstRead(string authority, string audience, string expectedMessage)
    {
        var services = new ServiceCollection();
        services.AddClavarisAuth(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clavaris:Authority"] = authority,
            ["Clavaris:Audience"] = audience,
        }).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => _ = provider.GetRequiredService<IOptions<ClavarisOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void AnAuthorityLongerThanTheIssuerColumn_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddClavarisAuth(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clavaris:Authority"] = "https://clavaris.example/o/" + new string('x', ExternalIdentityLimits.MaxIssuerLength),
            ["Clavaris:Audience"] = "api",
        }).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => _ = provider.GetRequiredService<IOptions<ClavarisOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*at most 512*");
    }

    [Fact]
    public void ClockSkewAbove60Seconds_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddClavarisAuth(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clavaris:Authority"] = "https://clavaris.example/o/x",
            ["Clavaris:Audience"] = "api",
            ["Clavaris:ClockSkew"] = "00:05:00",
        }).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => _ = provider.GetRequiredService<IOptions<ClavarisOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*ClockSkew*");
    }
}
