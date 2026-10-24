extern alias HostApi;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Providers.Fake;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>
/// The real API host over a real PostgreSQL with a fake provider in place of Twelve Data, and the shared search budget and cache replaced by
/// in-memory stand-ins, for the tests of the stock search and the watchlist.
/// </summary>
internal static class WatchlistRig
{
    internal sealed class AllowAll : IProviderCallBudget
    {
        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken) =>
            Task.FromResult(BudgetDecision.Allowed);
    }

    /// <summary>Grants credits until the day of each key is spent, as the shared budget does; one counter per key, no clock.</summary>
    internal sealed class CountingBudget : IProviderCallBudget
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _spent = new();

        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken)
        {
            int total = _spent.AddOrUpdate(provider, credits, (_, spent) => spent + credits);
            return Task.FromResult(total <= limits.PerDay ? BudgetDecision.Allowed : new BudgetDecision(false, TimeSpan.FromHours(5)));
        }
    }

    internal sealed class DenyAll : IProviderCallBudget
    {
        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken) =>
            Task.FromResult(new BudgetDecision(false, TimeSpan.FromSeconds(37)));
    }

    internal sealed class MemoryCache : ISearchCache
    {
        public Task<IReadOnlyList<CachedListing>?> GetAsync(string provider, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CachedListing>?>(null);

        public Task SetAsync(string provider, string query, IReadOnlyList<CachedListing> listings, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// The time as the API and the test see it: the instant the rig started, plus one microsecond for each time it is read. Seeding "the last
    /// settled session" and answering "is the price current" cannot disagree because a session settled between the two, and what is stamped one
    /// after the other keeps its order (a clock that stood still would stamp a whole test with one instant). Timers and delays are the real ones.
    /// </summary>
    internal sealed class SteadyClock(DateTimeOffset start) : TimeProvider
    {
        private const long _ticksPerMicrosecond = 10;
        private long _reads;

        public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Increment(ref _reads) * _ticksPerMicrosecond);
    }

    internal sealed record Rig(RealApiHost Api, WebApplicationFactory<HostApi::Program> Host, FakeMarketDataProvider Provider, HttpClient Client, SteadyClock Clock) : IAsyncDisposable
    {
        /// <summary>Stops both hosts. A host that is left running keeps its file watchers, and the machine has a small number of them.</summary>
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    internal static ProviderListing Listing(string mic, string symbol, string name) =>
        new(new ProviderListingKey(mic, symbol), name, "USD", "Asia/Tokyo", AssetType.Equity);

    internal static async Task<Rig> StartAsync(TimescalePostgresFixture postgres, bool budgetSpent = false, int? registrationsPerDay = null, Func<FakeMarketDataProvider, MigratedDatabase, IMarketDataProvider>? wrap = null, int? maxCatalogListings = null)
    {
        RealApiHost api = await RealApiHost.StartAsync(postgres);
        var clock = new SteadyClock(DateTimeOffset.UtcNow);
        var provider = new FakeMarketDataProvider(ExchangeCalendars.Default(), clock)
            .WithListing(Listing("XNGS", "AAPL", "Apple Inc."), 150m)
            .WithListing(Listing("XNYS", "KO", "Coca-Cola Co."), 60m)
            .WithListing(Listing("XNGS", "MSFT", "Microsoft Corp."), 400m)
            .WithListing(Listing("XNGS", "NVDA", "Nvidia Corp."), 900m)
            .WithListing(new ProviderListing(new ProviderListingKey("XTSE", "SHOP"), "Shopify", "CAD", "America/Toronto", AssetType.Equity), 90m);
        IProviderCallBudget budget = new AllowAll();
        if (budgetSpent)
        {
            budget = new DenyAll();
        }
        else if (registrationsPerDay is not null)
        {
            budget = new CountingBudget();
        }

        WebApplicationFactory<HostApi::Program> host = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MarketData:Providers:Markets:XNAS", FakeMarketDataProvider.ProviderName);
            builder.UseSetting("MarketData:Providers:Markets:XNYS", FakeMarketDataProvider.ProviderName);
            builder.UseSetting("MarketData:Payloads:Enabled", "false");
            if (registrationsPerDay is { } perDay)
            {
                builder.UseSetting("MarketData:Lookup:RegistrationsPerUserPerDay", perDay.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (maxCatalogListings is { } cap)
            {
                builder.UseSetting("MarketData:Lookup:MaxCatalogListings", cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<IMarketDataProvider>();
                services.AddSingleton(wrap is null ? provider : wrap(provider, api.Db));
                services.RemoveAll<IProviderCallBudget>();
                services.AddSingleton(budget);
                services.RemoveAll<ISearchCache>();
                services.AddSingleton<ISearchCache, MemoryCache>();
            });
        });
        return new Rig(api, host, provider, host.CreateClient(), clock);
    }

    internal static HttpRequestMessage Send(HttpMethod method, string path, string? token, object? body = null)
    {
        HttpRequestMessage request = ApiTestHost.Get(path, token);
        request.Method = method;
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    internal static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
