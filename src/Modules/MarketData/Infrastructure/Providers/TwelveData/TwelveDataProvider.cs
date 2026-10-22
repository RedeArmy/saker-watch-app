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
    private const string _searchOperation = "search";
    private const string _dailyOperation = "daily";

    // Measured on 2026-10-17 with /api_usage: a time_series call costs one credit even when it fails (unknown symbol, plan restriction),
    // and symbol_search costs none, so a search never touches the budget.
    /// <summary>Suffix of the budget key of the on-demand allowance, after the provider name.</summary>
    public const string OnDemandBudgetSuffix = "_ondemand";

    private const int _timeSeriesCredits = 1;

    private static readonly Counter<long> _calls = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_provider_calls_total",
        description: "Calls to external market data providers by provider, operation and outcome.");

    private readonly IHttpClientFactory _clients;
    private readonly IOptions<TwelveDataOptions> _options;
    private readonly IProviderCallBudget _budget;
    private readonly IProviderPayloadStore _payloads;
    private readonly TimeProvider _clock;
    private readonly ILogger<TwelveDataProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="clients">Source of the named, allow-listed client.</param>
    /// <param name="options">Connection settings.</param>
    /// <param name="budget">The shared call budget; a call is made only after its credit is reserved.</param>
    /// <param name="payloads">Keeps the raw body of every successful response.</param>
    /// <param name="clock">Injected clock, for the time a response was fetched.</param>
    /// <param name="logger">Logger.</param>
    public TwelveDataProvider(
        IHttpClientFactory clients,
        IOptions<TwelveDataOptions> options,
        IProviderCallBudget budget,
        IProviderPayloadStore payloads,
        TimeProvider clock,
        ILogger<TwelveDataProvider> logger)
    {
        _clients = clients;
        _options = options;
        _budget = budget;
        _payloads = payloads;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => TwelveDataOptions.ProviderName;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.TwelveData.Search");

        Result<ProviderResponse> response = await GetAsync(
            $"symbol_search?symbol={Uri.EscapeDataString(symbol.Trim())}&outputsize={_maxSearchRows}", symbol, credits: 0, CallLane.Scheduled, cancellationToken);
        if (!response.IsSuccess)
        {
            Record(_searchOperation, response.Error!);
            return response.Error!;
        }

        using JsonDocument document = response.Value.Document;
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return Fail<IReadOnlyList<ProviderListing>>(_searchOperation, ProviderErrors.InvalidResponse(Name));
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

        await KeepAsync(new ProviderPayload(Name, _searchOperation, null, symbol.Trim(), null, response.Value.Body, _clock.GetUtcNow()), cancellationToken);
        _calls.Add(1, Tags(_searchOperation, "success"));
        return listings;
    }

    /// <inheritdoc />
    public async Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.TwelveData.GetDaily");

        string query = DailyQuery(request);

        Result<ProviderResponse> response = await GetAsync(query, request.Key.Symbol, _timeSeriesCredits, request.Lane, cancellationToken);
        if (!response.IsSuccess)
        {
            Record(_dailyOperation, response.Error!);
            return response.Error!;
        }

        using JsonDocument document = response.Value.Document;
        if (!document.RootElement.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return Fail<DailySeries>(_dailyOperation, ProviderErrors.InvalidResponse(Name));
        }

        bool truncated = values.GetArrayLength() >= _maxRows;
        (SortedDictionary<DateOnly, DailyBar> bars, int rejected) = CollectBars(values, request);

        if (rejected > 0)
        {
            LogRowsRejected(request.Key.Symbol, rejected);
        }

        await KeepAsync(
            new ProviderPayload(Name, _dailyOperation, request.Key, $"{request.From:yyyy-MM-dd}..{request.To:yyyy-MM-dd}", request.Adjusted, response.Value.Body, _clock.GetUtcNow()),
            cancellationToken);
        _calls.Add(1, Tags(_dailyOperation, "success"));
        return new DailySeries(request.Key, [.. bars.Values], request.Adjusted, rejected, truncated);
    }

    private static string DailyQuery(DailySeriesRequest request)
    {
        string query = $"time_series?symbol={Uri.EscapeDataString(request.Key.Symbol)}&mic_code={Uri.EscapeDataString(request.Key.ProviderMic)}"
            + $"&interval=1day&order=asc&adjust={(request.Adjusted ? "all" : "none")}&outputsize={_maxRows}";
        if (request.From is { } from)
        {
            query += $"&start_date={from:yyyy-MM-dd}";
        }

        // Verified on 2026-10-17: end_date is exclusive (end_date 2024-06-20 stops at the 18th). Asking for the day after is correct either
        // way, and the range is enforced again on what comes back.
        return request.To is { } to ? query + $"&end_date={to.AddDays(1):yyyy-MM-dd}" : query;
    }

    // Reads every row: a valid bar inside the requested range is kept (the later row wins for a repeated date), a row outside the range is
    // ignored, and a row that cannot be a bar is counted as rejected.
    private static (SortedDictionary<DateOnly, DailyBar> Bars, int Rejected) CollectBars(JsonElement values, DailySeriesRequest request)
    {
        var bars = new SortedDictionary<DateOnly, DailyBar>();
        int rejected = 0;
        foreach (JsonElement row in values.EnumerateArray())
        {
            if (TryReadBar(row) is not { } bar)
            {
                rejected++;
            }
            else if (IsInRange(bar.Date, request))
            {
                bars[bar.Date] = bar;
            }
        }

        return (bars, rejected);
    }

    private static bool IsInRange(DateOnly date, DailySeriesRequest request) =>
        (request.From is not { } first || date >= first) && (request.To is not { } last || date <= last);

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

    private async Task<Result<ProviderResponse>> GetAsync(string relativeUrl, string symbol, int credits, CallLane lane, CancellationToken cancellationToken)
    {
        if (await SpendBudgetAsync(credits, lane, cancellationToken) is { } refused)
        {
            return refused;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("apikey", _options.Value.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using HttpClient client = _clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Interpret(response, body, symbol);
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

    // A denied call is never sent. A call that costs no credit needs no budget. Returns the refusal, or null when the call may go ahead.
    // On-demand work spends the allowance of its own first and then the plan's budget, so a burst of new stocks cannot take the credits of the
    // daily refresh (ADR-0043). The allowance is spent before the plan's budget because the plan's credits are the scarce ones.
    private async Task<Error?> SpendBudgetAsync(int credits, CallLane lane, CancellationToken cancellationToken)
    {
        if (credits <= 0)
        {
            return null;
        }

        TwelveDataOptions settings = _options.Value;
        if (lane == CallLane.OnDemand)
        {
            BudgetDecision allowance = await _budget.TryAcquireAsync(
                Name + OnDemandBudgetSuffix, new BudgetLimits(settings.CreditsPerMinute, settings.OnDemandCreditsPerDay), credits, cancellationToken);
            if (!allowance.Granted)
            {
                return ProviderErrors.RateLimited(Name, allowance.RetryAfter);
            }
        }

        BudgetDecision decision = await _budget.TryAcquireAsync(Name, new BudgetLimits(settings.CreditsPerMinute, settings.CreditsPerDay), credits, cancellationToken);
        return decision.Granted ? null : ProviderErrors.RateLimited(Name, decision.RetryAfter);
    }

    private Result<ProviderResponse> Interpret(HttpResponseMessage response, string body, string symbol)
    {
        JsonDocument? document = ParseObject(body);
        (int? code, string? message) = document is null ? (null, null) : ReadErrorFields(document);
        bool isError = !response.IsSuccessStatusCode
            || (document is not null && string.Equals(Text(document.RootElement, "status"), "error", StringComparison.OrdinalIgnoreCase));
        if (!isError)
        {
            return document is null ? ProviderErrors.InvalidResponse(Name) : new ProviderResponse(document, body);
        }

        document?.Dispose();
        return message is not null && message.Contains("No data is available", StringComparison.OrdinalIgnoreCase)
            ? EmptyRange(body)
            : Classify((int)response.StatusCode, code, message, symbol, response.Headers.RetryAfter?.Delta);
    }

    // Every Twelve Data answer is a JSON object; anything else cannot be the contract, and indexing it would throw.
    private static JsonDocument? ParseObject(string body)
    {
        JsonDocument? document = TryParse(body);
        if (document is not null && document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    private static (int? Code, string? Message) ReadErrorFields(JsonDocument document)
    {
        int? code = document.RootElement.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int value) ? value : null;
        return (code, Text(document.RootElement, "message"));
    }

    private Error Classify(int status, int? code, string? message, string symbol, TimeSpan? retryAfter)
    {
        int effective = code ?? status;
        string text = message ?? string.Empty;

        if (effective == 429 || status == (int)HttpStatusCode.TooManyRequests)
        {
            // Verified 2026-10-17: the 429 carries no Retry-After header, only "You have run out of API credits for the current minute ...
            // Wait for the next minute", so the wait is the time left in the current minute.
            return ProviderErrors.RateLimited(Name, retryAfter ?? (Mentions(text, "current minute") ? UntilNextMinute() : null));
        }

        return ClassifyAccess(status, effective, text) ?? ClassifyLookup(status, effective, text, symbol);
    }

    // The key was refused, or the plan does not include what was asked.
    private Error? ClassifyAccess(int status, int effective, string text)
    {
        if (effective is 401 or 403 || status is 401 or 403)
        {
            return Mentions(text, "plan") ? ProviderErrors.PlanRestricted(Name) : ProviderErrors.Unauthorized(Name);
        }

        return Mentions(text, "available starting with") || Mentions(text, "upgrade") ? ProviderErrors.PlanRestricted(Name) : null;
    }

    // Whatever else the answer says: an unknown symbol, a server problem, or something that is not the contract.
    private Error ClassifyLookup(int status, int effective, string text, string symbol)
    {
        if (effective == 404 || Mentions(text, "missing or invalid") || Mentions(text, "not found"))
        {
            return ProviderErrors.SymbolNotFound(Name, symbol);
        }

        return status >= 500 || effective >= 500 ? ProviderErrors.Unavailable(Name) : ProviderErrors.InvalidResponse(Name);
    }

    private static bool Mentions(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    // Best effort: the data was already fetched and parsed, so a store that is down must not turn a good answer into a failure.
    private async Task KeepAsync(ProviderPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            await _payloads.StoreAsync(payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPayloadNotKept(payload.Operation, ex.GetType().Name);
        }
    }

    // A range with no session in it (a holiday, a weekend, a day not yet published) is not a failure: it is a series with no bars. The wording
    // is the provider's real answer, recorded on 2026-10-17 for a holiday, a weekend and a future date: HTTP 400 and this message.
    private static ProviderResponse EmptyRange(string body) => new(JsonDocument.Parse("{\"values\":[],\"status\":\"ok\"}"), body);

    private TimeSpan UntilNextMinute() => TimeSpan.FromSeconds(61 - _clock.GetUtcNow().Second);

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

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "The raw Twelve Data response of a {Operation} call could not be kept ({ExceptionType}); the data itself was fetched and parsed")]
    private partial void LogPayloadNotKept(string operation, string exceptionType);

    private sealed record ProviderResponse(JsonDocument Document, string Body);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Twelve Data call failed in transit ({ExceptionType})")]
    private partial void LogTransportFailure(string exceptionType);
}
