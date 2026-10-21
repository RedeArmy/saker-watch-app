using System.Globalization;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Transactions;

namespace SakerWatch.MarketData.Application.Lifecycle;

/// <summary>The changes to the life of a listing an operator can ask for (ADR-0039).</summary>
public enum LifecycleOperation
{
    /// <summary>The stock stopped trading.</summary>
    Delist,

    /// <summary>The stock trades again.</summary>
    Relist,

    /// <summary>The stock has a new ticker.</summary>
    Rename,
}

/// <summary>
/// An operator's request, read from the arguments of the worker: <c>delist MIC TICKER [--on yyyy-MM-dd]</c>, <c>relist MIC TICKER</c> or
/// <c>rename MIC OLD NEW [--on yyyy-MM-dd] [--provider-symbol SYMBOL]</c>.
/// </summary>
/// <param name="Operation">What to do.</param>
/// <param name="Market">The canonical market code.</param>
/// <param name="Ticker">The ticker the stock has now (for a rename, the old one).</param>
/// <param name="NewTicker">The new ticker of a rename.</param>
/// <param name="On">The day the change took effect; today when not given.</param>
/// <param name="ProviderSymbol">The provider's symbol after a rename; the new ticker when not given.</param>
public sealed record LifecycleCommand(LifecycleOperation Operation, string Market, string Ticker, string? NewTicker, DateOnly? On, string? ProviderSymbol)
{
    /// <summary>Code of the failure for a request that cannot be read.</summary>
    public const string UsageCode = "marketdata.lifecycle.usage";

    /// <summary>How to ask, for the operator.</summary>
    public const string Usage =
        "marketdata delist MIC TICKER [--on yyyy-MM-dd] | marketdata relist MIC TICKER | marketdata rename MIC OLD NEW [--on yyyy-MM-dd] [--provider-symbol SYMBOL]";

    /// <summary>Reads the arguments that follow <c>marketdata</c>.</summary>
    public static Result<LifecycleCommand> Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Result<(List<string> Positional, DateOnly? On, string? ProviderSymbol)> read = ReadOptions(args);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        (List<string> positional, DateOnly? on, string? providerSymbol) = read.Value;
        if (positional.Count == 0 || !Enum.TryParse(positional[0], ignoreCase: true, out LifecycleOperation operation) || !Enum.IsDefined(operation))
        {
            return Bad("The first argument is delist, relist or rename.");
        }

        int expected = operation == LifecycleOperation.Rename ? 4 : 3;
        if (positional.Count != expected)
        {
            return Bad($"{operation} takes {expected - 1} values.");
        }

        if (operation != LifecycleOperation.Rename && providerSymbol is not null)
        {
            return Bad("--provider-symbol is only for rename.");
        }

        if (operation == LifecycleOperation.Relist && on is not null)
        {
            return Bad("--on is not for relist.");
        }

        return new LifecycleCommand(operation, positional[1], positional[2], operation == LifecycleOperation.Rename ? positional[3] : null, on, providerSymbol);
    }

    private static Result<(List<string> Positional, DateOnly? On, string? ProviderSymbol)> ReadOptions(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        DateOnly? on = null;
        string? providerSymbol = null;
        var pending = new Queue<string>(args);
        while (pending.TryDequeue(out string? arg))
        {
            if (arg == "--on")
            {
                if (!pending.TryDequeue(out string? text) || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
                {
                    return Bad("--on needs a date as yyyy-MM-dd.");
                }

                on = day;
            }
            else if (arg == "--provider-symbol")
            {
                if (!pending.TryDequeue(out string? symbol) || symbol.StartsWith("--", StringComparison.Ordinal))
                {
                    return Bad("--provider-symbol needs a symbol.");
                }

                providerSymbol = symbol;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return Bad($"The option {arg} is unknown.");
            }
            else
            {
                positional.Add(arg);
            }
        }

        return (positional, on, providerSymbol);
    }

    private static Error Bad(string reason) => Error.Validation(UsageCode, $"{reason} {Usage}");
}

/// <summary>Runs an operator's request in one unit of work: everything it implies commits together, or nothing does.</summary>
public sealed class LifecycleCommandRunner(ListingLifecycle lifecycle, IUnitOfWork unit)
{
    /// <summary>Does what the operator asked for.</summary>
    public async Task<Result<LifecycleChange>> RunAsync(LifecycleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await unit.BeginAsync(cancellationToken);
        try
        {
            Result<LifecycleChange> result = command.Operation switch
            {
                LifecycleOperation.Delist => await lifecycle.DelistAsync(command.Market, command.Ticker, command.On, cancellationToken),
                LifecycleOperation.Relist => await lifecycle.RelistAsync(command.Market, command.Ticker, cancellationToken),
                _ => await lifecycle.RenameAsync(command.Market, command.Ticker, command.NewTicker!, command.On, command.ProviderSymbol, cancellationToken),
            };

            if (result.IsSuccess)
            {
                await unit.CommitAsync(cancellationToken);
            }
            else
            {
                await unit.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            await unit.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
