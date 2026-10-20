using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Lifecycle;

namespace SakerWatch.Hosts.Worker;

/// <summary>
/// The operator's door to the life of a listing (ADR-0039): <c>SakerWatch.Hosts.Worker marketdata delist|relist|rename ...</c> runs one change in
/// one unit of work and exits, without starting the worker. The outcome goes to the log, because the host writes nothing to the console itself.
/// The exit code is 0 when the change was made or the stock was already as asked, 1 when it was refused and 2 when the request cannot be read.
/// </summary>
internal static partial class MarketDataCommandLine
{
    /// <summary>Exit code of a change that was made or was already so.</summary>
    public const int Done = 0;

    /// <summary>Exit code of a change that was refused (an unknown stock, a taken ticker).</summary>
    public const int Refused = 1;

    /// <summary>Exit code of a request that cannot be read.</summary>
    public const int Unreadable = 2;

    /// <summary>Runs the request given by the arguments that follow <c>marketdata</c>.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(arguments);

        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SakerWatch.MarketData.Lifecycle");
        Result<LifecycleCommand> command = LifecycleCommand.Parse(arguments);
        if (!command.IsSuccess)
        {
            LogUnreadable(logger, command.Error!.Message);
            return Unreadable;
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Result<LifecycleChange> result = await scope.ServiceProvider.GetRequiredService<LifecycleCommandRunner>().RunAsync(command.Value, cancellationToken);
        string operation = command.Value.Operation.ToString();
        if (!result.IsSuccess)
        {
            LogRefused(logger, operation, result.Error!.Code, result.Error.Message);
            return Refused;
        }

        LogDone(logger, operation, result.Value.Market, result.Value.Ticker, result.Value.Changed);
        return Done;
    }

    [LoggerMessage(EventId = 2604, Level = LogLevel.Information, Message = "{Operation} of {Market} {Ticker} finished (changed: {Changed})")]
    private static partial void LogDone(ILogger logger, string operation, string market, string ticker, bool changed);

    [LoggerMessage(EventId = 2605, Level = LogLevel.Warning, Message = "{Operation} was refused with {Code}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string operation, string code, string reason);

    [LoggerMessage(EventId = 2606, Level = LogLevel.Warning, Message = "The request cannot be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);
}
