using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Web;
using SakerWatch.BuildingBlocks.Web.Health;
using Serilog;
using Serilog.Events;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddSakerObservability(
    "saker-api",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());
builder.Services.AddSakerWeb(builder.Configuration);
builder.Services.AddApplicationPipeline();

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
