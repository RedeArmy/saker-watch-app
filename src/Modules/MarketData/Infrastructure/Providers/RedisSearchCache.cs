using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Redis;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Stocks;
using StackExchange.Redis;

namespace SakerWatch.MarketData.Infrastructure.Providers;

/// <summary>
/// <see cref="ISearchCache"/> over Redis: the listings a provider returned for a search, kept for a few minutes under a key made from the provider
/// and a hash of the lower-cased text, so the text never becomes a key segment. Nothing here is authoritative: a Redis failure, an entry that
/// cannot be read or a version bump is a miss, never an error for the request.
/// </summary>
/// <param name="redis">The shared connection.</param>
/// <param name="logger">Logger.</param>
public sealed partial class RedisSearchCache(IConnectionMultiplexer redis, ILogger<RedisSearchCache> logger) : ISearchCache
{
    private sealed record Entry(string ProviderMic, string Symbol, string? Name, string? Currency, string AssetType);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CachedListing>?> GetAsync(string provider, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RedisValue value = await redis.GetDatabase().StringGetAsync(KeyFor(provider, query));
            if (!value.HasValue)
            {
                return null;
            }

            Entry[]? entries = JsonSerializer.Deserialize<Entry[]>(value.ToString());
            return entries?.Select(entry => new CachedListing(entry.ProviderMic, entry.Symbol, entry.Name, entry.Currency, Parse(entry.AssetType))).ToArray();
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            LogUnavailable(ex.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string provider, string query, IReadOnlyList<CachedListing> listings, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listings);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Entry[] entries = [.. listings.Select(listing => new Entry(listing.ProviderMic, listing.Symbol, listing.Name, listing.Currency, listing.AssetType.ToString()))];
            await redis.GetDatabase().StringSetAsync(KeyFor(provider, query), JsonSerializer.Serialize(entries), ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogUnavailable(ex.GetType().Name);
        }
    }

    private static RedisKey KeyFor(string provider, string query)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(query.Trim().ToLowerInvariant()));
        return RedisKeys.For("search", provider, Convert.ToHexString(hash)[..32].ToLowerInvariant());
    }

    private static AssetType Parse(string text) => Enum.TryParse(text, ignoreCase: true, out AssetType type) ? type : AssetType.Other;

    [LoggerMessage(EventId = 2503, Level = LogLevel.Warning, Message = "The search cache could not be used ({ExceptionType}); the search goes to the provider")]
    private partial void LogUnavailable(string exceptionType);
}
