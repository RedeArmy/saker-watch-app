using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// The registrations of new stocks one user may make in a UTC day (ADR-0043, ADR-0047). The count is the shared Redis budget under a key of the user,
/// so it holds across replicas. When Redis cannot be reached the shared budget lets the call through uncounted, and the quota then counts for itself in
/// <see cref="RegistrationFallback"/>: each process holds its share of the cap (<see cref="StockLookupOptions.ApiReplicas"/>), so together they cannot pass it
/// and a user cannot drain the provider's credits while Redis is down.
/// A slot taken for a registration that did not create a stock (it already existed, or conflicted) is given back. A call with no user (a job) is not limited.
/// </summary>
/// <param name="budget">The shared budget.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="lookup">The daily cap.</param>
/// <param name="fallback">The count kept by this process for when the shared budget is down.</param>
/// <param name="clock">The clock; the day is the UTC one.</param>
public sealed class RegistrationQuota(IProviderCallBudget budget, ICurrentUser currentUser, IOptions<StockLookupOptions> lookup, RegistrationFallback fallback, TimeProvider clock)
{
    private static readonly Counter<long> _decisions = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_registration_quota_total",
        description: "Decisions on a user's registration of a new stock, by where the count was kept (shared: Redis; process: the fallback while Redis is down) and outcome (granted, refused).");

    private bool _tookFromFallback;
    private bool _holdsSlot;

    /// <summary>Takes one of the caller's registrations of the day, or says how long until they come back.</summary>
    public async Task<Error?> ReserveAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } user)
        {
            return null;
        }

        int cap = lookup.Value.RegistrationsPerUserPerDay;
        BudgetDecision decision = await budget.TryAcquireAsync(BudgetKey(user), new BudgetLimits(cap, cap), 1, cancellationToken);
        string source = "shared";
        if (decision.Granted && decision.Degraded)
        {
            // Each of the processes holds its share of the cap, so that together they cannot pass it.
            source = "process";
            decision = fallback.TryTake(user, Math.Max(1, cap / lookup.Value.ApiReplicas), clock.GetUtcNow());
            _tookFromFallback = decision.Granted;
        }

        _decisions.Add(1, new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("outcome", decision.Granted ? "granted" : "refused"));

        _holdsSlot = decision.Granted;
        return decision.Granted
            ? null
            : Error.TooManyRequests(
                StockDirectory.RegistrationLimitCode,
                "You have added the most new stocks allowed for today. Stocks that are already in the catalog can still be added; try again tomorrow.",
                (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
    }

    /// <summary>Gives back the slot taken by <see cref="ReserveAsync"/> when the registration did not create a stock. Nothing happens if none is held.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (!_holdsSlot || currentUser.UserId is not { } user)
        {
            return;
        }

        _holdsSlot = false;
        if (_tookFromFallback)
        {
            fallback.Release(user, clock.GetUtcNow());
            _tookFromFallback = false;
            return;
        }

        await budget.ReleaseAsync(BudgetKey(user), 1, cancellationToken);
    }

    /// <summary>The budget key of a user's registrations: letters, digits and underscores, as a Redis key segment needs.</summary>
    public static string BudgetKey(Guid user) => "register_" + user.ToString("N");
}

/// <summary>
/// The registrations each user made today as this process counts them, used only while the shared budget is unreachable (ADR-0047). The count is per
/// process and each process is given its share of the cap, so the processes together stay within it; a day later every count is forgotten.
/// </summary>
public sealed class RegistrationFallback
{
    private const int _pruneAbove = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (DateOnly Day, int Count)> _counts = [];

    /// <summary>Takes one registration of <paramref name="user"/> for the UTC day of <paramref name="now"/>, or says how long until the day ends.</summary>
    public BudgetDecision TryTake(Guid user, int cap, DateTimeOffset now)
    {
        DateOnly day = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_gate)
        {
            if (_counts.Count > _pruneAbove)
            {
                foreach (Guid old in _counts.Where(entry => entry.Value.Day != day).Select(entry => entry.Key).ToList())
                {
                    _counts.Remove(old);
                }
            }

            int used = _counts.TryGetValue(user, out (DateOnly Day, int Count) known) && known.Day == day ? known.Count : 0;
            if (used >= cap)
            {
                return new BudgetDecision(false, now.UtcDateTime.Date.AddDays(1) - now.UtcDateTime);
            }

            _counts[user] = (day, used + 1);
            return BudgetDecision.Allowed;
        }
    }

    /// <summary>Gives back one registration of <paramref name="user"/> for the UTC day of <paramref name="now"/>.</summary>
    public void Release(Guid user, DateTimeOffset now)
    {
        DateOnly day = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_gate)
        {
            if (_counts.TryGetValue(user, out (DateOnly Day, int Count) known) && known.Day == day && known.Count > 0)
            {
                _counts[user] = (day, known.Count - 1);
            }
        }
    }
}

/// <summary>
/// Puts a validated stock in the catalog and queues its first load (ADR-0032). The registry returns the id of whichever row holds the market and
/// ticker, so parallel registrations end with one stock; the job key is derived from that id, so they also end with one job. The two writes are one
/// short unit of work of their own, so the caller need not hold a transaction while the provider was asked (ADR-0044).
/// </summary>
public sealed partial class StockRegistration(
    IStockRegistry registry,
    IJobEnqueuer jobs,
    IOptions<IngestionOptions> ingestion,
    RegistrationQuota quota,
    IUnitOfWork unit,
    ILogger<StockRegistration> logger)
{
    /// <summary>Takes one of the caller's registrations of the day (<see cref="RegistrationQuota"/>).</summary>
    public Task<Error?> ReserveAsync(CancellationToken cancellationToken) => quota.ReserveAsync(cancellationToken);

    /// <summary>Gives back the registration taken by <see cref="ReserveAsync"/> when no stock was created (<see cref="RegistrationQuota.ReleaseAsync"/>).</summary>
    public Task ReleaseAsync(CancellationToken cancellationToken) => quota.ReleaseAsync(cancellationToken);

    /// <summary>Registers <paramref name="stock"/> and queues its history; returns the id of the stock that holds its market and ticker.</summary>
    public async Task<StockId> RegisterAsync(Stock stock, string provider, ProviderListingKey providerKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stock);
        await unit.BeginAsync(cancellationToken);
        try
        {
            StockId id = await registry.RegisterAsync(stock, provider, providerKey, now, cancellationToken);
            await jobs.EnqueueAsync(
                BackfillStockJob.KeyFor(id),
                new BackfillStockJob(id.Value),
                new JobEnqueueOptions(MaxAttempts: ingestion.Value.BackfillMaxAttempts),
                cancellationToken);
            await unit.CommitAsync(cancellationToken);
            LogRegistered(stock.Market.Value);
            return id;
        }
        catch
        {
            await unit.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    [LoggerMessage(EventId = 2501, Level = LogLevel.Information, Message = "A stock of {Market} was registered in the catalog and its history queued")]
    private partial void LogRegistered(string market);
}
