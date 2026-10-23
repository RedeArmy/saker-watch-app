using System.Data.Common;
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
    /// Every problem response (validation, 404, 429, 500...) carries <c>correlationId</c>, <c>traceId</c> and a <c>code</c> a client can switch on;
    /// unhandled exceptions become a generic 500 without stack trace or message.
    /// </summary>
    public static IServiceCollection AddSakerProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                // A failure that came from a Result has its code already. The ones raised by the pipeline (limiter, busy database, unhandled exception) name it
                // only in the type, and the framework's own (an unknown route, a bad body) in neither, so the code is taken from the type or made from the status.
                context.ProblemDetails.Extensions.TryAdd("code", CodeOf(context.ProblemDetails, context.HttpContext));
                context.ProblemDetails.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context.HttpContext);
                context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }

    private static string CodeOf(Microsoft.AspNetCore.Mvc.ProblemDetails problem, HttpContext http) =>
        problem.Type is { } type && type.StartsWith(ResultHttpExtensions.ProblemTypeBase, StringComparison.Ordinal)
            ? type[ResultHttpExtensions.ProblemTypeBase.Length..]
            : $"http.{http.Response.StatusCode}";
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

        // The database refusing to wait (a lock timeout, a deadlock, a serialization failure) says the work was not done and can be asked again: that is
        // "busy, retry", not an unexpected failure of the service (and the retry is safe: nothing was committed).
        if (RetryableDatabaseState(exception) is { } state)
        {
            DatabaseBusy(state, httpContext.Request.Method, httpContext.GetEndpoint()?.DisplayName ?? "unknown");
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            httpContext.Response.Headers.RetryAfter = "1";
            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails =
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "The service is busy; try again.",
                    Type = ResultHttpExtensions.ProblemTypeBase + "busy",
                },
            });
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

    // lock_not_available (a lock timeout), deadlock_detected and serialization_failure, wherever in the chain of exceptions the driver's is.
    private static string? RetryableDatabaseState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: "55P03" or "40P01" or "40001" } database)
            {
                return database.SqlState;
            }
        }

        return null;
    }

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "The database refused to wait ({SqlState}) in {Method} {Endpoint}; the client was told to retry")]
    private partial void DatabaseBusy(string sqlState, string method, string endpoint);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Error, Message = "Unhandled exception in {Method} {Endpoint}")]
    private partial void UnhandledException(Exception exception, string method, string endpoint);
}
