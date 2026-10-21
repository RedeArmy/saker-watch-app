using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Application.Lifecycle;

/// <summary>
/// The operator's door to the life of a listing (ADR-0039): reads the arguments that follow <c>marketdata</c>, runs the change in one unit of work
/// and turns the outcome into an exit code. The host writes nothing to the console, so the outcome goes to the log. Whatever goes wrong inside is
/// caught and reported (exit code 3), so an operator always gets an answer and never a stack trace from the process.
/// </summary>
public sealed partial class LifecycleCommandLine(LifecycleCommandRunner runner, ILogger<LifecycleCommandLine> logger)
{
    /// <summary>Exit code of a change that was made or was already so.</summary>
    public const int Done = 0;

    /// <summary>Exit code of a change that was refused (an unknown stock, a taken ticker).</summary>
    public const int Refused = 1;

    /// <summary>Exit code of a request that cannot be read.</summary>
    public const int Unreadable = 2;

    /// <summary>Exit code of a change that broke (the database was down, a constraint nobody foresaw); nothing was changed.</summary>
    public const int Failed = 3;

    /// <summary>Runs the request given by the arguments that follow <c>marketdata</c>.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        Result<LifecycleCommand> command = LifecycleCommand.Parse(arguments);
        if (!command.IsSuccess)
        {
            LogUnreadable(command.Error!.Message);
            return Unreadable;
        }

        string operation = command.Value.Operation.ToString();
        try
        {
            Result<LifecycleChange> result = await runner.RunAsync(command.Value, cancellationToken);
            if (!result.IsSuccess)
            {
                LogRefused(operation, result.Error!.Code, result.Error.Message);
                return Refused;
            }

            LogDone(operation, result.Value.Market, result.Value.Ticker, result.Value.Changed);
            return Done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, operation);
            return Failed;
        }
    }

    [LoggerMessage(EventId = 2604, Level = LogLevel.Information, Message = "{Operation} of {Market} {Ticker} finished (changed: {Changed})")]
    private partial void LogDone(string operation, string market, string ticker, bool changed);

    [LoggerMessage(EventId = 2605, Level = LogLevel.Warning, Message = "{Operation} was refused with {Code}: {Reason}")]
    private partial void LogRefused(string operation, string code, string reason);

    [LoggerMessage(EventId = 2606, Level = LogLevel.Warning, Message = "The request cannot be read: {Reason}")]
    private partial void LogUnreadable(string reason);

    [LoggerMessage(EventId = 2607, Level = LogLevel.Error, Message = "{Operation} failed and nothing was changed")]
    private partial void LogFailed(Exception exception, string operation);
}
