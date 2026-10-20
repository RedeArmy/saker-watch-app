using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Companies;

namespace SakerWatch.MarketData.Application.Providers;

/// <summary>Who a listed company is, as the filer registry reports it (ADR-0040). Text from outside, validated and bounded.</summary>
/// <param name="Cik">The filer's key.</param>
/// <param name="Name">The registered name.</param>
/// <param name="Tickers">The tickers the filer trades under, upper case.</param>
/// <param name="Exchanges">The exchanges, as the registry writes them (<c>Nasdaq</c>, <c>NYSE</c>).</param>
/// <param name="Sic">The four-digit Standard Industrial Classification code, or <see langword="null"/> when the registry has none.</param>
/// <param name="SicDescription">What the code means, or <see langword="null"/>.</param>
/// <param name="FiscalYearEnd">The day the fiscal year ends as <c>MMdd</c> (<c>0926</c>), or <see langword="null"/>.</param>
public sealed record CompanyProfile(
    Cik Cik,
    string Name,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> Exchanges,
    string? Sic,
    string? SicDescription,
    string? FiscalYearEnd);

/// <summary>A document a company filed with the regulator.</summary>
/// <param name="AccessionNumber">The filing's number (<c>0000320193-24-000123</c>), unique for a filer.</param>
/// <param name="Form">The form type (<c>10-K</c>, <c>10-Q</c>, <c>8-K</c>).</param>
/// <param name="FilingDate">The day it was filed.</param>
/// <param name="ReportDate">The period it reports on, or <see langword="null"/> when it reports none.</param>
/// <param name="PrimaryDocument">The name of its main document, or <see langword="null"/> when the registry gave none that is safe to use.</param>
/// <param name="Document">The address of the main document, built from the key, the number and the name; <see langword="null"/> without a document.</param>
public sealed record Filing(string AccessionNumber, string Form, DateOnly FilingDate, DateOnly? ReportDate, string? PrimaryDocument, Uri? Document);

/// <summary>What filings to list for a company.</summary>
/// <param name="Cik">The filer's key.</param>
/// <param name="Forms">Only these form types (letter case ignored); <see langword="null"/> or empty for all of them.</param>
/// <param name="Since">Only filings on or after this day; <see langword="null"/> for all the registry keeps in its recent list.</param>
/// <param name="Limit">The most filings to return, 1 to <see cref="MaxLimit"/>, newest first.</param>
public sealed record FilingsRequest(Cik Cik, IReadOnlyList<string>? Forms, DateOnly? Since, int Limit)
{
    /// <summary>The most filings a request may ask for.</summary>
    public const int MaxLimit = 200;
}

/// <summary>
/// A source of facts about a company that do not change by the day (ADR-0040): who it is and, later, its financial statements. Like every
/// provider it reports expected failures as <see cref="Result"/> values built with <see cref="ProviderErrors"/>, and everything it returns is
/// untrusted text.
/// </summary>
public interface IFundamentalsProvider
{
    /// <summary>The name the configuration and the metrics use for this provider.</summary>
    string Name { get; }

    /// <summary>The profile of the company that trades under <paramref name="ticker"/>; an unknown ticker is a symbol-not-found failure.</summary>
    Task<Result<CompanyProfile>> GetProfileAsync(string ticker, CancellationToken cancellationToken);
}

/// <summary>A source of the documents companies file with the regulator (ADR-0040).</summary>
public interface IFilingsProvider
{
    /// <summary>The name the configuration and the metrics use for this provider.</summary>
    string Name { get; }

    /// <summary>The filings of a company, newest first, narrowed by the request. A company the registry does not know is a symbol-not-found failure.</summary>
    Task<Result<IReadOnlyList<Filing>>> GetFilingsAsync(FilingsRequest request, CancellationToken cancellationToken);
}

/// <summary>The rules about a request for filings that every provider applies the same way, so a fake and the real one cannot disagree (ADR-0040).</summary>
public static partial class FilingRules
{
    /// <summary>Code of the failure for a limit outside what a request may ask.</summary>
    public const string LimitInvalidCode = "marketdata.filings.limit_invalid";

    /// <summary>Code of the failure for a form type that is not one.</summary>
    public const string FormInvalidCode = "marketdata.filings.form_invalid";

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 /\-]{0,11}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex FormPattern();

    /// <summary>True for a form type as the registry writes it: letters, digits, spaces, '-' and '/', up to twelve characters.</summary>
    public static bool IsValidForm(string form) => FormPattern().IsMatch(form);

    /// <summary>The failure of a request that is not valid, or <see langword="null"/> when it is.</summary>
    public static Error? Check(FilingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > FilingsRequest.MaxLimit)
        {
            return Error.Validation(LimitInvalidCode, $"A request asks for 1 to {FilingsRequest.MaxLimit} filings.");
        }

        return (request.Forms ?? []).Any(form => !IsValidForm(form.Trim()))
            ? Error.Validation(FormInvalidCode, "A form type is letters, digits, spaces, '-' and '/', up to twelve characters.")
            : null;
    }
}
