using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.MarketData.Application.Providers;
using StackExchange.Redis;

namespace SakerWatch.MarketData.Infrastructure.Providers;

/// <summary>
/// <see cref="IProviderCallBudget"/> over Redis: one counter per provider and calendar minute and one per UTC day, checked and spent in a
/// single script so concurrent workers cannot overspend. Keys expire on their own. If Redis cannot be reached the call is allowed and the
/// failure is logged: the provider enforces its own limit, so the worst outcome is a rate-limit answer and not a lost watchlist.
/// </summary>
/// <param name="redis">The shared connection.</param>
/// <param name="timeProvider">Injected clock; the windows are UTC, as Twelve Data resets its quotas at 00:00 UTC.</param>
/// <param name="logger">Logger.</param>
public sealed partial class RedisProviderCallBudget(IConnectionMultiplexer redis, TimeProvider timeProvider, ILogger<RedisProviderCallBudget> logger) : IProviderCallBudget
{
    // KEYS: minute counter, day counter. ARGV: per minute, per day, seconds to the next minute, seconds to the next day, credits.
    // Returns {0, 0} when granted, {1, wait} when the minute is spent, {2, wait} when the day is spent. The day wins when both are spent.
    private const string _script = """
        local minute = tonumber(redis.call('GET', KEYS[1]) or '0')
        local day = tonumber(redis.call('GET', KEYS[2]) or '0')
        local credits = tonumber(ARGV[5])
        if day + credits > tonumber(ARGV[2]) then return {2, tonumber(ARGV[4])} end
        if minute + credits > tonumber(ARGV[1]) then return {1, tonumber(ARGV[3])} end
        redis.call('INCRBY', KEYS[1], credits)
        redis.call('EXPIRE', KEYS[1], 120)
        redis.call('INCRBY', KEYS[2], credits)
        redis.call('EXPIRE', KEYS[2], 172800)
        return {0, 0}
        """;

    /// <inheritdoc />
    public async Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(credits, 1);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = timeProvider.GetUtcNow();
        (RedisKey minuteKey, RedisKey dayKey) = CounterKeys(provider, now);
        int toNextMinute = 60 - now.Second;
        int toNextDay = (int)Math.Ceiling((now.UtcDateTime.Date.AddDays(1) - now.UtcDateTime).TotalSeconds);

        try
        {
            RedisResult result = await redis.GetDatabase().ScriptEvaluateAsync(
                _script,
                [minuteKey, dayKey],
                [limits.PerMinute, limits.PerDay, toNextMinute, toNextDay, credits]).WaitAsync(cancellationToken);
            int[] answer = (int[]?)result ?? throw new RedisException("The budget script returned nothing.");
            return answer[0] == 0 ? BudgetDecision.Allowed : new BudgetDecision(false, TimeSpan.FromSeconds(answer[1]));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogBudgetUnavailable(provider, ex.GetType().Name);
            return BudgetDecision.Allowed;
        }
    }

    // KEYS: minute counter, day counter. ARGV: credits. A counter never goes below zero; a minute that has passed since the reservation is left alone.
    private const string _releaseScript = """
        for i = 1, 2 do
            local value = tonumber(redis.call('GET', KEYS[i]) or '0')
            if value > 0 then redis.call('DECRBY', KEYS[i], math.min(value, tonumber(ARGV[1]))) end
        end
        return 0
        """;

    /// <inheritdoc />
    public async Task ReleaseAsync(string provider, int credits, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentOutOfRangeException.ThrowIfLessThan(credits, 1);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = timeProvider.GetUtcNow();
        (RedisKey minuteKey, RedisKey dayKey) = CounterKeys(provider, now);
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(_releaseScript, [minuteKey, dayKey], [credits]).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogBudgetUnavailable(provider, ex.GetType().Name);
        }
    }

    private static (RedisKey Minute, RedisKey Day) CounterKeys(string provider, DateTimeOffset now) =>
        (RedisKeys.For("budget", provider, "m", now.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)),
         RedisKeys.For("budget", provider, "d", now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)));

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "The call budget of {Provider} could not be checked ({ExceptionType}); the call goes ahead and the provider enforces its own limit")]
    private partial void LogBudgetUnavailable(string provider, string exceptionType);
}
