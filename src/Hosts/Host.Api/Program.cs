using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Mongo;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.BuildingBlocks.Web;
using SakerWatch.BuildingBlocks.Web.Health;
using SakerWatch.Identity.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddSakerObservability(
    "saker-api",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());
builder.Services.AddSakerWeb(builder.Configuration);
builder.Services.AddApplicationPipeline();
builder.Services.AddIdempotency(builder.Configuration);
builder.Services.AddIntegrationEvents();

// Datastores: fail fast at startup when a connection string is missing; readiness reports each one.
builder.Services.AddSakerPostgres(builder.Configuration);
builder.Services.AddSakerDbContext<PlatformDbContext>(PlatformDbContext.Schema);
builder.Services.AddSakerDbContext<IdentityDbContext>(IdentityDbContext.Schema);
builder.Services.AddSakerMongo(builder.Configuration);
builder.Services.AddSakerRedis(builder.Configuration);

WebApplication app = builder.Build();

app.UseSakerWeb();
app.UseSerilogRequestLogging(options => options.GetLevel = RequestLogLevel);
app.UseRouting();
app.UseSakerRateLimiting();
app.MapSakerHealth();

await app.RunAsync();

// Health probes would drown the log, so they are logged only at Verbose; server errors stand out.
static LogEventLevel RequestLogLevel(HttpContext context, double elapsedMs, Exception? exception)
{
    if (context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
    {
        return LogEventLevel.Verbose;
    }

    return exception is not null || context.Response.StatusCode >= 500 ? LogEventLevel.Error : LogEventLevel.Information;
}
