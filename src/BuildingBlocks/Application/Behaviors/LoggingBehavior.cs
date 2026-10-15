using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Application.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Application.Behaviors;

/// <summary>
/// Logs the outcome of every use case with structured properties only. The request itself is never logged
/// (it may carry personal data or secrets, ADR-0016).
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The result type.</typeparam>
/// <param name="logger">Logger for the pipeline.</param>
/// <param name="timeProvider">Clock for the elapsed time.</param>
public sealed partial class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger,
    TimeProvider timeProvider) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        long start = timeProvider.GetTimestamp();
        string useCase = UseCaseInfo<TRequest>.SpanName;
        try
        {
            TResponse response = await next();
            double elapsedMs = timeProvider.GetElapsedTime(start).TotalMilliseconds;

            if (response.IsSuccess)
            {
                UseCaseCompleted(useCase, elapsedMs);
            }
            else if (response.Error!.Type == ErrorType.Failure)
            {
                UseCaseFailed(useCase, response.Error.Code, elapsedMs);
            }
            else
            {
                UseCaseRejected(useCase, response.Error.Code, response.Error.Type, elapsedMs);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away or the host is stopping: expected, not a failure of the use case.
            double elapsedMs = timeProvider.GetElapsedTime(start).TotalMilliseconds;
            UseCaseCancelled(useCase, elapsedMs);
            throw;
        }
        catch (Exception ex)
        {
            UseCaseThrew(ex, useCase, timeProvider.GetElapsedTime(start).TotalMilliseconds);
            throw;
        }
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Use case {UseCase} completed in {ElapsedMs} ms")]
    private partial void UseCaseCompleted(string useCase, double elapsedMs);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "Use case {UseCase} rejected with {ErrorCode} ({ErrorType}) in {ElapsedMs} ms")]
    private partial void UseCaseRejected(string useCase, string errorCode, ErrorType errorType, double elapsedMs);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Error, Message = "Use case {UseCase} failed with {ErrorCode} in {ElapsedMs} ms")]
    private partial void UseCaseFailed(string useCase, string errorCode, double elapsedMs);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Debug, Message = "Use case {UseCase} cancelled after {ElapsedMs} ms")]
    private partial void UseCaseCancelled(string useCase, double elapsedMs);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Error, Message = "Use case {UseCase} threw after {ElapsedMs} ms")]
    private partial void UseCaseThrew(Exception exception, string useCase, double elapsedMs);
}
