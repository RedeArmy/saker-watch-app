using System.Diagnostics;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Application.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Application.Behaviors;

/// <summary>
/// Outermost step: opens the span <c>&lt;Module&gt;.&lt;UseCase&gt;</c> and records the duration metric, so
/// logging, validation and the handler all run inside the span (ADR-0016).
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The result type.</typeparam>
/// <param name="timeProvider">Clock for the duration measurement.</param>
public sealed class TracingBehavior<TRequest, TResponse>(TimeProvider timeProvider) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    private const string _success = "success";
    private const string _failure = "failure";
    private const string _exception = "exception";

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        using Activity? activity = UseCaseTelemetry.Source.StartActivity(UseCaseInfo<TRequest>.SpanName);
        activity?.SetTag("saker.module", UseCaseInfo<TRequest>.Module);
        activity?.SetTag("saker.usecase", UseCaseInfo<TRequest>.UseCase);

        long start = timeProvider.GetTimestamp();
        try
        {
            TResponse response = await next();

            if (response.IsSuccess)
            {
                activity?.SetTag("saker.outcome", _success);
                Record(start, _success, errorType: null);
                return response;
            }

            Error error = response.Error!;
            activity?.SetTag("saker.outcome", _failure);
            activity?.SetTag("saker.error_code", error.Code);
            if (error.Type == ErrorType.Failure)
            {
                activity?.SetStatus(ActivityStatusCode.Error, error.Code);
            }

            Record(start, _failure, error.Type.ToString());
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetTag("saker.outcome", _exception);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            Record(start, _exception, errorType: null);
            throw;
        }
    }

    private void Record(long start, string outcome, string? errorType)
    {
        double seconds = timeProvider.GetElapsedTime(start).TotalSeconds;
        var tags = new TagList
        {
            { "usecase", UseCaseInfo<TRequest>.UseCase },
            { "outcome", outcome },
        };
        if (errorType is not null)
        {
            tags.Add("error_type", errorType);
        }

        UseCaseTelemetry.Duration(UseCaseInfo<TRequest>.MetricModule).Record(seconds, tags);
    }
}
