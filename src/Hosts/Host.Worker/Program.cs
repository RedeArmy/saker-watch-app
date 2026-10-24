using System.Reflection;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Mongo;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Jobs;
using SakerWatch.BuildingBlocks.Persistence.Outbox;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.Hosts.Worker;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.Retention.Application;
using SakerWatch.Retention.Infrastructure;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.AddSakerObservability("saker-worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
builder.Services.AddSakerPostgres(builder.Configuration);
builder.Services.AddSakerDbContext<PlatformDbContext>(PlatformDbContext.Schema);

// The modules this host runs: one list for the integration-event consumers and the job handlers and payloads.
Assembly[] moduleAssemblies = [typeof(RetentionRunJob).Assembly, typeof(BackfillStockJob).Assembly];
builder.Services.AddIntegrationEvents(moduleAssemblies);
builder.Services.AddOutboxDispatcher(builder.Configuration);
builder.Services.AddJobs(moduleAssemblies);
builder.Services.AddRetention(builder.Configuration);
builder.Services.AddMarketData(builder.Configuration);
builder.Services.AddJobDispatcher(builder.Configuration);
builder.Services.AddSakerMongo(builder.Configuration);
builder.Services.AddSakerRedis(builder.Configuration);
builder.Services.AddHostedService<WorkerLifecycleService>();

// "marketdata <change> ..." runs one change to the life of a listing and exits; the hosted services are never started (ADR-0039).
if (args.Length > 0 && string.Equals(args[0], "marketdata", StringComparison.OrdinalIgnoreCase))
{
    using IHost tool = builder.Build();

    // Ctrl-C cancels the command (a hung database call included) instead of killing the process mid-write; 130 is the usual status of an interrupt.
    using var interrupted = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        interrupted.Cancel();
    };
    try
    {
        return await MarketDataCommandLine.RunAsync(tool.Services, args[1..], interrupted.Token);
    }
    catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
    {
        return 130;
    }
}

await builder.Build().RunAsync();
return 0;
