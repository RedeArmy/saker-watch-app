using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Infrastructure;

/// <summary>
/// Asks, every few minutes, which enabled markets have a session that settled and queues the daily refresh of each. The job key is the market
/// and the day, so asking again for a day already queued, running or done does nothing; the scheduler holds no state of its own, and a worker that
/// was down all day queues the missed day as soon as it starts (ADR-0029).
/// </summary>
public sealed partial class MarketDataScheduler(
    IServiceScopeFactory scopes,
    IMarketDataProviderRouter router,
    ExchangeCalendars calendars,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<MarketDataScheduler> logger) : BackgroundService
{
    /// <summary>How soon the scheduler looks again after a failure to queue.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IngestionOptions settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int failures = await EnsureJobsAsync(stoppingToken);
            TimeSpan wait = failures > 0 ? TimeSpan.FromTicks(Math.Min(RetryDelay.Ticks, settings.CheckInterval.Ticks)) : settings.CheckInterval;
            try
            {
                await Task.Delay(wait, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Queues the refresh of every enabled market whose latest session has settled. Returns how many markets could not be queued.</summary>
    public async Task<int> EnsureJobsAsync(CancellationToken cancellationToken)
    {
        int failures = 0;
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (Mic market in router.EnabledMarkets)
        {
            Result<IExchangeCalendar> calendar = calendars.TryGet(market);
            if (!calendar.IsSuccess)
            {
                LogNoCalendar(market.Value);
                continue;
            }

            try
            {
                DateOnly day = SessionSchedule.LatestSettled(calendar.Value, now, options.Value.SettleDelay);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobEnqueuer>();
                bool created = await jobs.EnqueueAsync(IngestMarketDayJob.KeyFor(market, day), new IngestMarketDayJob(market.Value, day), options: null, cancellationToken);
                if (created)
                {
                    string text = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    LogQueued(market.Value, text);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Also an unexpected calendar answer: one market failing must not stop the loop, and a hosted service that throws stops the host.
                failures++;
                LogQueueFailed(market.Value, ex);
            }
        }

        return failures + (options.Value.FxEnabled ? await EnsureFxJobAsync(now, cancellationToken) : 0);
    }

    // The exchange rate is not tied to a market session: the bank publishes every calendar day, ahead of time, so the job of the day in
    // Guatemala asks for the rates up to today. Returns 1 when it could not be queued.
    private async Task<int> EnsureFxJobAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        (CurrencyCode baseCurrency, CurrencyCode quote) = FxFreshness.Anchor;
        DateOnly day = FxFreshness.TodayInGuatemala(now);
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobEnqueuer>();
            bool created = await jobs.EnqueueAsync(
                FxRefreshJob.KeyFor(baseCurrency, quote, day),
                new FxRefreshJob(baseCurrency.Value, quote.Value, day),
                new JobEnqueueOptions(MaxAttempts: options.Value.FxMaxAttempts),
                cancellationToken);
            if (created)
            {
                string text = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                LogFxQueued(baseCurrency.Value, quote.Value, text);
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFxQueueFailed(baseCurrency.Value, quote.Value, ex);
            return 1;
        }
    }

    [LoggerMessage(EventId = 2121, Level = LogLevel.Information, Message = "The daily market data refresh is disabled; no session will be scheduled")]
    private partial void LogDisabled();

    [LoggerMessage(EventId = 2122, Level = LogLevel.Information, Message = "Daily refresh of {Market} for {Day} queued")]
    private partial void LogQueued(string market, string day);

    [LoggerMessage(EventId = 2123, Level = LogLevel.Warning, Message = "Could not queue the daily refresh of {Market}; it will be tried again shortly")]
    private partial void LogQueueFailed(string market, Exception exception);

    [LoggerMessage(EventId = 2124, Level = LogLevel.Warning, Message = "The market {Market} has a provider but no calendar, so its daily refresh cannot be scheduled")]
    private partial void LogNoCalendar(string market);

    [LoggerMessage(EventId = 2125, Level = LogLevel.Information, Message = "Daily refresh of the exchange rates {Base}/{Quote} for {Day} queued")]
    private partial void LogFxQueued(string @base, string quote, string day);

    [LoggerMessage(EventId = 2126, Level = LogLevel.Warning, Message = "Could not queue the daily refresh of the exchange rates {Base}/{Quote}; it will be tried again shortly")]
    private partial void LogFxQueueFailed(string @base, string quote, Exception exception);
}
