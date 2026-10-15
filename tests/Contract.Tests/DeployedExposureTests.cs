using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SakerWatch.Contract.Tests;

/// <summary>Outside development the API description is not public and the Swagger UI is not served (ADR-0014).</summary>
public sealed class DeployedExposureTests
{
    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=contract;Username=u;Password=p;Timeout=1");
            builder.UseSetting("ConnectionStrings:Mongo", "mongodb://127.0.0.1:1");
            builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false");
            builder.UseSetting("Audit:ActorPepper", "q8Zy1p0m2V3x4W5e6R7t8Y9u0I1o2P3a4S5d6F7g8H9=");
            builder.UseSetting("Clavaris:Authority", "https://clavaris.invalid/o/00000000-0000-0000-0000-000000000000");
            builder.UseSetting("Clavaris:Audience", "contract-tests");
        }
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    public async Task InProduction_TheApiDescriptionAndUi_AreNotAvailableToAnonymousCallers(string path)
    {
        await using var factory = new ProductionFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
