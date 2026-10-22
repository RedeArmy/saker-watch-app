using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// Puts a validated stock in the catalog and queues its first load (ADR-0032). The registry returns the id of whichever row holds the market and
/// ticker, so parallel registrations end with one stock; the job key is derived from that id, so they also end with one job.
/// </summary>
public sealed partial class StockRegistration(
    IStockRegistry registry,
    IJobEnqueuer jobs,
    IOptions<IngestionOptions> ingestion,
    IProviderCallBudget budget,
    ICurrentUser currentUser,
    IOptions<StockLookupOptions> lookup,
    ILogger<StockRegistration> logger)
{
    /// <summary>
    /// Takes one of the caller's registrations of the day (ADR-0043), or says how long until they come back. The count is the shared Redis budget
    /// under a key of the user, so it holds across replicas, and like the other budgets it lets the call through when Redis is unreachable. A call
    /// with no user (a job) is not limited here.
    /// </summary>
    public async Task<Error?> ReserveAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } user)
        {
            return null;
        }

        int cap = lookup.Value.RegistrationsPerUserPerDay;
        BudgetDecision decision = await budget.TryAcquireAsync(RegistrationBudgetKey(user), new BudgetLimits(cap, cap), 1, cancellationToken);
        return decision.Granted
            ? null
            : Error.TooManyRequests(
                StockDirectory.RegistrationLimitCode,
                "You have added the most new stocks allowed for today. Stocks that are already in the catalog can still be added; try again tomorrow.",
                (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
    }

    /// <summary>The budget key of a user's registrations: letters, digits and underscores, as a Redis key segment needs.</summary>
    public static string RegistrationBudgetKey(Guid user) => "register_" + user.ToString("N");

    /// <summary>Registers <paramref name="stock"/> and queues its history; returns the id of the stock that holds its market and ticker.</summary>
    public async Task<StockId> RegisterAsync(Stock stock, string provider, ProviderListingKey providerKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stock);
        StockId id = await registry.RegisterAsync(stock, provider, providerKey, now, cancellationToken);
        await jobs.EnqueueAsync(
            BackfillStockJob.KeyFor(id),
            new BackfillStockJob(id.Value),
            new JobEnqueueOptions(MaxAttempts: ingestion.Value.BackfillMaxAttempts),
            cancellationToken);
        LogRegistered(stock.Market.Value);
        return id;
    }

    [LoggerMessage(EventId = 2501, Level = LogLevel.Information, Message = "A stock of {Market} was registered in the catalog and its history queued")]
    private partial void LogRegistered(string market);
}
