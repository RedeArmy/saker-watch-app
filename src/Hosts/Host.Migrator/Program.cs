using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.Hosts.Migrator;

// Deploy-time job: applies database migrations and exits. It is a separate step from the API, which never migrates
// on startup. Needs the admin connection string (ConnectionStrings__PostgresAdmin).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSakerObservability("saker-migrator");

string admin = builder.Configuration.GetConnectionString("PostgresAdmin")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgresAdmin is required.");
foreach (IMigrationStep step in MigrationSteps.Create(admin))
{
    builder.Services.AddSingleton(step);
}

builder.Services.AddSingleton<MigrationPipeline>();

using IHost host = builder.Build();
try
{
    await host.Services.GetRequiredService<MigrationPipeline>().RunAsync(CancellationToken.None);
    return 0;
}
catch (Exception)
{
    // The failure is already logged by the pipeline with the failing step.
    return 1;
}
