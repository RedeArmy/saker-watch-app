using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;

namespace SakerWatch.Retention.Application;

/// <summary>The queued retention run. Its job key is <c>retention:{mode}:{yyyyMMdd}</c>, so a day is enqueued once however many schedulers run.</summary>
/// <param name="DryRun">Count only, delete nothing.</param>
[JobType("retention.run.v1")]
public sealed record RetentionRunJob(bool DryRun);

/// <summary>
/// Runs the retention rules for a queued job. A rerun after a lease takeover simply finds less to remove. A job can ask for a dry
/// run but never switch one off: deletion needs both the job and the configured <see cref="RetentionOptions.DryRun"/> to say so,
/// so a forged payload cannot bypass the safe default.
/// </summary>
/// <param name="runner">The rules.</param>
/// <param name="options">The retention settings.</param>
public sealed class RetentionRunJobHandler(RetentionRunner runner, IOptions<RetentionOptions> options) : IJobHandler<RetentionRunJob>
{
    /// <inheritdoc />
    public async Task HandleAsync(RetentionRunJob payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await runner.RunAsync(payload.DryRun || options.Value.DryRun, cancellationToken);
    }
}

/// <summary>Registration of the retention rules.</summary>
public static class RetentionApplicationExtensions
{
    /// <summary>Registers the runner; the job handler is found by <c>AddJobs</c> when the host passes this assembly.</summary>
    public static IServiceCollection AddRetentionApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<RetentionRunner>();
        return services;
    }
}
