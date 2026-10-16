namespace SakerWatch.BuildingBlocks.Kernel.Health;

/// <summary>Defaults shared by the readiness checks of every datastore.</summary>
public static class ReadinessDefaults
{
    /// <summary>
    /// How long one datastore check may take before it is reported unhealthy. Orchestrators give a readiness probe a few seconds, and
    /// the driver defaults (five seconds for a MongoDB server selection or a Redis connect) are longer than that.
    /// </summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);
}
