using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SakerWatch.Contract.Tests;

/// <summary>
/// Hosts <c>Host.Api</c> in memory with throw-away configuration. Nothing here connects to a datastore: the contract tests only
/// read routes, metadata and the generated OpenAPI document, and the clients connect lazily.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=contract;Username=u;Password=p;Timeout=1");
        builder.UseSetting("ConnectionStrings:Mongo", "mongodb://127.0.0.1:1");
        builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false,connectTimeout=500");
        builder.UseSetting("OpenApi:Anonymous", "true");
        builder.UseSetting("Audit:ActorPepper", "q8Zy1p0m2V3x4W5e6R7t8Y9u0I1o2P3a4S5d6F7g8H9=");
        builder.UseSetting("Clavaris:Authority", "https://clavaris.invalid/o/00000000-0000-0000-0000-000000000000");
        builder.UseSetting("Clavaris:Audience", "contract-tests");
    }
}
