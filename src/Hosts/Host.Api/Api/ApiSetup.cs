using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using SakerWatch.BuildingBlocks.Web.Idempotency;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>Versioning and OpenAPI of the public API (ADR-0014).</summary>
internal static class ApiSetup
{
    /// <summary>The only API version today; a breaking change needs a new major version.</summary>
    public static readonly ApiVersion V1 = new(1, 0);

    /// <summary>Route prefix of every versioned endpoint.</summary>
    public const string VersionedPrefix = "/v{version:apiVersion}";

    /// <summary>
    /// URL-segment versioning with the supported versions reported in <c>api-supported-versions</c>, and one OpenAPI document per
    /// version (<c>/openapi/v1.json</c>) that declares the Bearer scheme.
    /// </summary>
    public static IServiceCollection AddSakerApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = V1;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        }).AddOpenApi(versioned =>
        {
            versioned.Document.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Saker Watch API",
                    Version = document.Info?.Version ?? "v1",
                    Description = "Personal stock watchlist and research assistant. Signals are research aids, never financial advice.",
                };
                // The server URL depends on where the API is deployed, so the contract does not pin one.
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "A Clavaris access token (RS256).",
                };
                return Task.CompletedTask;
            });
            // Every decimal travels as a string (docs/api/README.md), so the contract says so and generated clients never use floating point.
            versioned.Document.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(decimal) || context.JsonTypeInfo.Type == typeof(decimal?))
                {
                    schema.Type = JsonSchemaType.String | (context.JsonTypeInfo.Type == typeof(decimal?) ? JsonSchemaType.Null : 0);
                    schema.Format = "decimal";
                    schema.Pattern = "^-?(?:0|[1-9]\\d*)(?:\\.\\d+)?$";
                }

                return Task.CompletedTask;
            });
            versioned.Document.AddOperationTransformer((operation, context, _) =>
            {
                // Default-deny: every operation needs a bearer token unless its endpoint is explicitly anonymous.
                bool anonymous = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any();
                if (!anonymous)
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", context.Document)] = [] },
                    ];
                }

                DescribeFailures(operation);

                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequiresIdempotencyKeyMetadata>().Any())
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyKeyReader.HeaderName,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Client-generated key (16-64 letters, digits, '-' or '_') that makes the request safe to retry. A repeat with the same key and body replays the first response.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[A-Za-z0-9_-]{16,64}$" },
                    });
                }

                return Task.CompletedTask;
            });
        });
        return services;
    }

    // What the two failures that any endpoint can answer with mean for the caller. 500 is a defect on our side: the body has the correlation and trace ids to
    // quote and never the cause. 503 is "not now": the database refused to wait (a lock timeout, a deadlock) or a provider the request needed is unavailable;
    // nothing was committed, so the request can be sent again after the seconds in Retry-After (a mutating request carries its Idempotency-Key).
    private static void DescribeFailures(OpenApiOperation operation)
    {
        if (operation.Responses is null)
        {
            return;
        }

        if (operation.Responses.TryGetValue("500", out IOpenApiResponse? unexpected) && unexpected is OpenApiResponse internalError)
        {
            internalError.Description = "Unexpected error on our side. The problem carries a correlationId and a traceId to quote when reporting it, and never the cause.";
        }

        if (operation.Responses.TryGetValue("503", out IOpenApiResponse? busy) && busy is OpenApiResponse unavailable)
        {
            unavailable.Description = "Busy or a dependency is unavailable: nothing was committed and the request can be sent again after the seconds in Retry-After.";
            unavailable.Headers ??= new Dictionary<string, IOpenApiHeader>();
            unavailable.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Seconds to wait before sending the request again.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0" },
            };
        }
    }

    /// <summary>The versioned group every endpoint of version 1 is mapped on.</summary>
    public static RouteGroupBuilder MapV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versions)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGroup(VersionedPrefix).WithApiVersionSet(versions).HasApiVersion(V1);
    }
}
