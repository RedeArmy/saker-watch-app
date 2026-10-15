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
using SakerWatch.BuildingBlocks.Web.Security;
using SakerWatch.Hosts.Api.Api;
using SakerWatch.Identity.Infrastructure.Clavaris;
using SakerWatch.Identity.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddSakerObservability(
    "saker-api",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());
builder.Services.AddSakerWeb(builder.Configuration);
builder.Services.AddApplicationPipeline(typeof(SakerWatch.Identity.Application.IAssemblyMarker).Assembly);
builder.Services.AddIdempotency(builder.Configuration);
builder.Services.AddIntegrationEvents();
builder.Services.AddJobs();

// Datastores: fail fast at startup when a connection string is missing; readiness reports each one.
builder.Services.AddSakerPostgres(builder.Configuration);
builder.Services.AddSakerDbContext<PlatformDbContext>(PlatformDbContext.Schema);
builder.Services.AddSakerDbContext<IdentityDbContext>(IdentityDbContext.Schema);
builder.Services.AddSakerMongo(builder.Configuration);
builder.Services.AddSakerRedis(builder.Configuration);

// Authentication (Clavaris bearer tokens), the resolved internal user, and default-deny authorization.
builder.Services.AddClavarisAuth(builder.Configuration);
builder.Services.AddSakerCurrentUser();
builder.Services.AddSakerAuthorization();
builder.Services.AddSakerApi();

WebApplication app = builder.Build();

app.UseSakerWeb();
app.UseSerilogRequestLogging(options => options.GetLevel = RequestLogLevel);
app.UseRouting();

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
app.MapV1(versions).MapMe();

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
