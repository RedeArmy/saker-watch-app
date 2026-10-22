using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Infrastructure.Providers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class ProviderCallBudgetTests : IAsyncLifetime
{
    private static readonly BudgetLimits _freePlan = new(PerMinute: 8, PerDay: 800);

    private readonly RedisContainer _container = new RedisBuilder(ComposeImages.For("redis")).Build();
    private ConnectionMultiplexer _redis = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _container.DisposeAsync();
    }

    // 2026-10-16 17:45:20 UTC: 40 seconds to the next minute, 6 h 14 min 40 s to the next UTC day.
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 16, 17, 45, 20, TimeSpan.Zero));

    private RedisProviderCallBudget Budget(FakeTimeProvider clock) => new(_redis, clock, NullLogger<RedisProviderCallBudget>.Instance);

    [Fact]
    public async Task TheMinuteBudget_AllowsExactlyTheLimit_ThenSaysHowLongUntilTheNextMinute()
    {
        FakeTimeProvider clock = Clock();
        RedisProviderCallBudget budget = Budget(clock);

        int granted = 0;
        for (int i = 0; i < 8; i++)
        {
            granted += (await budget.TryAcquireAsync("p1", _freePlan, 1, CancellationToken.None)).Granted ? 1 : 0;
        }

        BudgetDecision ninth = await budget.TryAcquireAsync("p1", _freePlan, 1, CancellationToken.None);

        granted.Should().Be(8);
        ninth.Granted.Should().BeFalse();
        ninth.RetryAfter.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task ReleasedCredits_AreAvailableAgain_AndTheCounterNeverGoesBelowZero()
    {
        FakeTimeProvider clock = Clock();
        RedisProviderCallBudget budget = Budget(clock);
        var limits = new BudgetLimits(PerMinute: 8, PerDay: 2);
        await budget.TryAcquireAsync("p-release", limits, 2, CancellationToken.None);
        (await budget.TryAcquireAsync("p-release", limits, 1, CancellationToken.None)).Granted.Should().BeFalse();

        await budget.ReleaseAsync("p-release", 1, CancellationToken.None);
        await budget.ReleaseAsync("p-release", 5, CancellationToken.None);

        (await budget.TryAcquireAsync("p-release", limits, 2, CancellationToken.None)).Granted.Should().BeTrue("the counters stopped at zero and two credits fit again");
        (await budget.TryAcquireAsync("p-release", limits, 1, CancellationToken.None)).Granted.Should().BeFalse();
    }

    [Fact]
    public async Task TheMinuteBudget_IsRestoredWhenTheNextMinuteStarts()
    {
        FakeTimeProvider clock = Clock();
        RedisProviderCallBudget budget = Budget(clock);
        for (int i = 0; i < 8; i++)
        {
            await budget.TryAcquireAsync("p2", _freePlan, 1, CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(41));

        (await budget.TryAcquireAsync("p2", _freePlan, 1, CancellationToken.None)).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task TheDayBudget_IsSpentAcrossMinutes_AndWinsOverTheMinuteWait()
    {
        FakeTimeProvider clock = Clock();
        RedisProviderCallBudget budget = Budget(clock);
        var limits = new BudgetLimits(PerMinute: 8, PerDay: 10);
        for (int minute = 0; minute < 2; minute++)
        {
            for (int i = 0; i < (minute == 0 ? 8 : 2); i++)
            {
                (await budget.TryAcquireAsync("p3", limits, 1, CancellationToken.None)).Granted.Should().BeTrue();
            }

            clock.Advance(TimeSpan.FromMinutes(1));
        }

        BudgetDecision denied = await budget.TryAcquireAsync("p3", limits, 1, CancellationToken.None);

        denied.Granted.Should().BeFalse();
        denied.RetryAfter.Should().BeGreaterThan(TimeSpan.FromHours(5), "the day is spent, so the wait is until 00:00 UTC and not the next minute");
    }

    [Fact]
    public async Task TheDayBudget_IsRestoredAtMidnightUtc()
    {
        FakeTimeProvider clock = Clock();
        RedisProviderCallBudget budget = Budget(clock);
        var limits = new BudgetLimits(PerMinute: 100, PerDay: 3);
        for (int i = 0; i < 3; i++)
        {
            await budget.TryAcquireAsync("p4", limits, 1, CancellationToken.None);
        }

        (await budget.TryAcquireAsync("p4", limits, 1, CancellationToken.None)).Granted.Should().BeFalse();
        clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(15));

        (await budget.TryAcquireAsync("p4", limits, 1, CancellationToken.None)).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task ADeniedRequest_SpendsNothing()
    {
        FakeTimeProvider clock = Clock();
        RedisProviderCallBudget budget = Budget(clock);
        var limits = new BudgetLimits(PerMinute: 2, PerDay: 100);
        await budget.TryAcquireAsync("p5", limits, 2, CancellationToken.None);

        (await budget.TryAcquireAsync("p5", limits, 1, CancellationToken.None)).Granted.Should().BeFalse();
        (await budget.TryAcquireAsync("p5", limits, 1, CancellationToken.None)).Granted.Should().BeFalse();
        clock.Advance(TimeSpan.FromMinutes(1));

        (await budget.TryAcquireAsync("p5", limits, 2, CancellationToken.None)).Granted.Should().BeTrue("the denied attempts did not eat the next minute's credits");
    }

    [Fact]
    public async Task ARequestForMoreCreditsThanRemain_IsDeniedWhole()
    {
        RedisProviderCallBudget budget = Budget(Clock());
        var limits = new BudgetLimits(PerMinute: 5, PerDay: 100);
        await budget.TryAcquireAsync("p6", limits, 4, CancellationToken.None);

        (await budget.TryAcquireAsync("p6", limits, 2, CancellationToken.None)).Granted.Should().BeFalse();
        (await budget.TryAcquireAsync("p6", limits, 1, CancellationToken.None)).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task Providers_HaveIndependentBudgets()
    {
        RedisProviderCallBudget budget = Budget(Clock());
        var limits = new BudgetLimits(PerMinute: 1, PerDay: 100);
        await budget.TryAcquireAsync("p7", limits, 1, CancellationToken.None);

        (await budget.TryAcquireAsync("p7", limits, 1, CancellationToken.None)).Granted.Should().BeFalse();
        (await budget.TryAcquireAsync("p8", limits, 1, CancellationToken.None)).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task UnderParallelCallers_NeverMoreThanTheLimitIsGranted()
    {
        RedisProviderCallBudget budget = Budget(Clock());

        BudgetDecision[] decisions = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => budget.TryAcquireAsync("p9", _freePlan, 1, CancellationToken.None)));

        decisions.Count(d => d.Granted).Should().Be(8);
    }

    [Fact]
    public async Task TheCounters_ExpireOnTheirOwn()
    {
        FakeTimeProvider clock = Clock();
        await Budget(clock).TryAcquireAsync("p10", _freePlan, 1, CancellationToken.None);

        TimeSpan? minuteTtl = await _redis.GetDatabase().KeyTimeToLiveAsync("v1:budget:p10:m:202610161745");
        TimeSpan? dayTtl = await _redis.GetDatabase().KeyTimeToLiveAsync("v1:budget:p10:d:20261016");

        minuteTtl.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
        dayTtl.Should().BeGreaterThan(TimeSpan.FromHours(24)).And.BeLessThanOrEqualTo(TimeSpan.FromHours(48));
    }

    [Fact]
    public async Task WhenRedisCannotBeReached_TheCallIsAllowedAndTheProviderEnforcesItsOwnLimit()
    {
        await using ConnectionMultiplexer down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=300,syncTimeout=300,asyncTimeout=300");
        var budget = new RedisProviderCallBudget(down, Clock(), NullLogger<RedisProviderCallBudget>.Instance);

        BudgetDecision decision = await budget.TryAcquireAsync("p11", _freePlan, 1, CancellationToken.None);

        (decision.Granted, decision.Degraded).Should().Be((true, true), "it is let through, and says that nothing was counted so a caller with a cap of its own can count for itself");
    }

    [Fact]
    public async Task ACancelledRequest_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => Budget(Clock()).TryAcquireAsync("p12", _freePlan, 1, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
