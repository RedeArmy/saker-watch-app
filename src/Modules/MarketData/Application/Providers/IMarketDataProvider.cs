using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Application.Providers;

/// <summary>
/// A source of listings and daily prices (ADR-0027). Adapters translate a vendor's API into these types and report expected failures as
/// <see cref="Result"/> values built with <see cref="ProviderErrors"/>, never as exceptions, so ingestion can tell a rate limit from a
/// symbol that does not exist. Everything a vendor returns is untrusted text.
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>The name the configuration uses to route markets to this provider.</summary>
    string Name { get; }

    /// <summary>Finds the listings that match <paramref name="symbol"/>; one symbol can exist on several markets.</summary>
    Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken);

    /// <summary>Fetches daily bars. The result holds only bars that passed validation and says how many were refused.</summary>
    Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves the credits of <paramref name="requests"/> daily requests at once, for a caller that needs all of them or none (the adjusted and the raw series of a listing):
    /// a call is then made with <see cref="DailySeriesRequest.Prepaid"/>. Returns the refusal (a rate limit with its wait) or <see langword="null"/>. A provider that does
    /// not meter its calls reserves nothing.
    /// </summary>
    Task<Error?> ReserveDailyAsync(int requests, CallLane lane, CancellationToken cancellationToken) => Task.FromResult<Error?>(null);

    /// <summary>Gives back the credits of <paramref name="requests"/> reserved requests that were never made.</summary>
    Task ReleaseDailyAsync(int requests, CallLane lane, CancellationToken cancellationToken) => Task.CompletedTask;
}
