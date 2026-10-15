using Microsoft.AspNetCore.Builder;

namespace SakerWatch.BuildingBlocks.Web.Idempotency;

/// <summary>Endpoint metadata: the endpoint requires an <c>Idempotency-Key</c> header (ADR-0017). It feeds the OpenAPI document and the contract tests.</summary>
/// <param name="HeaderName">The header the key is read from.</param>
public sealed record RequiresIdempotencyKeyMetadata(string HeaderName);

/// <summary>Endpoint metadata: a mutating endpoint that is deliberately exempt from <c>Idempotency-Key</c> (for example a pure computation).</summary>
/// <param name="reason">Why the endpoint needs no key.</param>
public sealed class IdempotencyExemptMetadata(string reason)
{
    /// <summary>Why the endpoint needs no key.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Declares the idempotency contract of an endpoint.</summary>
public static class IdempotencyEndpointExtensions
{
    /// <summary>Marks the endpoint as requiring the <c>Idempotency-Key</c> header and documents the related responses.</summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequiresIdempotencyKeyMetadata(IdempotencyKeyReader.HeaderName));
    }

    /// <summary>Marks a mutating endpoint as exempt from the key, with the reason.</summary>
    public static TBuilder ExemptFromIdempotencyKey<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new IdempotencyExemptMetadata(reason));
    }
}
