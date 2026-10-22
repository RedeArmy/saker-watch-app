using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Web.Correlation;

namespace SakerWatch.BuildingBlocks.Web.Problems;

/// <summary>Registers ProblemDetails with correlation and trace ids and a safe global exception handler.</summary>
public static class ProblemDetailsSetup
{
    /// <summary>
    /// Every problem response (validation, 404, 429, 500...) carries <c>correlationId</c> and <c>traceId</c>;
    /// unhandled exceptions become a generic 500 without stack trace or message.
    /// </summary>
    public static IServiceCollection AddSakerProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context.HttpContext);
                context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }
}

/// <summary>Turns unhandled exceptions into a generic 500 ProblemDetails and logs the exception server-side.</summary>
/// <param name="problems">Writer that applies the shared problem-details customization.</param>
/// <param name="logger">Logger for the unhandled exception.</param>
internal sealed partial class GlobalExceptionHandler(IProblemDetailsService problems, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const int _clientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nothing to report and no body to write. The status is the one nginx made usual for it (there is no standard one),
            // so request logs and metrics do not count the abandoned request as a 200.
            httpContext.Response.StatusCode = _clientClosedRequest;
            return true;
        }

        UnhandledException(exception, httpContext.Request.Method, httpContext.GetEndpoint()?.DisplayName ?? "unknown");
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = ResultHttpExtensions.ProblemTypeBase + "internal_error",
            },
        });
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Error, Message = "Unhandled exception in {Method} {Endpoint}")]
    private partial void UnhandledException(Exception exception, string method, string endpoint);
}
