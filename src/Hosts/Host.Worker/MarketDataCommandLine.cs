using SakerWatch.MarketData.Application.Lifecycle;

namespace SakerWatch.Hosts.Worker;

/// <summary>
/// <c>SakerWatch.Hosts.Worker marketdata delist|relist|rename ...</c> runs one change to the life of a listing and exits without starting the
/// worker (ADR-0039, <c>docs/runbooks/listing-lifecycle.md</c>). The reading, the running and the outcome are <see cref="LifecycleCommandLine"/>;
/// this only gives it a scope of the host. The exit code is 0 done, 1 refused, 2 not understood, 3 failed.
/// </summary>
internal static class MarketDataCommandLine
{
    /// <summary>Runs the request given by the arguments that follow <c>marketdata</c>.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(arguments);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LifecycleCommandLine>().RunAsync(arguments, cancellationToken);
    }
}
