using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.Hosts.Migrator;

// Deploy-time job: applies database migrations and exits. It is a separate step from the API, which never migrates
// on startup. Needs the admin connection strings (ConnectionStrings__PostgresAdmin, ConnectionStrings__MongoAdmin).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSakerObservability("saker-migrator");

string postgresAdmin = builder.Configuration.GetConnectionString("PostgresAdmin")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgresAdmin is required.");
string mongoAdmin = builder.Configuration.GetConnectionString("MongoAdmin")
    ?? throw new InvalidOperationException("ConnectionStrings:MongoAdmin is required.");
string mongoDatabase = builder.Configuration["Mongo:Database"] ?? "saker";

foreach (IMigrationStep step in MigrationSteps.Create(postgresAdmin))
{
    builder.Services.AddSingleton(step);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMigrationStep>(provider =>
    new MongoMigrationStep(mongoAdmin, mongoDatabase, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILoggerFactory>()));

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
