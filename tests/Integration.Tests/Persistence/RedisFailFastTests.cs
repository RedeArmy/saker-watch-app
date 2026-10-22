using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SakerWatch.BuildingBlocks.Redis;
using StackExchange.Redis;

namespace SakerWatch.Integration.Tests.Persistence;

public sealed class RedisFailFastTests
{
    // A port nothing listens on: Redis is down for good. The default command timeout is five seconds, and the connect timeout of the connection
    // string is shortened to keep the test short; neither is what is measured.
    private static ServiceProvider Down() => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200",
        }).Build())
        .AddLogging()
        .AddSakerRedis(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200",
        }).Build())
        .BuildServiceProvider();

    [Fact]
    public async Task ACommandWhileRedisIsDown_FailsAtOnce_NotAfterTheCommandTimeout()
    {
        await using ServiceProvider provider = Down();
        IDatabase database = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

        var clock = Stopwatch.StartNew();
        Func<Task> read = () => database.StringGetAsync("v1:perf:anything");
        await read.Should().ThrowAsync<RedisException>();
        clock.Stop();

        clock.ElapsedMilliseconds.Should().BeLessThan(2000, "a cache read that waited for the five-second command timeout would add five seconds to every request while Redis is down");
    }

    [Fact]
    public async Task TheHostConnectsWhenItStarts_SoTheFirstRequestDoesNotPayForIt()
    {
        await using ServiceProvider provider = Down();
        var starter = provider.GetServices<IHostedService>().Single(service => service.GetType().Name == "RedisConnectionStarter");

        await starter.StartAsync(CancellationToken.None);

        provider.GetRequiredService<IConnectionMultiplexer>().Should().NotBeNull();
    }
}
