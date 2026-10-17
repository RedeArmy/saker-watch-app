using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

/// <summary>
/// <see cref="IMarketDataProvider"/> over the Twelve Data REST API (ADR-0027). The key travels only in the <c>Authorization</c> header; the
/// body of an error is never copied into a message, only classified. The response is untrusted: it is parsed defensively, every bar is
/// validated by the domain and a bad row is counted and dropped, never repaired.
/// </summary>
public sealed partial class TwelveDataProvider : IMarketDataProvider
{
    /// <summary>Name of the named <see cref="HttpClient"/>.</summary>
    public const string ClientName = "twelvedata";

    // One request returns up to 5 000 daily rows, about twenty years: a backfill is one credit (verified on the free plan).
    private const int _maxRows = 5000;
    private const int _maxSearchRows = 30;

    private static readonly Counter<long> _calls = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_provider_calls_total",
        description: "Calls to external market data providers by provider, operation and outcome.");

    private readonly IHttpClientFactory _clients;
    private readonly IOptions<TwelveDataOptions> _options;
    private readonly ILogger<TwelveDataProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="clients">Source of the named, allow-listed client.</param>
    /// <param name="options">Connection settings.</param>
    /// <param name="logger">Logger.</param>
    public TwelveDataProvider(IHttpClientFactory clients, IOptions<TwelveDataOptions> options, ILogger<TwelveDataProvider> logger)
    {
        _clients = clients;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => TwelveDataOptions.ProviderName;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.TwelveData.Search");

        Result<JsonDocument> response = await GetAsync(
            $"symbol_search?symbol={Uri.EscapeDataString(symbol.Trim())}&outputsize={_maxSearchRows}", symbol, cancellationToken);
        if (!response.IsSuccess)
        {
            Record("search", response.Error!);
            return response.Error!;
        }

        using JsonDocument document = response.Value;
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return Fail<IReadOnlyList<ProviderListing>>("search", ProviderErrors.InvalidResponse(Name));
        }

        var listings = new List<ProviderListing>();
        foreach (JsonElement item in data.EnumerateArray())
        {
            string? mic = Text(item, "mic_code");
            string? ticker = Text(item, "symbol");
            if (mic is null || ticker is null)
            {
                continue;
            }

            listings.Add(new ProviderListing(
                new ProviderListingKey(mic, ticker),
                Text(item, "instrument_name"),
                Text(item, "currency"),
                Text(item, "exchange_timezone"),
                MapType(Text(item, "instrument_type"))));
        }

        _calls.Add(1, Tags("search", "success"));
        return listings;
    }

    /// <inheritdoc />
    public async Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.TwelveData.GetDaily");

        string query = $"time_series?symbol={Uri.EscapeDataString(request.Key.Symbol)}&mic_code={Uri.EscapeDataString(request.Key.ProviderMic)}"
            + $"&interval=1day&order=asc&adjust={(request.Adjusted ? "all" : "none")}&outputsize={_maxRows}"
            + (request.From is { } from ? $"&start_date={from:yyyy-MM-dd}" : string.Empty)
            // Observed on 2026-10-16: with end_date 2024-06-20 the last bar returned was 2024-06-18, so the bound looks exclusive (June 19 was a
            // holiday). Asking for the day after is correct either way, and the range is enforced again below on what comes back.
            + (request.To is { } to ? $"&end_date={to.AddDays(1):yyyy-MM-dd}" : string.Empty);

        Result<JsonDocument> response = await GetAsync(query, request.Key.Symbol, cancellationToken);
        if (!response.IsSuccess)
        {
            Record("daily", response.Error!);
            return response.Error!;
        }

        using JsonDocument document = response.Value;
        if (!document.RootElement.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return Fail<DailySeries>("daily", ProviderErrors.InvalidResponse(Name));
        }

        var bars = new SortedDictionary<DateOnly, DailyBar>();
        int rejected = 0;
        foreach (JsonElement row in values.EnumerateArray())
        {
            if (TryReadBar(row) is { } bar)
            {
                if ((request.From is { } first && bar.Date < first) || (request.To is { } last && bar.Date > last))
                {
                    continue;
                }

                bars[bar.Date] = bar;
            }
            else
            {
                rejected++;
            }
        }

        if (rejected > 0)
        {
            LogRowsRejected(request.Key.Symbol, rejected);
        }

        _calls.Add(1, Tags("daily", "success"));
        return new DailySeries(request.Key, [.. bars.Values], request.Adjusted, rejected);
    }

    private static DailyBar? TryReadBar(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !DateOnly.TryParseExact(Text(row, "datetime"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            || !TryDecimal(row, "open", out decimal open)
            || !TryDecimal(row, "high", out decimal high)
            || !TryDecimal(row, "low", out decimal low)
            || !TryDecimal(row, "close", out decimal close)
            || !long.TryParse(Text(row, "volume"), NumberStyles.None, CultureInfo.InvariantCulture, out long volume))
        {
            return null;
        }

        Result<DailyBar> bar = DailyBar.Create(date, open, high, low, close, volume);
        return bar.IsSuccess ? bar.Value : null;
    }

    private static bool TryDecimal(JsonElement row, string name, out decimal value)
    {
        value = 0;
        return Text(row, name) is { } text && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static AssetType MapType(string? type) => type switch
    {
        "Common Stock" or "Preferred Stock" => AssetType.Equity,
        "ETF" => AssetType.Etf,
        _ => AssetType.Other,
    };

    private static KeyValuePair<string, object?>[] Tags(string operation, string outcome) =>
        [new("provider", TwelveDataOptions.ProviderName), new("operation", operation), new("outcome", outcome)];

    private async Task<Result<JsonDocument>> GetAsync(string relativeUrl, string symbol, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("apikey", _options.Value.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using HttpClient client = _clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            JsonDocument? document = TryParse(body);
            if (document is not null && document.RootElement.ValueKind != JsonValueKind.Object)
            {
                // Every Twelve Data answer is a JSON object; anything else cannot be the contract, and indexing it would throw.
                document.Dispose();
                document = null;
            }

            string? message = null;
            int? code = null;
            if (document is not null)
            {
                if (document.RootElement.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int value))
                {
                    code = value;
                }

                message = Text(document.RootElement, "message");
            }

            bool isError = !response.IsSuccessStatusCode
                || (document is not null && string.Equals(Text(document.RootElement, "status"), "error", StringComparison.OrdinalIgnoreCase));
            if (isError)
            {
                document?.Dispose();
                return Classify((int)response.StatusCode, code, message, symbol, response.Headers.RetryAfter?.Delta);
            }

            return document is null ? ProviderErrors.InvalidResponse(Name) : document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or RateLimiterRejectedException or OperationCanceledException)
        {
            // The type is enough to log: the message of a transport exception can carry the request URL.
            LogTransportFailure(ex.GetType().Name);
            return ProviderErrors.Unavailable(Name);
        }
    }

    private Error Classify(int status, int? code, string? message, string symbol, TimeSpan? retryAfter)
    {
        int effective = code ?? status;
        string text = message ?? string.Empty;

        if (effective == 429 || status == (int)HttpStatusCode.TooManyRequests)
        {
            return ProviderErrors.RateLimited(Name, retryAfter);
        }

        if (effective is 401 or 403 || status is 401 or 403)
        {
            return text.Contains("plan", StringComparison.OrdinalIgnoreCase) ? ProviderErrors.PlanRestricted(Name) : ProviderErrors.Unauthorized(Name);
        }

        if (text.Contains("available starting with", StringComparison.OrdinalIgnoreCase) || text.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderErrors.PlanRestricted(Name);
        }

        if (effective == 404 || text.Contains("missing or invalid", StringComparison.OrdinalIgnoreCase) || text.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderErrors.SymbolNotFound(Name, symbol);
        }

        return status >= 500 || effective >= 500 ? ProviderErrors.Unavailable(Name) : ProviderErrors.InvalidResponse(Name);
    }

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Record(string operation, Error error)
    {
        ProviderFailureKind kind = ProviderErrors.Classify(error);
        _calls.Add(1, Tags(operation, kind.ToString().ToLowerInvariant()));
    }

    private static Result<T> Fail<T>(string operation, Error error)
    {
        Record(operation, error);
        return error;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Twelve Data returned {Rejected} inconsistent daily rows for {Symbol}; they were dropped")]
    private partial void LogRowsRejected(string symbol, int rejected);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Twelve Data call failed in transit ({ExceptionType})")]
    private partial void LogTransportFailure(string exceptionType);
}
