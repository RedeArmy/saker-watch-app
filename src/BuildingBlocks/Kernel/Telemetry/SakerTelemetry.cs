using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SakerWatch.BuildingBlocks.Kernel.Telemetry;

/// <summary>
/// The one name, meter and activity source of Saker Watch's own telemetry (ADR-0016). The hosts subscribe to <see cref="Name"/> for
/// traces and metrics, so an instrument or span created under any other name is silently never exported; creating them anywhere else is
/// banned in <c>src/**</c> for that reason.
/// </summary>
public static class SakerTelemetry
{
    /// <summary>Name of the meter and of the activity source; the hosts subscribe to it.</summary>
    public const string Name = "SakerWatch";

    // Stamped on the instrumentation scope of every metric and span, so a backend can tell which build produced them.
    private static readonly string _version = typeof(SakerTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>The shared meter: static counters and histograms are created on it once.</summary>
    public static Meter Meter { get; } = CreateMeter();

    /// <summary>The shared activity source: one span per use case, job run and outbox delivery.</summary>
    public static ActivitySource Source { get; } = new(Name, _version);

    /// <summary>
    /// A new meter with the product's name, for a component that owns instruments it must be able to remove (observable gauges that
    /// belong to an object with a lifetime). Dispose it with that object.
    /// </summary>
    public static Meter CreateMeter() => new(Name, _version);
}
