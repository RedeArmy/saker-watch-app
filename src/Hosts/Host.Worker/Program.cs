using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Mongo;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Outbox;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.Hosts.Worker;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.AddSakerObservability("saker-worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
builder.Services.AddSakerPostgres(builder.Configuration);
builder.Services.AddSakerDbContext<PlatformDbContext>(PlatformDbContext.Schema);
builder.Services.AddIntegrationEvents();
builder.Services.AddOutboxDispatcher(builder.Configuration);
builder.Services.AddSakerMongo(builder.Configuration);
builder.Services.AddSakerRedis(builder.Configuration);
builder.Services.AddHostedService<WorkerLifecycleService>();

await builder.Build().RunAsync();
