using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

/// <summary>The two series of a listing are fetched as a pair: their credits are reserved together before the first call (ADR-0043).</summary>
public sealed class ListingIngestionFetchTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 16, 23, 30, 0, TimeSpan.Zero);

    private sealed class Provider(Error? refuse = null, Error? failFirst = null, bool truncated = false) : IMarketDataProvider
    {
        public List<string> Log { get; } = [];

        public string Name => "scripted";

        public Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Error?> ReserveDailyAsync(int requests, CallLane lane, CancellationToken cancellationToken)
        {
            Log.Add($"reserve {requests}");
            return Task.FromResult(refuse);
        }

        public Task ReleaseDailyAsync(int requests, CallLane lane, CancellationToken cancellationToken)
        {
            Log.Add($"release {requests}");
            return Task.CompletedTask;
        }

        public Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken)
        {
            Log.Add($"{(request.Adjusted ? "adjusted" : "raw")}{(request.Prepaid ? " prepaid" : string.Empty)}");
            if (request.Adjusted && failFirst is not null)
            {
                return Task.FromResult(Result.Failure<DailySeries>(failFirst));
            }

            return Task.FromResult(Result.Success(new DailySeries(request.Key, [], request.Adjusted, 0, truncated)));
        }
    }

    // FetchAsync uses the provider, the calendar and the options only; the rest of the collaborators of the class are not touched by it.
    private static ListingIngestion Ingestion() => new(null!, null!, null!, ExchangeCalendars.Default(), null!, Options.Create(new IngestionOptions()));

    private static ResolvedListing Listing(Provider provider) => new(
        null!, provider, new ProviderListingKey("XNGS", "AAPL"), new UsEquityCalendar(Mic.Parse("XNAS")));

    [Fact]
    public async Task BothSeries_AreFetchedPrepaid_AfterOneReservationOfTwo()
    {
        var provider = new Provider();

        Result<MergedPrices> result = await Ingestion().FetchAsync(Listing(provider), new DateOnly(2026, 10, 1), _now, CallLane.Scheduled, CancellationToken.None);

        provider.Log.Should().Equal("reserve 2", "adjusted prepaid", "raw prepaid");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task WhenTheReservationIsRefused_NoSeriesIsAskedFor()
    {
        var provider = new Provider(refuse: ProviderErrors.RateLimited("scripted", TimeSpan.FromSeconds(30)));

        Result<MergedPrices> result = await Ingestion().FetchAsync(Listing(provider), new DateOnly(2026, 10, 1), _now, CallLane.Scheduled, CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        provider.Log.Should().Equal("reserve 2");
    }

    [Fact]
    public async Task ASeriesTheProviderCutAtItsRowLimit_IsAFailureOfTheListing_NotAStoredHalfSeries()
    {
        var provider = new Provider(truncated: true);

        Result<MergedPrices> result = await Ingestion().FetchAsync(Listing(provider), new DateOnly(2026, 10, 1), _now, CallLane.Scheduled, CancellationToken.None);

        result.Error!.Code.Should().Be(ProviderErrors.TruncatedSeriesCode);
        ListingIngestion.IsRetryable(result.Error).Should().BeFalse("asking again returns the same oldest 5000 rows");
        ListingIngestion.IsProviderFault(result.Error).Should().BeFalse("it is the listing that is too long, so it is recorded against it");
    }

    [Fact]
    public async Task WhenTheFirstSeriesFails_TheCreditOfTheSecondGoesBack()
    {
        var provider = new Provider(failFirst: ProviderErrors.Unavailable("scripted"));

        Result<MergedPrices> result = await Ingestion().FetchAsync(Listing(provider), new DateOnly(2026, 10, 1), _now, CallLane.Scheduled, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        provider.Log.Should().Equal("reserve 2", "adjusted prepaid", "release 1");
    }
}
