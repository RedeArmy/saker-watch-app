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

    internal sealed record Rig(RealApiHost Api, WebApplicationFactory<HostApi::Program> Host, FakeMarketDataProvider Provider, HttpClient Client) : IAsyncDisposable
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

    internal static async Task<Rig> StartAsync(TimescalePostgresFixture postgres, bool budgetSpent = false)
    {
        RealApiHost api = await RealApiHost.StartAsync(postgres);
        var provider = new FakeMarketDataProvider(ExchangeCalendars.Default(), TimeProvider.System)
            .WithListing(Listing("XNGS", "AAPL", "Apple Inc."), 150m)
            .WithListing(Listing("XNYS", "KO", "Coca-Cola Co."), 60m)
            .WithListing(Listing("XNGS", "MSFT", "Microsoft Corp."), 400m)
            .WithListing(Listing("XNGS", "NVDA", "Nvidia Corp."), 900m)
            .WithListing(new ProviderListing(new ProviderListingKey("XTSE", "SHOP"), "Shopify", "CAD", "America/Toronto", AssetType.Equity), 90m);
        WebApplicationFactory<HostApi::Program> host = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MarketData:Providers:Markets:XNAS", FakeMarketDataProvider.ProviderName);
            builder.UseSetting("MarketData:Providers:Markets:XNYS", FakeMarketDataProvider.ProviderName);
            builder.UseSetting("MarketData:Payloads:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMarketDataProvider>();
                services.AddSingleton<IMarketDataProvider>(provider);
                services.RemoveAll<IProviderCallBudget>();
                services.AddSingleton<IProviderCallBudget>(budgetSpent ? new DenyAll() : new AllowAll());
                services.RemoveAll<ISearchCache>();
                services.AddSingleton<ISearchCache, MemoryCache>();
            });
        });
        return new Rig(api, host, provider, host.CreateClient());
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
