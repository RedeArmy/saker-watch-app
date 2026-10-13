using SakerWatch.BuildingBlocks.Application.Messaging;

namespace SakerWatch.BuildingBlocks.Application.Idempotency;

/// <summary>
/// The identity of one retryable request: the client's key plus the HTTP method and route template it was sent to
/// (ADR-0017 scopes keys by user, method, route and key). It is never part of the request hash.
/// </summary>
/// <param name="Key">Client-generated key, 16-64 characters of <c>[A-Za-z0-9_-]</c>.</param>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="Route">Route template, for example <c>/v1/watchlist/items</c>.</param>
public sealed record IdempotencyKeyInfo(string Key, string Method, string Route)
{
    /// <summary>Shortest accepted key.</summary>
    public const int MinLength = 16;

    /// <summary>Longest accepted key.</summary>
    public const int MaxLength = 64;

    /// <summary>True when <paramref name="key"/> has the allowed length and alphabet.</summary>
    public static bool IsValidKey(string? key) =>
        key is { Length: >= MinLength and <= MaxLength } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// A command that can be retried safely. The pipeline records its key with the effect in one transaction and replays
/// the stored result for a repeated key (ADR-0017). Create it from the <c>Idempotency-Key</c> header at the endpoint.
/// </summary>
public interface IIdempotentRequest : ITransactionalRequest
{
    /// <summary>The key and scope of this attempt.</summary>
    IdempotencyKeyInfo IdempotencyKey { get; }
}

/// <summary>Stable error codes of the idempotency contract.</summary>
public static class IdempotencyErrors
{
    /// <summary>The key is missing where it is required (400).</summary>
    public const string KeyRequired = "idempotency.key_required";

    /// <summary>The key is not 16-64 characters of letters, digits, '-' or '_' (400).</summary>
    public const string KeyInvalid = "idempotency.key_invalid";

    /// <summary>The key was already used with a different request (422).</summary>
    public const string KeyReuse = "idempotency.key_reuse";

    /// <summary>The same key is still being processed by another request (409, retry shortly).</summary>
    public const string InProgress = "idempotency.in_progress";

    /// <summary>The first response was too large to store; fetch the resource with a GET (409).</summary>
    public const string ReplayUnavailable = "idempotency.replay_unavailable";

    /// <summary>An idempotent command needs an authenticated user (401).</summary>
    public const string Unauthenticated = "idempotency.unauthenticated";
}

/// <summary>Tells the web layer whether the current response was replayed, so it can add <c>Idempotent-Replayed: true</c>.</summary>
public interface IIdempotencyContext
{
    /// <summary>True when the result came from a stored first attempt.</summary>
    bool Replayed { get; }
}

/// <summary>Scoped holder written by the idempotency behavior.</summary>
public sealed class IdempotencyContext : IIdempotencyContext
{
    /// <inheritdoc />
    public bool Replayed { get; internal set; }
}

/// <summary>What the store needs to claim a key.</summary>
/// <param name="UserId">Owner of the key.</param>
/// <param name="Key">Key and scope.</param>
/// <param name="RequestHash">SHA-256 of the canonical request.</param>
/// <param name="Now">Current time (injected clock).</param>
/// <param name="ExpiresAt">When the key may be purged.</param>
/// <param name="TraceId">Trace of the first attempt, for diagnostics.</param>
public sealed record IdempotencyClaimRequest(Guid UserId, IdempotencyKeyInfo Key, byte[] RequestHash, DateTimeOffset Now, DateTimeOffset ExpiresAt, string? TraceId);

/// <summary>A stored first response.</summary>
/// <param name="Status">Status recorded with the snapshot.</param>
/// <param name="Body">Serialized result, or <see langword="null"/> when it was too large to store.</param>
public sealed record StoredResponse(int Status, byte[]? Body);

/// <summary>What to record when the first attempt succeeds.</summary>
/// <param name="UserId">Owner of the key.</param>
/// <param name="Key">Key and scope.</param>
/// <param name="Status">Status to store.</param>
/// <param name="Body">Serialized result, or <see langword="null"/> when too large.</param>
public sealed record IdempotencyCompletion(Guid UserId, IdempotencyKeyInfo Key, int Status, byte[]? Body);

/// <summary>Outcome of claiming a key.</summary>
public abstract record IdempotencyClaim
{
    private IdempotencyClaim()
    {
    }

    /// <summary>This request owns the key: run the handler.</summary>
    public sealed record Claimed : IdempotencyClaim;

    /// <summary>A completed first attempt exists: replay it.</summary>
    /// <param name="Response">The stored response.</param>
    public sealed record Replay(StoredResponse Response) : IdempotencyClaim;

    /// <summary>The key was used with a different request.</summary>
    public sealed record Mismatch : IdempotencyClaim;

    /// <summary>Another request holds the key and did not finish in time.</summary>
    public sealed record Busy : IdempotencyClaim;
}

/// <summary>Storage of idempotency keys. It must run inside the active unit of work so the key and the effect commit together.</summary>
public interface IIdempotencyStore
{
    /// <summary>Records the key for this attempt, or reports how an earlier attempt ended.</summary>
    Task<IdempotencyClaim> ClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken);

    /// <summary>Stores the successful result against the key claimed in this unit of work.</summary>
    Task CompleteAsync(IdempotencyCompletion completion, CancellationToken cancellationToken);
}
