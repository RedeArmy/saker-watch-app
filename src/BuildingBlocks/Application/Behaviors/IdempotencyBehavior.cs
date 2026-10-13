using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Application.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.BuildingBlocks.Application.Behaviors;

/// <summary>
/// Makes commands that carry an <c>Idempotency-Key</c> (<see cref="IIdempotentRequest"/>) exactly-once (ADR-0017, ADR-0022).
/// It runs inside the unit of work: the key is recorded in the same transaction as the effect, so a crash before commit
/// leaves neither, and a repeated key replays the stored result without running the handler again.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The result type.</typeparam>
/// <param name="store">Key storage bound to the active unit of work.</param>
/// <param name="currentUser">The validated caller; keys are scoped by user.</param>
/// <param name="context">Receives the replay flag for the web layer.</param>
/// <param name="options">Key retention.</param>
/// <param name="timeProvider">Injected clock.</param>
/// <param name="logger">Behavior logger.</param>
public sealed partial class IdempotencyBehavior<TRequest, TResponse>(
    IIdempotencyStore store,
    ICurrentUser currentUser,
    IdempotencyContext context,
    IOptions<IdempotencyOptions> options,
    TimeProvider timeProvider,
    ILogger<IdempotencyBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    private const int _snapshotStatus = 200;

    private static readonly Counter<long> _events = UseCaseTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_idempotency_events_total",
        description: "Idempotent command attempts by outcome (claimed, replay, mismatch, conflict).");

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IIdempotentRequest idempotent)
        {
            return await next();
        }

        IdempotencyKeyInfo key = idempotent.IdempotencyKey;
        if (!IdempotencyKeyInfo.IsValidKey(key.Key))
        {
            return TResponse.FromError(Error.Validation(IdempotencyErrors.KeyInvalid, "Idempotency-Key must be 16-64 characters of letters, digits, '-' or '_'."));
        }

        if (currentUser.UserId is not { } userId)
        {
            return TResponse.FromError(Error.Unauthorized(IdempotencyErrors.Unauthenticated, "An idempotent command requires an authenticated user."));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var claimRequest = new IdempotencyClaimRequest(
            userId, key, RequestHasher.Hash(request, key), now, now + options.Value.Retention, Activity.Current?.TraceId.ToString());

        switch (await store.ClaimAsync(claimRequest, cancellationToken))
        {
            case IdempotencyClaim.Replay replay:
                return Replay(replay.Response);

            case IdempotencyClaim.Mismatch:
                Record("mismatch");
                KeyReused(key.Method, key.Route);
                return TResponse.FromError(Error.Unprocessable(IdempotencyErrors.KeyReuse, "This Idempotency-Key was already used with a different request."));

            case IdempotencyClaim.Busy:
                Record("conflict");
                KeyBusy(key.Method, key.Route);
                return TResponse.FromError(Error.Conflict(IdempotencyErrors.InProgress, "A request with this Idempotency-Key is still being processed; retry shortly."));

            default:
                Record("claimed");
                return await ExecuteAsync(next, userId, key, cancellationToken);
        }
    }

    private async ValueTask<TResponse> ExecuteAsync(PipelineNext<TResponse> next, Guid userId, IdempotencyKeyInfo key, CancellationToken cancellationToken)
    {
        TResponse response = await next();
        if (response.IsSuccess)
        {
            // A failure result is not stored: the unit of work rolls back and the key disappears with the effect.
            await store.CompleteAsync(new IdempotencyCompletion(userId, key, _snapshotStatus, ResultSnapshot.Serialize(response)), cancellationToken);
        }

        return response;
    }

    private TResponse Replay(StoredResponse stored)
    {
        Record("replay");
        context.Replayed = true;
        Replayed();

        if (stored.Body is null)
        {
            return TResponse.FromError(Error.Conflict(IdempotencyErrors.ReplayUnavailable, "The first response was too large to replay; fetch the resource with a GET."));
        }

        return ResultSnapshot.Restore<TResponse>(stored.Body);
    }

    private static void Record(string outcome) =>
        _events.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    [LoggerMessage(EventId = 9411, Level = LogLevel.Information, Message = "Idempotent request replayed from the stored first attempt")]
    private partial void Replayed();

    [LoggerMessage(EventId = 9412, Level = LogLevel.Warning, Message = "Idempotency-Key reused with a different request on {Method} {Route}")]
    private partial void KeyReused(string method, string route);

    [LoggerMessage(EventId = 9413, Level = LogLevel.Warning, Message = "Idempotency-Key still in progress on {Method} {Route}")]
    private partial void KeyBusy(string method, string route);
}
