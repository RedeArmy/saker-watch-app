using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.Hosts.Worker;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.AddSakerObservability("saker-worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIdGenerator, TimeProviderIdGenerator>();
builder.Services.AddHostedService<WorkerLifecycleService>();

await builder.Build().RunAsync();
