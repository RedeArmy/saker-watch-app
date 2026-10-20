using System.Reflection;
using Asp.Versioning;
using Asp.Versioning.Builder;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Mongo;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.BuildingBlocks.Web;
using SakerWatch.BuildingBlocks.Web.Health;
using SakerWatch.BuildingBlocks.Web.RateLimiting;
using SakerWatch.BuildingBlocks.Web.Security;
using SakerWatch.Hosts.Api.Api;
using SakerWatch.Identity.Infrastructure.Clavaris;
using SakerWatch.Identity.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.Watchlist.Infrastructure;
using Serilog;
using Serilog.Events;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureSakerKestrel();
builder.AddSakerObservability(
    "saker-api",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());
builder.Services.AddSakerWeb(builder.Configuration);

// The modules this host runs. One list feeds the handler scan, the integration-event registry and the job registry, so a module added
// here is known to all three (an empty list for jobs or events would only fail later, when something is first enqueued).
Assembly[] moduleAssemblies =
[
    typeof(SakerWatch.Identity.Application.IAssemblyMarker).Assembly,
    typeof(SakerWatch.Watchlist.Application.IAssemblyMarker).Assembly,
    typeof(SakerWatch.MarketData.Application.IAssemblyMarker).Assembly,
    typeof(SakerWatch.Calculator.Application.IAssemblyMarker).Assembly,
];
builder.Services.AddApplicationPipeline(moduleAssemblies);
builder.Services.AddIdempotency(builder.Configuration);
builder.Services.AddIntegrationEvents(moduleAssemblies);

// The API queues the first load of a stock that the worker performs: it knows the payload names but runs none of the MarketData job handlers.
builder.Services.AddJobs(typeof(SakerWatch.Identity.Application.IAssemblyMarker).Assembly, typeof(SakerWatch.Watchlist.Application.IAssemblyMarker).Assembly);
builder.Services.AddJobPayloads(typeof(SakerWatch.MarketData.Application.IAssemblyMarker).Assembly);

// Datastores: fail fast at startup when a connection string is missing; readiness reports each one.
builder.Services.AddSakerPostgres(builder.Configuration);
builder.Services.AddSakerDbContext<PlatformDbContext>(PlatformDbContext.Schema);
builder.Services.AddSakerDbContext<IdentityDbContext>(IdentityDbContext.Schema);
builder.Services.AddSakerMongo(builder.Configuration);
builder.Services.AddSakerRedis(builder.Configuration);

// Finding and registering stocks (ADR-0032) and the watchlist.
builder.Services.AddMarketDataLookup(builder.Configuration);
builder.Services.AddWatchlist(builder.Configuration);

// Authentication (Clavaris bearer tokens), the resolved internal user, and default-deny authorization.
builder.Services.AddClavarisAuth(builder.Configuration);
builder.Services.AddSakerCurrentUser();
builder.Services.AddSakerAuthorization();
builder.Services.AddSakerApi();

WebApplication app = builder.Build();

app.UseSakerWeb();
app.UseSerilogRequestLogging(options => options.GetLevel = RequestLogLevel);
app.UseRouting();
app.UseSakerCors();
app.UseSakerPreAuthenticationRateLimiting();

// Development only, and before authentication: the UI is static and the default-deny policy would otherwise answer 401 for it.
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Saker Watch API v1"));
}

app.UseAuthentication();
app.UseSakerUserResolution();
app.UseAuthorization();
app.UseSakerRateLimiting();
app.MapSakerHealth();

ApiVersionSet versions = app.NewApiVersionSet().HasApiVersion(ApiSetup.V1).ReportApiVersions().Build();
RouteGroupBuilder v1 = app.MapV1(versions);
v1.MapMe();
v1.MapStocks();
v1.MapWatchlist();
v1.MapPortfolios();
v1.MapCalculations();

// The OpenAPI document and Swagger UI describe the surface only. Outside development the document needs a valid token unless
// OpenApi:Anonymous is set (ADR-0014).
bool openApiAnonymous = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Anonymous");
IEndpointConventionBuilder openApi = app.MapOpenApi().WithDocumentPerVersion();
if (openApiAnonymous)
{
    openApi.AllowAnonymous();
}

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

/// <summary>Entry point type, public so the contract tests can host the API in memory.</summary>
public partial class Program
{
    /// <summary>Not instantiable; the type only names the entry point for in-memory hosting.</summary>
    protected Program()
    {
    }
}
