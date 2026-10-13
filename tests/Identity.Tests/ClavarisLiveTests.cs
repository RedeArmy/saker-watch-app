using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SakerWatch.Identity.Contracts;
using SakerWatch.Identity.Infrastructure.Clavaris;

namespace SakerWatch.Identity.Tests;

/// <summary>
/// Opt-in check of the provider against a <b>running</b> Clavaris and a real access token (docs/runbooks/local-development.md).
/// It does nothing unless <c>CLAVARIS_LIVE_AUTHORITY</c>, <c>CLAVARIS_LIVE_AUDIENCE</c> and <c>CLAVARIS_LIVE_TOKEN_FILE</c> are set, so CI never needs a Clavaris.
/// </summary>
[Trait("Category", "ClavarisLive")]
public sealed class ClavarisLiveTests
{
    [Fact]
    public async Task RealClavarisToken_IsAcceptedWithItsRealShape()
    {
        string? authority = Environment.GetEnvironmentVariable("CLAVARIS_LIVE_AUTHORITY");
        string? audience = Environment.GetEnvironmentVariable("CLAVARIS_LIVE_AUDIENCE");
        string? tokenFile = Environment.GetEnvironmentVariable("CLAVARIS_LIVE_TOKEN_FILE");
        if (string.IsNullOrEmpty(authority) || string.IsNullOrEmpty(audience) || string.IsNullOrEmpty(tokenFile))
        {
            return;
        }

        string token = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(tokenFile)).RootElement.GetProperty("access_token").GetString()!;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clavaris:Authority"] = authority,
            ["Clavaris:Audience"] = audience,
            ["Clavaris:RequireHttpsMetadata"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole());
        services.AddSingleton(TimeProvider.System);
        services.AddClavarisAuth(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();

        AuthResult result = await provider.GetRequiredService<IAuthProvider>().AuthenticateAsync(token, CancellationToken.None);

        ValidatedIdentity identity = result.Should().BeOfType<AuthResult.Authenticated>().Subject.Identity;
        identity.Issuer.Should().Be(authority);
        identity.Scopes.Should().Contain("openid");
        Guid.TryParse(identity.Subject, out _).Should().BeTrue("Clavaris subjects are account UUIDs");
    }
}
