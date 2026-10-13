using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.Integration.Tests.Infrastructure;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class RedisTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder(ComposeImages.For("redis")).Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = connectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddSakerRedis(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReadinessCheck_ReportsHealthyForAReachableServer()
    {
        await using ServiceProvider provider = BuildProvider(_container.GetConnectionString());
        var check = new RedisReadinessCheck(provider.GetRequiredService<IConnectionMultiplexer>());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ReadinessCheck_ReportsUnhealthyWithoutDetailsWhenTheServerIsDown()
    {
        await using ServiceProvider provider = BuildProvider("127.0.0.1:1,connectTimeout=300,syncTimeout=300,asyncTimeout=300");
        var check = new RedisReadinessCheck(provider.GetRequiredService<IConnectionMultiplexer>());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public async Task VersionedKeys_RoundTripThroughTheSharedConnection()
    {
        await using ServiceProvider provider = BuildProvider(_container.GetConnectionString());
        IDatabase db = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        RedisKey key = RedisKeys.For("price", "NVDA");

        await db.StringSetAsync(key, "123.45", TimeSpan.FromMinutes(1));

        key.ToString().Should().Be("v1:price:NVDA");
        ((string?)await db.StringGetAsync(key)).Should().Be("123.45");
    }

    [Fact]
    public void Options_WithoutAConnectionString_FailValidation()
    {
        var services = new ServiceCollection();
        services.AddSakerRedis(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => _ = provider.GetRequiredService<IOptions<RedisOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*ConnectionString*");
    }
}

public sealed class RedisKeysTests
{
    [Fact]
    public void For_JoinsSegmentsUnderTheVersionPrefix()
    {
        RedisKeys.For("price", "NVDA", "daily").ToString().Should().Be("v1:price:NVDA:daily");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a:b")]
    [InlineData("glob*")]
    [InlineData("new\nline")]
    [InlineData("wild?card")]
    public void For_RejectsSegmentsThatCouldInjectKeyLevelsOrPatterns(string segment)
    {
        Action act = () => RedisKeys.For("price", segment);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void For_RejectsOverlongSegmentsAndEmptyKeys()
    {
        Action tooLong = () => RedisKeys.For(new string('a', 129));
        Action empty = () => RedisKeys.For();

        tooLong.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
    }
}
