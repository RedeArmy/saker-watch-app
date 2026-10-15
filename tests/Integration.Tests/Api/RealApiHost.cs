extern alias HostApi;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.Identity.Infrastructure.Clavaris;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>
/// The real <c>Host.Api</c> (its own <c>Program</c>, versioning, OpenAPI, authentication, authorization) in memory, over a real
/// migrated PostgreSQL and a fake Clavaris. Mongo and Redis are configured but never reached by these tests.
/// </summary>
internal sealed class RealApiHost : WebApplicationFactory<HostApi::Program>
{
    private readonly bool _ownsIdp;
    private readonly (string Authority, string Audience)? _live;

    private RealApiHost(MigratedDatabase db, FakeIdentityProvider idp, bool ownsIdp, (string Authority, string Audience)? live = null)
    {
        _live = live;
        Db = db;
        Idp = idp;
        _ownsIdp = ownsIdp;
    }

    /// <summary>Starts a host that validates tokens against a <b>running</b> Clavaris instead of the fake (opt-in live checks).</summary>
    public static async Task<RealApiHost> StartLiveAsync(TimescalePostgresFixture postgres, string authority, string audience)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        FakeIdentityProvider idp = await FakeIdentityProvider.StartAsync();
        return new RealApiHost(db, idp, ownsIdp: true, (authority, audience));
    }

    public MigratedDatabase Db { get; }

    public FakeIdentityProvider Idp { get; }

    /// <summary>Starts a host over new backends, or over those of <paramref name="sameBackends"/> (same data and identity provider, but its own caches).</summary>
    public static async Task<RealApiHost> StartAsync(TimescalePostgresFixture postgres, RealApiHost? sameBackends = null)
    {
        MigratedDatabase db = sameBackends?.Db ?? await postgres.CreateMigratedDatabaseAsync();
        FakeIdentityProvider idp = sameBackends?.Idp ?? await FakeIdentityProvider.StartAsync();
        return new RealApiHost(db, idp, ownsIdp: sameBackends is null);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_ownsIdp)
        {
            await Idp.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", Db.Api);
        builder.UseSetting("ConnectionStrings:Mongo", "mongodb://127.0.0.1:1");
        builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false");
        builder.UseSetting("OpenApi:Anonymous", "true");
        builder.UseSetting("Audit:ActorPepper", ApiTestHost.PepperBase64);
        builder.UseSetting("Clavaris:Authority", _live?.Authority ?? Idp.Issuer);
        builder.UseSetting("Clavaris:Audience", _live?.Audience ?? FakeIdentityProvider.Audience);
        builder.UseSetting("Clavaris:RequireHttpsMetadata", "false");
        if (_live is null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(ClavarisAuthProvider.MetadataClientName).ConfigurePrimaryHttpMessageHandler(Idp.CreateHandler));
        }
    }
}
