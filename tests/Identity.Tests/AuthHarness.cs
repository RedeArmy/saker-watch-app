using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.Identity.Contracts;
using SakerWatch.Identity.Infrastructure.Clavaris;
using SakerWatch.TestSupport;

namespace SakerWatch.Identity.Tests;

/// <summary>A real <see cref="IAuthProvider"/> wired to an in-process fake Clavaris and a controllable clock.</summary>
internal sealed class AuthHarness : IAsyncDisposable
{
    private AuthHarness(FakeIdentityProvider idp, FakeTimeProvider clock, ServiceProvider services)
    {
        Idp = idp;
        Clock = clock;
        Services = services;
    }

    public FakeIdentityProvider Idp { get; }

    public FakeTimeProvider Clock { get; }

    public ServiceProvider Services { get; }

    public FakeLogCollector Logs => Services.GetRequiredService<FakeLogCollector>();

    public IAuthProvider Provider => Services.GetRequiredService<IAuthProvider>();

    public static async Task<AuthHarness> StartAsync(Dictionary<string, string?>? overrides = null, int metadataRetries = 0)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        FakeIdentityProvider idp = await FakeIdentityProvider.StartAsync(clock: clock);

        var settings = new Dictionary<string, string?>
        {
            ["Clavaris:Authority"] = idp.Issuer,
            ["Clavaris:Audience"] = FakeIdentityProvider.Audience,
            ["Clavaris:RequireHttpsMetadata"] = "false",
        };
        foreach ((string key, string? value) in overrides ?? [])
        {
            settings[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging());
        services.AddSingleton<TimeProvider>(clock);
        services.AddClavarisAuth(configuration);
        services.AddHttpClient(ClavarisAuthProvider.MetadataClientName).ConfigurePrimaryHttpMessageHandler(idp.CreateHandler);
        services.Configure<HttpClientResilienceOptions>(ClavarisAuthProvider.MetadataClientName, o =>
        {
            o.MaxRetries = metadataRetries;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });
        return new AuthHarness(idp, clock, services.BuildServiceProvider());
    }

    public async Task<AuthResult> AuthenticateAsync(TokenSpec? spec = null) =>
        await Provider.AuthenticateAsync(Idp.Mint(spec), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await Idp.DisposeAsync();
    }
}
