using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Providers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class SearchCacheTests : IAsyncLifetime
{
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

    private RedisSearchCache Cache() => new(_redis, NullLogger<RedisSearchCache>.Instance);

    [Fact]
    public async Task AnAnswer_IsKeptAndComesBackEqual_ForTheSameProviderAndTextInAnyCase()
    {
        RedisSearchCache cache = Cache();
        CachedListing[] listings = [new("XNGS", "AAPL", "Apple Inc.", "USD", AssetType.Equity), new("ARCX", "SPY", null, null, AssetType.Etf)];

        await cache.SetAsync("twelvedata", "Apple", listings, TimeSpan.FromMinutes(5), CancellationToken.None);

        (await cache.GetAsync("twelvedata", "  APPLE ", CancellationToken.None)).Should().Equal(listings);
    }

    [Fact]
    public async Task ADifferentProviderOrText_IsAMiss()
    {
        RedisSearchCache cache = Cache();
        await cache.SetAsync("twelvedata", "Apple", [new("XNGS", "AAPL", "Apple", "USD", AssetType.Equity)], TimeSpan.FromMinutes(5), CancellationToken.None);

        (await cache.GetAsync("itick", "Apple", CancellationToken.None)).Should().BeNull();
        (await cache.GetAsync("twelvedata", "Apples", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnEmptyAnswer_IsKeptToo_SoAnUnknownTickerDoesNotSpendASearchEveryTime()
    {
        RedisSearchCache cache = Cache();

        await cache.SetAsync("twelvedata", "ZZZZ", [], TimeSpan.FromMinutes(5), CancellationToken.None);

        (await cache.GetAsync("twelvedata", "ZZZZ", CancellationToken.None)).Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task AnEntryExpires()
    {
        RedisSearchCache cache = Cache();
        await cache.SetAsync("twelvedata", "Apple", [new("XNGS", "AAPL", "Apple", "USD", AssetType.Equity)], TimeSpan.FromMilliseconds(200), CancellationToken.None);

        await Task.Delay(600);

        (await cache.GetAsync("twelvedata", "Apple", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnEntryThatCannotBeRead_IsAMissAndNotAnError()
    {
        RedisSearchCache cache = Cache();
        await cache.SetAsync("twelvedata", "Apple", [new("XNGS", "AAPL", "Apple", "USD", AssetType.Equity)], TimeSpan.FromMinutes(5), CancellationToken.None);
        foreach (RedisKey key in _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: "v1:search:*"))
        {
            await _redis.GetDatabase().StringSetAsync(key, "not json at all");
        }

        (await cache.GetAsync("twelvedata", "Apple", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheKey_NeverContainsTheText()
    {
        RedisSearchCache cache = Cache();
        await cache.SetAsync("twelvedata", "secret company name", [], TimeSpan.FromMinutes(5), CancellationToken.None);

        string[] keys = [.. _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: "v1:search:*").Select(key => key.ToString())];

        keys.Should().ContainSingle().Which.Should().NotContainEquivalentOf("secret").And.StartWith("v1:search:twelvedata:");
    }
}
