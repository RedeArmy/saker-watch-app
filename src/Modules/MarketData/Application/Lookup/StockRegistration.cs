using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// Puts a validated stock in the catalog and queues its first load (ADR-0032). The registry returns the id of whichever row holds the market and
/// ticker, so parallel registrations end with one stock; the job key is derived from that id, so they also end with one job.
/// </summary>
public sealed partial class StockRegistration(IStockRegistry registry, IJobEnqueuer jobs, IOptions<IngestionOptions> ingestion, ILogger<StockRegistration> logger)
{
    /// <summary>Registers <paramref name="stock"/> and queues its history; returns the id of the stock that holds its market and ticker.</summary>
    public async Task<StockId> RegisterAsync(Stock stock, string provider, ProviderListingKey providerKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stock);
        StockId id = await registry.RegisterAsync(stock, provider, providerKey, now, cancellationToken);
        await jobs.EnqueueAsync(
            BackfillStockJob.KeyFor(id),
            new BackfillStockJob(id.Value),
            new JobEnqueueOptions(MaxAttempts: ingestion.Value.BackfillMaxAttempts),
            cancellationToken);
        LogRegistered(stock.Market.Value);
        return id;
    }

    [LoggerMessage(EventId = 2501, Level = LogLevel.Information, Message = "A stock of {Market} was registered in the catalog and its history queued")]
    private partial void LogRegistered(string market);
}
