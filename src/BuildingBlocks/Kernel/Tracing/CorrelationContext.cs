namespace SakerWatch.BuildingBlocks.Kernel.Tracing;

/// <summary>The correlation id of the current request or job, stamped on outbox messages, jobs and audit entries (ADR-0016).</summary>
public interface ICorrelationContext
{
    /// <summary>The correlation id, or <see langword="null"/> when none is known.</summary>
    string? CorrelationId { get; }
}

/// <summary>Scoped holder: the web middleware or the worker sets it once per request or message.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    /// <inheritdoc />
    public string? CorrelationId { get; set; }
}
