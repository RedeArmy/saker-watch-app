using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Deployment;
using SakerWatch.BuildingBlocks.Web.Deployment;
using SakerWatch.BuildingBlocks.Web.RateLimiting;
using SakerWatch.Integration.Tests.Infrastructure;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>The rate limiters that count in Redis, so that several API processes count a caller together, and how the processes count each other (ADR-0048).</summary>
[Trait("Category", "Integration")]
public sealed class RedisRateLimiterTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero); // a window of 60 s starts at :00

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

    // Counts in Redis, on the clock of the test (the production limiter reads the clock of Redis).
    private CallerRateLimiter Limiter(FakeTimeProvider clock, string scope, string caller, int limit, bool inRedis = true) =>
        new(_redis, () => inRedis, scope, caller, limit, TimeSpan.FromSeconds(60), clock, redisClock: false);

    private static async Task<bool> TakeAsync(RateLimiter limiter, int permits = 1) => (await limiter.AcquireAsync(permits)).IsAcquired;

    [Fact]
    public async Task TwoProcesses_CountTheSameCallerTogether()
    {
        var clock = new FakeTimeProvider(_start.AddSeconds(10));
        using CallerRateLimiter first = Limiter(clock, "global", "user:ana", limit: 3);
        using CallerRateLimiter second = Limiter(clock, "global", "user:ana", limit: 3);

        bool[] results = [await TakeAsync(first), await TakeAsync(second), await TakeAsync(first), await TakeAsync(second), await TakeAsync(first)];

        results.Should().Equal(true, true, true, false, false);
    }

    [Fact]
    public async Task ADeniedRequest_SaysHowLongToWait()
    {
        var clock = new FakeTimeProvider(_start.AddSeconds(20));
        using CallerRateLimiter limiter = Limiter(clock, "global", "user:wait", limit: 1);
        await TakeAsync(limiter);

        RateLimitLease denied = await limiter.AcquireAsync(1);

        denied.IsAcquired.Should().BeFalse();
        denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task RedisAndMemory_GiveTheSameAnswersToTheSameRequestsAtTheSameTimes()
    {
        var clock = new FakeTimeProvider(_start.AddSeconds(59));
        using CallerRateLimiter inRedis = Limiter(clock, "global", "user:parity", limit: 10);
        using CallerRateLimiter inMemory = Limiter(clock, "global", "user:parity-memory", limit: 10, inRedis: false);
        var redisAnswers = new List<bool>();
        var memoryAnswers = new List<bool>();

        async Task AskBothAsync(int times)
        {
            for (int i = 0; i < times; i++)
            {
                redisAnswers.Add(await TakeAsync(inRedis));
                memoryAnswers.Add(await TakeAsync(inMemory));
            }
        }

        await AskBothAsync(11); // ten fit, the eleventh does not
        clock.Advance(TimeSpan.FromSeconds(2)); // a second into the next window: the old one still weighs almost all of its ten
        await AskBothAsync(1);
        clock.Advance(TimeSpan.FromSeconds(29)); // half of the old window left: five permits
        await AskBothAsync(6);
        clock.Advance(TimeSpan.FromMinutes(3)); // nothing counts any more
        await AskBothAsync(2);

        redisAnswers.Should().Equal(memoryAnswers, "the script and the arithmetic of the process are the same limiter");
        redisAnswers.Count(answer => answer).Should().Be(10 + 5 + 2);
    }

    [Fact]
    public async Task AskingWithoutTaking_UsesNothing_AndSaysNoOnlyWhenThereIsNothingLeft()
    {
        var clock = new FakeTimeProvider(_start);
        using CallerRateLimiter limiter = Limiter(clock, "preauth", "ip:1.2.3.4", limit: 2);

        bool freshAsk = (await limiter.AcquireAsync(0)).IsAcquired;
        bool askedAgain = (await limiter.AcquireAsync(0)).IsAcquired;
        await TakeAsync(limiter);
        await TakeAsync(limiter);
        bool afterTheLimit = (await limiter.AcquireAsync(0)).IsAcquired;

        (freshAsk, askedAgain, afterTheLimit).Should().Be((true, true, false));
    }

    [Fact]
    public async Task EachScopeAndEachCaller_HasItsOwnCount()
    {
        var clock = new FakeTimeProvider(_start);
        using CallerRateLimiter global = Limiter(clock, "global", "user:bea", limit: 1);
        using CallerRateLimiter expensive = Limiter(clock, "expensive", "user:bea", limit: 1);
        using CallerRateLimiter other = Limiter(clock, "global", "user:carlos", limit: 1);

        bool[] results = [await TakeAsync(global), await TakeAsync(expensive), await TakeAsync(other), await TakeAsync(global)];

        results.Should().Equal(true, true, true, false);
    }

    [Fact]
    public async Task WithTheClockOfRedis_TheLimitHolds_AndTheKeyHoldsNeitherTheUserNorTheAddress()
    {
        using var limiter = new CallerRateLimiter(_redis, () => true, "preauth", "ip:203.0.113.77", 2, TimeSpan.FromSeconds(60), TimeProvider.System);

        bool[] results = [await TakeAsync(limiter), await TakeAsync(limiter), await TakeAsync(limiter)];
        string[] keys = [.. _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: "v1:ratelimit:preauth:*").Select(key => key.ToString())];

        results.Should().Equal(true, true, false);
        keys.Should().NotBeEmpty().And.OnlyContain(key => !key.Contains("203.0.113.77") && System.Text.RegularExpressions.Regex.IsMatch(key, "^v1:ratelimit:preauth:[0-9a-f]{32}:[0-9]+$"));
    }

    [Fact]
    public async Task WhenTheCountIsInMemoryNow_NothingIsWrittenToRedis()
    {
        var clock = new FakeTimeProvider(_start);
        using CallerRateLimiter limiter = Limiter(clock, "global", "user:alone", limit: 5, inRedis: false);

        await TakeAsync(limiter);

        _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: "v1:ratelimit:global:*").Count().Should().Be(0);
    }

    [Fact]
    public async Task WhenRedisCannotBeReached_TheLimiterOfTheProcessHoldsTheLimit_AndItIsCounted()
    {
        await using ConnectionMultiplexer down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200");
        var clock = new FakeTimeProvider(_start);
        using var limiter = new CallerRateLimiter(down, () => true, "global", "user:offline", 2, TimeSpan.FromSeconds(60), clock, redisClock: false);
        long fallbacks = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "sakerwatch_http_rate_limiter_fallback_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref fallbacks, value));
        listener.Start();

        bool[] results = [await TakeAsync(limiter), await TakeAsync(limiter), await TakeAsync(limiter)];

        results.Should().Equal(true, true, false);
        fallbacks.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ThePreAuthenticationLimiters_OfTwoProcesses_CountTheSameAddressTogether()
    {
        var settings = Options.Create(new RateLimitingOptions { PreAuthenticationPermitLimit = 2, Backend = RateLimitBackend.Redis });
        await using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IConnectionMultiplexer>(_redis)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IApiInstances>(new DeclaredApiInstances(Options.Create(new DeploymentOptions { ApiReplicas = 2 })))
            .BuildServiceProvider();
        using var first = new PreAuthenticationRateLimiter(settings, provider);
        using var second = new PreAuthenticationRateLimiter(settings, provider);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.9");

        (await first.TakeAsync(context, CancellationToken.None)).Dispose();
        (await second.TakeAsync(context, CancellationToken.None)).Dispose();
        using RateLimitLease askedOfFirst = await first.PeekAsync(context, CancellationToken.None);
        using RateLimitLease askedOfSecond = await second.PeekAsync(context, CancellationToken.None);

        (askedOfFirst.IsAcquired, askedOfSecond.IsAcquired).Should().Be((false, false), "the two refused requests were counted together, whichever process saw them");
    }

    [Theory]
    [InlineData(RateLimitBackend.Auto, 1, false)]
    [InlineData(RateLimitBackend.Auto, 2, true)]
    [InlineData(RateLimitBackend.Memory, 5, false)]
    [InlineData(RateLimitBackend.Redis, 1, true)]
    public void TheBackend_IsChosenFromTheSettingAndTheNumberOfProcesses(RateLimitBackend backend, int processes, bool redis) =>
        new RateLimitingOptions { Backend = backend }.UsesRedis(processes).Should().Be(redis);

    private ApiInstanceRegistry Registry(int declared, TimeSpan? lease = null, IConnectionMultiplexer? redis = null) =>
        new(Options.Create(new DeploymentOptions { ApiReplicas = declared }), NullLogger<ApiInstanceRegistry>.Instance, redis ?? _redis, interval: TimeSpan.FromSeconds(10), lease: lease);

    [Fact]
    public async Task ProcessesThatDeclareOne_SeeEachOther_AndCountWithTwo()
    {
        using ApiInstanceRegistry first = Registry(declared: 1);
        using ApiInstanceRegistry second = Registry(declared: 1);

        await first.BeatAsync(CancellationToken.None);
        int alone = first.Count;
        await second.BeatAsync(CancellationToken.None);
        await first.BeatAsync(CancellationToken.None);

        (alone, first.Count, second.Count).Should().Be((1, 2, 2));
    }

    [Fact]
    public async Task TheDeclaredNumber_IsNeverCountedBelow()
    {
        using ApiInstanceRegistry registry = Registry(declared: 3);

        await registry.BeatAsync(CancellationToken.None);

        registry.Count.Should().Be(3, "one process seen, three declared: the operator knows of processes that have not started yet");
    }

    [Fact]
    public async Task AProcessThatStopsBeating_IsDroppedWhenItsLeaseEnds()
    {
        using ApiInstanceRegistry stays = Registry(declared: 1, lease: TimeSpan.FromSeconds(1));
        using ApiInstanceRegistry leaves = Registry(declared: 1, lease: TimeSpan.FromSeconds(1));
        await stays.BeatAsync(CancellationToken.None);
        await leaves.BeatAsync(CancellationToken.None);
        await stays.BeatAsync(CancellationToken.None);
        stays.Count.Should().Be(2);

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (stays.Count > 1)
        {
            await Task.Delay(200, bound.Token); // the lease is Redis's clock, so it is waited out; the wait is bounded
            await stays.BeatAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AProcessThatStops_LeavesTheSetAtOnce()
    {
        using ApiInstanceRegistry stays = Registry(declared: 1);
        using ApiInstanceRegistry stops = Registry(declared: 1);
        await stops.BeatAsync(CancellationToken.None);
        await stays.BeatAsync(CancellationToken.None);
        stays.Count.Should().Be(2);

        await stops.StopAsync(CancellationToken.None);
        await stays.BeatAsync(CancellationToken.None);

        stays.Count.Should().Be(1);
    }

    [Fact]
    public async Task WhenRedisCannotBeReached_TheLastCountIsKept_AndNothingThrows()
    {
        await using ConnectionMultiplexer down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200");
        using ApiInstanceRegistry registry = Registry(declared: 2, redis: down);

        await registry.BeatAsync(CancellationToken.None);

        registry.Count.Should().Be(2);
    }

    [Fact]
    public async Task AHostWithNoRedis_CountsWhatItDeclares()
    {
        using var registry = new ApiInstanceRegistry(Options.Create(new DeploymentOptions { ApiReplicas = 4 }), NullLogger<ApiInstanceRegistry>.Instance);

        await registry.BeatAsync(CancellationToken.None);

        registry.Count.Should().Be(4);
    }
}
