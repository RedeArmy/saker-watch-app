using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Infrastructure.Providers.Banguat;

/// <summary>
/// <see cref="IFxRateProvider"/> over the SOAP 1.1 service of the Banco de Guatemala (ADR-0028). The service is public and the bank says it is
/// not an official source, so the answer is untrusted: the XML is read with DTDs and external entities off and a size cap, a row that is not a
/// rate is counted and dropped, never repaired, and the body of an error is classified, never copied into a message.
/// </summary>
public sealed partial class BanguatFxProvider : IFxRateProvider
{
    /// <summary>Name of the named <see cref="HttpClient"/>.</summary>
    public const string ClientName = "banguat";

    private const string _operation = "TipoCambioRango";
    private const string _metricOperation = "fx_daily";
    private const string _namespace = "http://www.banguat.gob.gt/variables/ws/";
    private const string _soapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string _dateFormat = "dd/MM/yyyy";

    // The bank's code for the US dollar in the answer (VariablesDisponibles, 2026-10-17). Its USD rows have a sale and a purchase rate that are
    // equal and equal to the daily reference.
    private const string _usdCode = "2";

    // Ten years are about 3 900 rows; this is far more than any range the product asks for, and bounds what one answer can hold.
    private const int _maxRangeDays = 366 * 20;
    private const long _maxXmlCharacters = 8 * 1024 * 1024;

    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");

    private static readonly Counter<long> _calls = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_provider_calls_total",
        description: "Calls to external market data providers by provider, operation and outcome.");

    private static readonly XmlReaderSettings _xmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = _maxXmlCharacters,
        MaxCharactersFromEntities = 0,
    };

    private readonly IHttpClientFactory _clients;
    private readonly IProviderPayloadStore _payloads;
    private readonly TimeProvider _clock;
    private readonly ILogger<BanguatFxProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="clients">Source of the named, allow-listed client.</param>
    /// <param name="payloads">Keeps the raw body of every successful response.</param>
    /// <param name="clock">Injected clock, for the time a response was fetched.</param>
    /// <param name="logger">Logger.</param>
    public BanguatFxProvider(IHttpClientFactory clients, IProviderPayloadStore payloads, TimeProvider clock, ILogger<BanguatFxProvider> logger)
    {
        _clients = clients;
        _payloads = payloads;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => BanguatOptions.ProviderName;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<FxRate>>> GetDailyAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentNullException.ThrowIfNull(quote);
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Provider.Banguat.GetDaily");

        if (baseCurrency != _usd || quote != _gtq)
        {
            return Error.Validation("marketdata.fx.pair_not_supported", "The Banco de Guatemala source serves USD to GTQ only.");
        }

        if (to < from || to.DayNumber - from.DayNumber > _maxRangeDays)
        {
            return Error.Validation("marketdata.fx.range_invalid", "The range must end on or after its start and span at most twenty years.");
        }

        Result<string> body = await PostAsync(from, to, cancellationToken);
        if (!body.IsSuccess)
        {
            return Fail(body.Error!);
        }

        Result<IReadOnlyList<FxRate>> rates = Parse(body.Value, from, to);
        if (!rates.IsSuccess)
        {
            return Fail(rates.Error!);
        }

        await KeepAsync(
            new ProviderPayload(Name, _metricOperation, null, $"USD/GTQ {from:yyyy-MM-dd}..{to:yyyy-MM-dd}", null, body.Value, _clock.GetUtcNow()),
            cancellationToken);
        _calls.Add(1, Tags("success"));
        return rates;
    }

    private static KeyValuePair<string, object?>[] Tags(string outcome) =>
        [new("provider", BanguatOptions.ProviderName), new("operation", _metricOperation), new("outcome", outcome)];

    private static string Envelope(DateOnly from, DateOnly to) =>
        $"""<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="{_soapEnvelopeNamespace}"><soap:Body><{_operation} xmlns="{_namespace}"><fechainit>{from.ToString(_dateFormat, CultureInfo.InvariantCulture)}</fechainit><fechafin>{to.ToString(_dateFormat, CultureInfo.InvariantCulture)}</fechafin></{_operation}></soap:Body></soap:Envelope>""";

    private static Result<IReadOnlyList<FxRate>> Fail(Error error)
    {
        _calls.Add(1, Tags(ProviderErrors.Classify(error).ToString().ToLowerInvariant()));
        return error;
    }

    private async Task<Result<string>> PostAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            Content = new StringContent(Envelope(from, to), System.Text.Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_namespace}{_operation}\"");

        try
        {
            using HttpClient client = _clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode ? body : ClassifyFailure(response, body);
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

    // A SOAP fault from the client side means our request is not what the service expects (a permanent failure we must fix); any other
    // failed answer is the service being unwell, which is worth retrying.
    private Error ClassifyFailure(HttpResponseMessage response, string body)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ProviderErrors.RateLimited(Name, response.Headers.RetryAfter?.Delta);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderErrors.Unauthorized(Name);
        }

        return FaultCode(body) is { } code && code.EndsWith("Client", StringComparison.Ordinal)
            ? ProviderErrors.InvalidResponse(Name)
            : ProviderErrors.Unavailable(Name);
    }

    private static string? FaultCode(string body)
    {
        XDocument? document = TryLoad(body);
        return document?.Descendants(XName.Get("Fault", _soapEnvelopeNamespace)).FirstOrDefault()?.Element("faultcode")?.Value.Trim();
    }

    private static XDocument? TryLoad(string body)
    {
        try
        {
            using var text = new StringReader(body);
            using var reader = XmlReader.Create(text, _xmlSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // Reads the rows of the answer. A row that is not a USD rate (another currency, a date or a number that does not parse, a rate that is not
    // positive) is dropped and counted; a valid row outside the range is ignored; a repeated date keeps the last row.
    private Result<IReadOnlyList<FxRate>> Parse(string body, DateOnly from, DateOnly to)
    {
        XDocument? document = TryLoad(body);
        XElement? result = document?.Descendants(XName.Get("TipoCambioRangoResult", _namespace)).FirstOrDefault();
        if (result is null)
        {
            return ProviderErrors.InvalidResponse(Name);
        }

        var byDay = new SortedDictionary<DateOnly, FxRate>();
        int rejected = 0;
        foreach (XElement row in result.Descendants(XName.Get("Var", _namespace)))
        {
            if (!TryReadRow(row, out DateOnly day, out decimal value))
            {
                rejected++;
            }
            else if (day >= from && day <= to)
            {
                Result<FxRate> rate = FxRate.Create(_usd, _gtq, day, value, Name);
                if (rate.IsSuccess)
                {
                    byDay[day] = rate.Value;
                }
                else
                {
                    rejected++;
                }
            }
        }

        if (rejected > 0)
        {
            LogRowsRejected(rejected);
        }

        return byDay.Values.ToList();
    }

    private static bool TryReadRow(XElement row, out DateOnly day, out decimal value)
    {
        day = default;
        value = 0m;
        return Child(row, "moneda") == _usdCode
            && DateOnly.TryParseExact(Child(row, "fecha"), _dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
            && decimal.TryParse(Child(row, "venta"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static string? Child(XElement row, string name) => row.Element(XName.Get(name, _namespace))?.Value.Trim();

    private async Task KeepAsync(ProviderPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            await _payloads.StoreAsync(payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPayloadNotKept(ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "The Banco de Guatemala answer had {Rejected} rows that are not a USD rate in the range; they were dropped")]
    private partial void LogRowsRejected(int rejected);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning, Message = "The Banco de Guatemala call failed in transit ({ExceptionType})")]
    private partial void LogTransportFailure(string exceptionType);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "The raw Banco de Guatemala response could not be kept ({ExceptionType}); the data itself was fetched and parsed")]
    private partial void LogPayloadNotKept(string exceptionType);
}
