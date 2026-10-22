namespace SakerWatch.MarketData.Application.Providers;

/// <summary>The call budget of one provider plan: how many credits per minute and per day (UTC) it allows.</summary>
/// <param name="PerMinute">Credits per calendar minute.</param>
/// <param name="PerDay">Credits per UTC day.</param>
public sealed record BudgetLimits(int PerMinute, int PerDay);

/// <summary>The answer to a request for credits.</summary>
/// <param name="Granted">True when the credits were reserved and the call may go ahead.</param>
/// <param name="RetryAfter">When denied, how long until the exhausted window ends.</param>
public sealed record BudgetDecision(bool Granted, TimeSpan RetryAfter)
{
    /// <summary>The credits were reserved.</summary>
    public static BudgetDecision Allowed { get; } = new(true, TimeSpan.Zero);
}

/// <summary>
/// Spends a provider's call budget before a call is made (ADR-0027). The budget is shared by every process that calls the provider, so
/// it lives outside the process; a call that is not granted is never sent, and the caller sees a rate-limit failure with the wait.
/// </summary>
public interface IProviderCallBudget
{
    /// <summary>Reserves <paramref name="credits"/> of <paramref name="provider"/>'s budget, or says how long to wait.</summary>
    Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken);

    /// <summary>
    /// Gives back credits reserved for a call that was then never sent (a second budget refused it). The default does nothing, for a budget
    /// that cannot take credits back.
    /// </summary>
    Task ReleaseAsync(string provider, int credits, CancellationToken cancellationToken) => Task.CompletedTask;
}
