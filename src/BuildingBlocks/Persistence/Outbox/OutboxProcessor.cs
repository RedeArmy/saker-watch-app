using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Delivers one due outbox message at a time (ADR-0005, ADR-0017). The message is claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> and processed in a single unit of work together with every consumer's effect and its
/// inbox mark, then marked processed: parallel workers never take the same row, a crash redelivers it, and the inbox keeps
/// redelivery from repeating an effect. A failing message rolls back everything it wrote and retries with backoff and
/// jitter until <see cref="OutboxOptions.MaxAttempts"/>, after which it is parked as dead for a human.
/// </summary>
public sealed partial class OutboxProcessor
{
    private static readonly Meter _meter = new("SakerWatch");
    private static readonly Counter<long> _messages = _meter.CreateCounter<long>(
        "sakerwatch_outbox_messages_total",
        description: "Outbox messages by delivery outcome (published, retry, dead).");
    private static readonly ActivitySource _activities = new("SakerWatch");
    private static readonly ConcurrentDictionary<Type, IEventDeliverer> _deliverers = new();

    private readonly IServiceScopeFactory _scopes;
    private readonly IntegrationEventRegistry _registry;
    private readonly OutboxOptions _options;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ExponentialBackoff _backoff;
    private readonly ILogger<OutboxProcessor> _logger;

    /// <summary>Creates a processor.</summary>
    public OutboxProcessor(
        IServiceScopeFactory scopes,
        IntegrationEventRegistry registry,
        IOptions<OutboxOptions> options,
        IOptions<PostgresOptions> postgres,
        TimeProvider timeProvider,
        ExponentialBackoff backoff,
        ILogger<OutboxProcessor> logger)
    {
        _scopes = scopes;
        _registry = registry;
        _options = options.Value;
        _connectionString = postgres.Value.Postgres;
        _timeProvider = timeProvider;
        _backoff = backoff;
        _logger = logger;
    }

    /// <summary>Claims and delivers the next due message. Returns <see langword="false"/> when none is due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        // The usual poll finds nothing: answer that with one cheap read, without a scope, a transaction and a locking claim.
        if (!await AnythingDueAsync(cancellationToken))
        {
            return false;
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await session.BeginAsync(cancellationToken);

        ClaimedMessage? claimed = await ClaimAsync(session, cancellationToken);
        if (claimed is null)
        {
            await session.CommitAsync(cancellationToken);
            return false;
        }

        using Activity? activity = _activities.StartActivity($"Outbox.Deliver {claimed.Type}");
        activity?.SetTag("saker.outbox.type", claimed.Type);
        activity?.SetTag("saker.origin_trace_id", claimed.TraceId);
        try
        {
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = claimed.CorrelationId;
            IDomainEvent message = _registry.Deserialize(claimed.Type, claimed.Payload);
            await DeliverAsync(scope.ServiceProvider, message, cancellationToken);
            await MarkProcessedAsync(session, claimed.Id, cancellationToken);
            await session.CommitAsync(cancellationToken);

            _messages.Add(1, new KeyValuePair<string, object?>("outcome", "published"));
            Delivered(claimed.Type, claimed.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: roll back and leave the message pending for the next worker.
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await session.RollbackAsync(CancellationToken.None);
            await RecordFailureAsync(claimed, ex, cancellationToken);
        }

        return true;
    }

    private async Task<bool> AnythingDueAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var peek = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.outbox_messages WHERE processed_at IS NULL AND next_attempt_at <= @now AND attempts < @max)",
            connection);
        peek.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
        peek.Parameters.AddWithValue("max", _options.MaxAttempts);
        return (bool)(await peek.ExecuteScalarAsync(cancellationToken))!;
    }

    private static Task DeliverAsync(IServiceProvider services, IDomainEvent message, CancellationToken cancellationToken) =>
        _deliverers.GetOrAdd(message.GetType(), static type => (IEventDeliverer)Activator.CreateInstance(typeof(EventDeliverer<>).MakeGenericType(type))!)
            .RunAsync(services, message, cancellationToken);

    // One typed deliverer per event type, created once: the consumers are resolved and called through their generic interface, with no
    // reflection per message and no TargetInvocationException wrapping a consumer's own failure.
    private interface IEventDeliverer
    {
        Task RunAsync(IServiceProvider services, IDomainEvent message, CancellationToken cancellationToken);
    }

    private sealed class EventDeliverer<TEvent> : IEventDeliverer
        where TEvent : IDomainEvent
    {
        public async Task RunAsync(IServiceProvider services, IDomainEvent message, CancellationToken cancellationToken)
        {
            var inbox = services.GetRequiredService<IInbox>();
            var typed = (TEvent)message;

            foreach (IIntegrationEventConsumer<TEvent> consumer in services.GetServices<IIntegrationEventConsumer<TEvent>>())
            {
                // Redelivery after a crash must not repeat this consumer's effect.
                if (!await inbox.TryBeginAsync(consumer.GetType().FullName!, message.EventId, cancellationToken))
                {
                    continue;
                }

                await consumer.ConsumeAsync(typed, cancellationToken);
            }
        }
    }

    private async Task<ClaimedMessage?> ClaimAsync(DbSession session, CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand(
            """
            SELECT id, type, payload::text, trace_id, correlation_id, attempts
            FROM platform.outbox_messages
            WHERE processed_at IS NULL AND next_attempt_at <= @now AND attempts < @max
            ORDER BY next_attempt_at, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """,
            session.Connection,
            session.Transaction);
        select.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
        select.Parameters.AddWithValue("max", _options.MaxAttempts);
        await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedMessage(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5));
    }

    private async Task MarkProcessedAsync(DbSession session, Guid id, CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            "UPDATE platform.outbox_messages SET processed_at = @now, last_error = NULL WHERE id = @id",
            session.Connection,
            session.Transaction);
        update.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    // A parked message gets next_attempt_at = infinity: the claim query orders by that column through a partial index, so parked rows
    // (which are never purged automatically) sit at the end of the index instead of being walked past on every poll.
    // The failure is written on its own connection, after the failed unit of work rolled back. It only applies while the attempt
    // count is still the one this worker claimed, so another worker that took the message in between is not overwritten.
    private async Task RecordFailureAsync(ClaimedMessage message, Exception exception, CancellationToken cancellationToken)
    {
        int attempts = message.Attempts + 1;
        bool dead = attempts >= _options.MaxAttempts;
        DateTimeOffset next = _timeProvider.GetUtcNow() + _backoff.Delay(attempts);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var update = new NpgsqlCommand(
            """
            UPDATE platform.outbox_messages
            SET attempts = @attempts, next_attempt_at = CASE WHEN @dead THEN 'infinity'::timestamptz ELSE @next END, last_error = @error
            WHERE id = @id AND processed_at IS NULL AND attempts = @expected
            """,
            connection);
        update.Parameters.AddWithValue("attempts", attempts);
        update.Parameters.AddWithValue("dead", dead);
        update.Parameters.AddWithValue("next", next);
        update.Parameters.AddWithValue("error", ErrorText.Describe(exception));
        update.Parameters.AddWithValue("id", message.Id);
        update.Parameters.AddWithValue("expected", message.Attempts);
        await update.ExecuteNonQueryAsync(cancellationToken);

        if (dead)
        {
            _messages.Add(1, new KeyValuePair<string, object?>("outcome", "dead"));
            Parked(exception, message.Type, message.Id, attempts);
        }
        else
        {
            _messages.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
            WillRetry(exception, message.Type, message.Id, attempts, next.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    [LoggerMessage(EventId = 9501, Level = LogLevel.Debug, Message = "Outbox message {Type} {MessageId} delivered")]
    private partial void Delivered(string type, Guid messageId);

    [LoggerMessage(EventId = 9502, Level = LogLevel.Warning, Message = "Outbox message {Type} {MessageId} failed on attempt {Attempt}; retrying at {NextAttemptAt}")]
    private partial void WillRetry(Exception exception, string type, Guid messageId, int attempt, string nextAttemptAt);

    [LoggerMessage(EventId = 9503, Level = LogLevel.Error, Message = "Outbox message {Type} {MessageId} parked as dead after {Attempts} attempts")]
    private partial void Parked(Exception exception, string type, Guid messageId, int attempts);

    private sealed record ClaimedMessage(Guid Id, string Type, string Payload, string? TraceId, string? CorrelationId, int Attempts);
}
