using Microsoft.Extensions.Logging;

namespace SakerWatch.Hosts.Migrator;

/// <summary>Runs the migration steps in order and stops at the first failure (forward-only, ADR-0004).</summary>
/// <param name="steps">Ordered steps; PostgreSQL platform first, because it creates the roles the others grant to.</param>
/// <param name="logger">Pipeline logger.</param>
public sealed partial class MigrationPipeline(IEnumerable<IMigrationStep> steps, ILogger<MigrationPipeline> logger)
{
    private readonly IMigrationStep[] _steps = [.. steps];

    /// <summary>Runs every step. Throws on the first failure so the deploy step fails loudly.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        MigrationRunStarted(_steps.Length);
        foreach (IMigrationStep step in _steps)
        {
            try
            {
                int applied = await step.RunAsync(cancellationToken);
                MigrationStepCompleted(step.Name, applied);
            }
            catch (Exception ex)
            {
                MigrationStepFailed(ex, step.Name);
                throw;
            }
        }
    }

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Migration run started with {StepCount} steps")]
    private partial void MigrationRunStarted(int stepCount);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Migration step {Step} completed, {Applied} migrations applied")]
    private partial void MigrationStepCompleted(string step, int applied);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Error, Message = "Migration step {Step} failed")]
    private partial void MigrationStepFailed(Exception exception, string step);
}
