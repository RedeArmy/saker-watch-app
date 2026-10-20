using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Companies;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Providers.Edgar;

/// <summary>
/// <see cref="IFundamentalsProvider"/> and <see cref="IFilingsProvider"/> over the public JSON of the SEC's EDGAR (ADR-0040): the map of
/// tickers to keys, and the submissions of a filer (its profile and its recent filings). The service has no key but a fair access policy: a
/// declared identity in every request and no more than ten requests a second, which the gate keeps. Everything it answers is untrusted: a row that
/// is not what it should be is dropped and counted, never repaired, text is bounded and checked, an address is built from checked parts, and the
/// body of an error is classified and never copied into a message.
/// </summary>
public sealed partial class EdgarProvider : IFundamentalsProvider, IFilingsProvider, IDisposable
{
    /// <summary>Name of the named <see cref="HttpClient"/>.</summary>
    public const string ClientName = "edgar";

    private const int _maxTickerEntries = 100_000;
    private const int _maxRows = 20_000;
    private const int _maxNameLength = 200;
    private const string _tickers = "tickers";
    private const string _profile = "profile";
    private const string _filings = "filings";

    private static readonly Counter<long> _calls = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_provider_calls_total",
        description: "Calls to external market data providers by provider, operation and outcome.");

    private static readonly JsonDocumentOptions _json = new() { MaxDepth = 16 };

    private readonly IHttpClientFactory _clients;
    private readonly IProviderPayloadStore _payloads;
    private readonly IOptions<EdgarOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<EdgarProvider> _logger;
    private readonly RequestGate _gate;
    private readonly SemaphoreSlim _mapLock = new(1, 1);
    private IReadOnlyDictionary<string, Cik>? _map;
    private DateTimeOffset _mapLoadedAt;

    /// <summary>Creates the provider.</summary>
    /// <param name="clients">Source of the named, allow-listed client.</param>
    /// <param name="payloads">Keeps the raw body of every successful profile and filings response.</param>
    /// <param name="options">Settings: the identity, the addresses, the pace.</param>
    /// <param name="clock">Injected clock, for the pace, the age of the ticker map and the time a response was fetched.</param>
    /// <param name="logger">Logger.</param>
    public EdgarProvider(IHttpClientFactory clients, IProviderPayloadStore payloads, IOptions<EdgarOptions> options, TimeProvider clock, ILogger<EdgarProvider> logger)
    {
        _clients = clients;
        _payloads = payloads;
        _options = options;
        _clock = clock;
        _logger = logger;
        _gate = new RequestGate(clock);
    }

    /// <inheritdoc />
    public string Name => EdgarOptions.ProviderName;

    [GeneratedRegex(@"^\d{10}-\d{2}-\d{6}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AccessionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DocumentPattern();

    [GeneratedRegex(@"^\d{4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex FourDigits();

    [GeneratedRegex(@"^[\p{L}\p{N} .,&'()\-/]{1,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Text();

    /// <inheritdoc />
    public async Task<Result<CompanyProfile>> GetProfileAsync(string ticker, CancellationToken cancellationToken)
    {
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.Edgar.GetProfile");
        Result<Ticker> symbol = Ticker.Create(ticker);
        if (!symbol.IsSuccess)
        {
            return symbol.Error!;
        }

        Result<IReadOnlyDictionary<string, Cik>> map = await TickerMapAsync(cancellationToken);
        if (!map.IsSuccess)
        {
            return map.Error!;
        }

        if (!map.Value.TryGetValue(symbol.Value.Value, out Cik? cik))
        {
            Count(_profile, "notfound");
            return ProviderErrors.SymbolNotFound(Name, symbol.Value.Value);
        }

        Result<string> body = await GetAsync(Combine(_options.Value.DataBaseUrl, $"submissions/CIK{cik.Value}.json"), _profile, cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        Result<CompanyProfile> profile = ParseProfile(body.Value, cik);
        if (!profile.IsSuccess)
        {
            Count(_profile, "invalid");
            return profile.Error!;
        }

        await KeepAsync(_profile, $"CIK{cik.Value}", body.Value, cancellationToken);
        Count(_profile, "success");
        return profile;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Filing>>> GetFilingsAsync(FilingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.Edgar.GetFilings");
        if (FilingRules.Check(request) is { } invalid)
        {
            return invalid;
        }

        string[] forms = [.. (request.Forms ?? []).Select(form => form.Trim())];
        Result<string> body = await GetAsync(Combine(_options.Value.DataBaseUrl, $"submissions/CIK{request.Cik.Value}.json"), _filings, cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        Result<IReadOnlyList<Filing>> filings = ParseFilings(body.Value, request, forms);
        if (!filings.IsSuccess)
        {
            Count(_filings, "invalid");
            return filings.Error!;
        }

        await KeepAsync(_filings, $"CIK{request.Cik.Value}", body.Value, cancellationToken);
        Count(_filings, "success");
        return filings;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        _mapLock.Dispose();
    }

    private static string Combine(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}/{path}";

    private static void Count(string operation, string outcome) =>
        _calls.Add(1, new KeyValuePair<string, object?>("provider", EdgarOptions.ProviderName), new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));

    private static string? Bounded(JsonElement parent, string property, int maxLength)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString()?.Trim();
        return text is { Length: > 0 } && text.Length <= maxLength && Text().IsMatch(text) ? text : null;
    }

    private static List<string> StringList(JsonElement parent, string property, Func<string, bool> accept)
    {
        var values = new List<string>();
        if (parent.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray().Take(50))
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && accept(text.Trim()))
                {
                    values.Add(text.Trim());
                }
            }
        }

        return values;
    }

    private async Task<Result<IReadOnlyDictionary<string, Cik>>> TickerMapAsync(CancellationToken cancellationToken)
    {
        TimeSpan keep = TimeSpan.FromHours(_options.Value.TickerMapHours);
        if (_map is { } known && _clock.GetUtcNow() - _mapLoadedAt < keep)
        {
            return Result.Success(known);
        }

        await _mapLock.WaitAsync(cancellationToken);
        try
        {
            if (_map is { } loaded && _clock.GetUtcNow() - _mapLoadedAt < keep)
            {
                return Result.Success(loaded);
            }

            Result<string> body = await GetAsync(Combine(_options.Value.WwwBaseUrl, "files/company_tickers.json"), _tickers, cancellationToken);
            if (!body.IsSuccess)
            {
                return body.Error!;
            }

            Result<IReadOnlyDictionary<string, Cik>> parsed = ParseTickers(body.Value);
            if (!parsed.IsSuccess)
            {
                Count(_tickers, "invalid");
                return parsed;
            }

            _map = parsed.Value;
            _mapLoadedAt = _clock.GetUtcNow();
            Count(_tickers, "success");
            return parsed;
        }
        finally
        {
            _mapLock.Release();
        }
    }

    // The registry writes {"0":{"cik_str":320193,"ticker":"AAPL","title":"Apple Inc."},...}. A row without a valid key or ticker is dropped; a
    // ticker that repeats keeps the first row.
    private Result<IReadOnlyDictionary<string, Cik>> ParseTickers(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, _json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ProviderErrors.InvalidResponse(Name);
            }

            var map = new Dictionary<string, Cik>(StringComparer.Ordinal);
            int rows = 0;
            int dropped = 0;
            foreach (JsonElement row in document.RootElement.EnumerateObject().Select(property => property.Value))
            {
                if (++rows > _maxTickerEntries)
                {
                    return ProviderErrors.InvalidResponse(Name);
                }

                if (row.ValueKind == JsonValueKind.Object
                    && TickerOf(row) is { } symbol
                    && CikOf(row, "cik_str") is { } cik)
                {
                    map.TryAdd(symbol, cik);
                }
                else
                {
                    dropped++;
                }
            }

            if (dropped > 0)
            {
                LogRowsDropped(_tickers, dropped, rows);
            }

            return map;
        }
        catch (JsonException)
        {
            return ProviderErrors.InvalidResponse(Name);
        }
    }

    private static string? TickerOf(JsonElement row) =>
        row.TryGetProperty("ticker", out JsonElement value) && value.ValueKind == JsonValueKind.String && Ticker.Create(value.GetString()) is { IsSuccess: true } ticker
            ? ticker.Value.Value
            : null;

    private static Cik? CikOf(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        string? text = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) && number > 0 => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        return Cik.Create(text) is { IsSuccess: true } cik ? cik.Value : null;
    }

    // The submissions of a filer: its name, its tickers and exchanges, its industry code and the end of its fiscal year.
    private Result<CompanyProfile> ParseProfile(string body, Cik asked)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, _json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Bounded(root, "name", _maxNameLength) is not { } name)
            {
                return ProviderErrors.InvalidResponse(Name);
            }

            // The answer must be about the filer that was asked for: a key that is another one, or none, is not this company.
            if (CikOf(root, "cik") is not { } answered || answered != asked)
            {
                return ProviderErrors.InvalidResponse(Name);
            }

            string? sic = root.TryGetProperty("sic", out JsonElement sicValue) && sicValue.ValueKind == JsonValueKind.String && FourDigits().IsMatch(sicValue.GetString() ?? string.Empty)
                ? sicValue.GetString()
                : null;
            string? fiscalYearEnd = root.TryGetProperty("fiscalYearEnd", out JsonElement endValue) && endValue.ValueKind == JsonValueKind.String && IsMonthDay(endValue.GetString())
                ? endValue.GetString()
                : null;
            return new CompanyProfile(
                asked,
                name,
                [.. StringList(root, "tickers", text => Ticker.Create(text).IsSuccess).Select(text => text.ToUpperInvariant()).Distinct(StringComparer.Ordinal)],
                StringList(root, "exchanges", text => text.Length is >= 1 and <= 32 && Text().IsMatch(text)),
                sic,
                Bounded(root, "sicDescription", 100),
                fiscalYearEnd);
        }
        catch (JsonException)
        {
            return ProviderErrors.InvalidResponse(Name);
        }
    }

    private static bool IsMonthDay(string? text) =>
        text is { Length: 4 }
        && int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month)
        && int.TryParse(text.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int day)
        && month is >= 1 and <= 12
        && day >= 1 && day <= DateTime.DaysInMonth(2024, month);

    // filings.recent holds parallel arrays, one entry per filing. A row whose number, form or date is not valid is dropped and counted.
    private Result<IReadOnlyList<Filing>> ParseFilings(string body, FilingsRequest request, string[] forms)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, _json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("filings", out JsonElement filings)
                || filings.ValueKind != JsonValueKind.Object
                || !filings.TryGetProperty("recent", out JsonElement recent)
                || recent.ValueKind != JsonValueKind.Object
                || CikOf(root, "cik") is not { } answered
                || answered != request.Cik)
            {
                return ProviderErrors.InvalidResponse(Name);
            }

            JsonElement[] numbers = Column(recent, "accessionNumber");
            JsonElement[] formsColumn = Column(recent, "form");
            JsonElement[] filedOn = Column(recent, "filingDate");
            JsonElement[] reportedOn = Column(recent, "reportDate");
            JsonElement[] documents = Column(recent, "primaryDocument");
            // The number, the form and the day are what makes a filing: a list without one of those columns is not a list of filings.
            if (!HasColumns(recent, "accessionNumber", "form", "filingDate"))
            {
                return ProviderErrors.InvalidResponse(Name);
            }

            int rows = new[] { numbers.Length, formsColumn.Length, filedOn.Length }.Min();
            if (rows > _maxRows)
            {
                return ProviderErrors.InvalidResponse(Name);
            }

            var found = new List<Filing>();
            int dropped = 0;
            for (int i = 0; i < rows; i++)
            {
                Filing? filing = BuildFiling(request.Cik, numbers[i], formsColumn[i], filedOn[i], i < reportedOn.Length ? reportedOn[i] : default, i < documents.Length ? documents[i] : default);
                if (filing is null)
                {
                    dropped++;
                }
                else if ((forms.Length == 0 || forms.Contains(filing.Form, StringComparer.OrdinalIgnoreCase)) && (request.Since is null || filing.FilingDate >= request.Since))
                {
                    found.Add(filing);
                }
            }

            if (dropped > 0)
            {
                LogRowsDropped(_filings, dropped, rows);
            }

            return Result.Success<IReadOnlyList<Filing>>([.. found
                .OrderByDescending(filing => filing.FilingDate)
                .ThenByDescending(filing => filing.AccessionNumber, StringComparer.Ordinal)
                .Take(request.Limit)]);
        }
        catch (JsonException)
        {
            return ProviderErrors.InvalidResponse(Name);
        }
    }

    private static bool HasColumns(JsonElement recent, params string[] properties) =>
        properties.All(property => recent.TryGetProperty(property, out JsonElement column) && column.ValueKind == JsonValueKind.Array);

    private static JsonElement[] Column(JsonElement recent, string property) =>
        recent.TryGetProperty(property, out JsonElement column) && column.ValueKind == JsonValueKind.Array ? [.. column.EnumerateArray().Take(_maxRows + 1)] : [];

    private static Filing? BuildFiling(Cik cik, JsonElement number, JsonElement form, JsonElement filedOn, JsonElement reportedOn, JsonElement document)
    {
        string? accession = number.ValueKind == JsonValueKind.String ? number.GetString()?.Trim() : null;
        string? kind = form.ValueKind == JsonValueKind.String ? form.GetString()?.Trim() : null;
        if (accession is null || !AccessionPattern().IsMatch(accession) || kind is null || !FilingRules.IsValidForm(kind) || ParseDate(filedOn) is not { } filed)
        {
            return null;
        }

        string? primary = document.ValueKind == JsonValueKind.String && document.GetString()?.Trim() is { } name && DocumentPattern().IsMatch(name) ? name : null;
        Uri? address = primary is null
            ? null
            : new Uri(string.Create(CultureInfo.InvariantCulture, $"https://www.sec.gov/Archives/edgar/data/{cik.Number}/{accession.Replace("-", string.Empty, StringComparison.Ordinal)}/{primary}"));
        return new Filing(accession, kind, filed, ParseDate(reportedOn), primary, address);
    }

    private static DateOnly? ParseDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : null;

    private async Task<Result<string>> GetAsync(string address, string operation, CancellationToken cancellationToken)
    {
        EdgarOptions settings = _options.Value;
        await _gate.WaitAsync(TimeSpan.FromMilliseconds(settings.MinRequestIntervalMilliseconds), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using HttpClient client = _clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode ? body : Fail(Classify(response), operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or RateLimiterRejectedException or OperationCanceledException)
        {
            // The type is enough to log: the message of a transport exception can carry the address.
            LogTransportFailure(ex.GetType().Name);
            return Fail(ProviderErrors.Unavailable(Name), operation);
        }
    }

    private Error Classify(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.TooManyRequests => ProviderErrors.RateLimited(Name, response.Headers.RetryAfter?.Delta),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrors.Unauthorized(Name),
        HttpStatusCode.NotFound => ProviderErrors.SymbolNotFound(Name, "the company"),
        _ => ProviderErrors.Unavailable(Name),
    };

    private static Error Fail(Error error, string operation)
    {
        Count(operation, ProviderErrors.Classify(error) switch
        {
            ProviderFailureKind.RateLimited => "ratelimited",
            ProviderFailureKind.SymbolNotFound => "notfound",
            ProviderFailureKind.Transient => "transient",
            _ => error.Code == ProviderErrors.UnauthorizedCode ? "unauthorized" : "failed",
        });
        return error;
    }

    private async Task KeepAsync(string operation, string query, string body, CancellationToken cancellationToken)
    {
        try
        {
            await _payloads.StoreAsync(new ProviderPayload(Name, operation, null, query, null, body, _clock.GetUtcNow()), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keeping the raw body is best effort: the data was obtained and parsed, so a store that is down never fails the call.
            LogPayloadNotKept(ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 2701, Level = LogLevel.Warning, Message = "EDGAR {Operation}: {Dropped} of {Rows} rows were not valid and were dropped")]
    private partial void LogRowsDropped(string operation, int dropped, int rows);

    [LoggerMessage(EventId = 2702, Level = LogLevel.Warning, Message = "EDGAR could not be reached ({ExceptionType})")]
    private partial void LogTransportFailure(string exceptionType);

    [LoggerMessage(EventId = 2703, Level = LogLevel.Warning, Message = "The raw EDGAR response was not kept ({ExceptionType}); the call is not affected")]
    private partial void LogPayloadNotKept(string exceptionType);
}
