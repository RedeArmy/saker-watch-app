using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace SakerWatch.BuildingBlocks.Application.Telemetry;

/// <summary>
/// Telemetry primitives shared by the pipeline (ADR-0016). Hosts subscribe to <see cref="SourceName"/> (traces)
/// and <see cref="MeterName"/> (metrics); the names are the contract.
/// </summary>
public static class UseCaseTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> that emits one span per use case.</summary>
    public const string SourceName = "SakerWatch";

    /// <summary>Name of the <see cref="Meter"/> that owns every Saker Watch instrument.</summary>
    public const string MeterName = "SakerWatch";

    private static readonly Meter _meter = new(MeterName);
    private static readonly ConcurrentDictionary<string, Histogram<double>> _durations = new();

    /// <summary>The activity source used for use-case spans.</summary>
    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Duration histogram for a module: <c>sakerwatch_&lt;module&gt;_usecase_duration_seconds</c>.
    /// Tags are low cardinality only: <c>usecase</c>, <c>outcome</c>, <c>error_type</c>.
    /// </summary>
    /// <param name="metricModule">Lower-case module name.</param>
    public static Histogram<double> Duration(string metricModule) =>
        _durations.GetOrAdd(
            metricModule,
            static module => _meter.CreateHistogram<double>(
                $"sakerwatch_{module}_usecase_duration_seconds",
                unit: "s",
                description: "Duration of use-case executions through the application pipeline."));
}

/// <summary>Names derived once per request type: module, use case, span name and metric module.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
[SuppressMessage("Major Bug", "S2743:Static fields should not be used in generic types", Justification = "Intentional per-request-type cache: each closed type computes its own names once.")]
internal static class UseCaseInfo<TRequest>
{
    private const string _rootNamespace = "SakerWatch.";
    private const string _platformModule = "Platform";

    /// <summary>Module taken from the request namespace (<c>SakerWatch.&lt;Module&gt;.…</c>); <c>Platform</c> otherwise.</summary>
    public static readonly string Module = ResolveModule(typeof(TRequest).Namespace);

    /// <summary>Request type name, used as the use-case name.</summary>
    public static readonly string UseCase = typeof(TRequest).Name;

    /// <summary>Span name <c>&lt;Module&gt;.&lt;UseCase&gt;</c>.</summary>
    public static readonly string SpanName = $"{Module}.{UseCase}";

    /// <summary>Lower-case module for metric names.</summary>
    public static readonly string MetricModule = Module.ToLowerInvariant();

    private static string ResolveModule(string? ns)
    {
        if (ns is null || !ns.StartsWith(_rootNamespace, StringComparison.Ordinal))
        {
            return _platformModule;
        }

        string rest = ns[_rootNamespace.Length..];
        int dot = rest.IndexOf('.', StringComparison.Ordinal);
        string module = dot < 0 ? rest : rest[..dot];
        return module is "BuildingBlocks" or "" ? _platformModule : module;
    }
}
