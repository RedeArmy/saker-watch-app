namespace SakerWatch.Hosts.Migrator;

/// <summary>One ordered unit of the deploy-time migration run (a PostgreSQL context or the MongoDB runner).</summary>
public interface IMigrationStep
{
    /// <summary>Short name used in logs.</summary>
    string Name { get; }

    /// <summary>Applies everything pending. Must be safe to run again (nothing pending is a no-op).</summary>
    /// <returns>How many migrations were applied.</returns>
    Task<int> RunAsync(CancellationToken cancellationToken);
}
