using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Infrastructure;

/// <summary>
/// Measures, per enabled market, how many listings have current prices, how many are stale and how many have no history yet, and how long the
/// stalest one has been stale. Sampled every few minutes and exposed as gauges, so "price staleness under 24 hours on trading days" can be alerted
/// on (the master prompt's SLO). Gauges are per process; with several worker replicas aggregate with <c>max</c>, never <c>sum</c>.
/// </summary>
public sealed partial class MarketDataFreshnessSampler : BackgroundService
{
    private const string _marketTag = "market";

    private readonly IServiceScopeFactory _scopes;
    private readonly IMarketDataProviderRouter _router;
    private readonly ExchangeCalendars _calendars;
    private readonly IOptions<IngestionOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MarketDataFreshnessSampler> _logger;
    private readonly Meter _meter = SakerTelemetry.CreateMeter();
    private readonly object _gate = new();
    private Dictionary<string, MarketFreshness> _snapshot = [];

    /// <summary>Creates the sampler and registers its gauges.</summary>
    public MarketDataFreshnessSampler(
        IServiceScopeFactory scopes,
        IMarketDataProviderRouter router,
        ExchangeCalendars calendars,
        IOptions<IngestionOptions> options,
        TimeProvider timeProvider,
        ILogger<MarketDataFreshnessSampler> logger)
    {
        _scopes = scopes;
        _router = router;
        _calendars = calendars;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _meter.CreateObservableGauge(
            "sakerwatch_marketdata_listings",
            ObserveListings,
            description: "Ingestable listings by market and state (fresh, stale, pending: no history yet).");
        _meter.CreateObservableGauge(
            "sakerwatch_marketdata_price_staleness_seconds",
            ObserveStaleness,
            unit: "s",
            description: "How long the stalest listing of a market has been stale; zero when every listing is current.");
    }

    /// <summary>The counts of one market at one moment.</summary>
    /// <param name="Fresh">Listings whose newest stored day is the latest settled session.</param>
    /// <param name="Stale">Listings missing a session that has settled.</param>
    /// <param name="Pending">Listings with no history yet.</param>
    /// <param name="StalestSeconds">Seconds since the stalest listing went stale, or zero.</param>
    public sealed record MarketFreshness(long Fresh, long Stale, long Pending, double StalestSeconds);

    /// <summary>The last measurement of each market, by canonical market code.</summary>
    public IReadOnlyDictionary<string, MarketFreshness> GetSnapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, MarketFreshness>(_snapshot);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Measures every enabled market that has a calendar and replaces the snapshot. A market that cannot be read keeps its last measurement.</summary>
    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan settle = _options.Value.SettleDelay;
        Dictionary<string, MarketFreshness> next = new(GetSnapshot());
        foreach (Mic market in _router.EnabledMarkets)
        {
            Result<IExchangeCalendar> calendar = _calendars.TryGet(market);
            if (!calendar.IsSuccess)
            {
                continue;
            }

            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                IReadOnlyList<ListingLatest> listings = await scope.ServiceProvider.GetRequiredService<IFreshnessReader>().LatestPerListingAsync(market, cancellationToken);
                next[market.Value] = Measure(calendar.Value, listings, now, settle);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSamplingFailed(market.Value, ex);
            }
        }

        lock (_gate)
        {
            _snapshot = next;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SampleAsync(stoppingToken);
            try
            {
                await Task.Delay(_options.Value.FreshnessInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static MarketFreshness Measure(IExchangeCalendar calendar, IReadOnlyList<ListingLatest> listings, DateTimeOffset now, TimeSpan settle)
    {
        long fresh = 0, stale = 0, pending = 0;
        DateTimeOffset? oldest = null;
        foreach (DateOnly? latest in listings.Select(listing => listing.Latest))
        {
            if (latest is null)
            {
                pending++;
            }
            else if (Freshness.StaleSince(calendar, latest, now, settle) is { } since)
            {
                stale++;
                oldest = oldest is { } current && current <= since ? current : since;
            }
            else
            {
                fresh++;
            }
        }

        return new MarketFreshness(fresh, stale, pending, oldest is { } first ? (now - first).TotalSeconds : 0);
    }

    private IEnumerable<Measurement<long>> ObserveListings()
    {
        foreach ((string market, MarketFreshness value) in GetSnapshot())
        {
            yield return new Measurement<long>(value.Fresh, Tags(market), State("fresh"));
            yield return new Measurement<long>(value.Stale, Tags(market), State("stale"));
            yield return new Measurement<long>(value.Pending, Tags(market), State("pending"));
        }
    }

    private IEnumerable<Measurement<double>> ObserveStaleness()
    {
        foreach ((string market, MarketFreshness value) in GetSnapshot())
        {
            yield return new Measurement<double>(value.StalestSeconds, Tags(market));
        }
    }

    private static KeyValuePair<string, object?> Tags(string market) => new(_marketTag, market);

    private static KeyValuePair<string, object?> State(string state) => new("state", state);

    [LoggerMessage(EventId = 2131, Level = LogLevel.Warning, Message = "Could not measure the freshness of {Market}; its last measurement is kept")]
    private partial void LogSamplingFailed(string market, Exception exception);
}
