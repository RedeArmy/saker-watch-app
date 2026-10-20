using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Companies;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Providers.Fake;

/// <summary>
/// A deterministic <see cref="IFundamentalsProvider"/> and <see cref="IFilingsProvider"/> for tests, CI and local runs without calling the SEC
/// (ADR-0040). Companies are added with <see cref="WithCompany"/>; their filings are generated from their key, so the same company always has the
/// same filings: a 10-K every year, three 10-Qs and an 8-K, each on a fixed day. Failures can be scripted.
/// </summary>
public sealed class FakeFundamentalsProvider : IFundamentalsProvider, IFilingsProvider
{
    /// <summary>The name the configuration and the metrics use for this provider.</summary>
    public const string ProviderName = "fake-edgar";

    private readonly Dictionary<string, CompanyProfile> _byTicker = new(StringComparer.Ordinal);
    private readonly Dictionary<Cik, CompanyProfile> _byCik = [];
    private readonly Queue<Error> _scriptedFailures = new();
    private readonly int _firstYear;
    private readonly int _lastYear;
    private int _calls;

    /// <summary>Creates the provider with filings from <paramref name="firstYear"/> to <paramref name="lastYear"/>.</summary>
    public FakeFundamentalsProvider(int firstYear = 2020, int lastYear = 2025)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstYear, lastYear);
        _firstYear = firstYear;
        _lastYear = lastYear;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <summary>How many calls reached the provider, failed or not.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Adds a company.</summary>
    public FakeFundamentalsProvider WithCompany(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _byCik[profile.Cik] = profile;
        foreach (string ticker in profile.Tickers)
        {
            _byTicker[ticker] = profile;
        }

        return this;
    }

    /// <summary>Makes the next call fail with <paramref name="error"/>; failures are used in the order they were scripted.</summary>
    public FakeFundamentalsProvider FailNext(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _scriptedFailures.Enqueue(error);
        return this;
    }

    /// <inheritdoc />
    public Task<Result<CompanyProfile>> GetProfileAsync(string ticker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        if (_scriptedFailures.TryDequeue(out Error? failure))
        {
            return Task.FromResult(Result.Failure<CompanyProfile>(failure));
        }

        Result<Ticker> symbol = Ticker.Create(ticker);
        if (!symbol.IsSuccess)
        {
            return Task.FromResult(Result.Failure<CompanyProfile>(symbol.Error!));
        }

        return Task.FromResult(_byTicker.TryGetValue(symbol.Value.Value, out CompanyProfile? profile)
            ? Result.Success(profile)
            : Result.Failure<CompanyProfile>(ProviderErrors.SymbolNotFound(Name, symbol.Value.Value)));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Filing>>> GetFilingsAsync(FilingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        if (_scriptedFailures.TryDequeue(out Error? failure))
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<Filing>>(failure));
        }

        if (FilingRules.Check(request) is { } invalid)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<Filing>>(invalid));
        }

        if (!_byCik.ContainsKey(request.Cik))
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<Filing>>(ProviderErrors.SymbolNotFound(Name, request.Cik.Value)));
        }

        string[] forms = [.. (request.Forms ?? []).Select(form => form.Trim())];
        IReadOnlyList<Filing> filings =
        [
            .. Generate(request.Cik)
                .Where(filing => (forms.Length == 0 || forms.Contains(filing.Form, StringComparer.OrdinalIgnoreCase)) && (request.Since is null || filing.FilingDate >= request.Since))
                .OrderByDescending(filing => filing.FilingDate)
                .ThenByDescending(filing => filing.AccessionNumber, StringComparer.Ordinal)
                .Take(request.Limit),
        ];
        return Task.FromResult(Result.Success(filings));
    }

    private IEnumerable<Filing> Generate(Cik cik)
    {
        int sequence = 0;
        for (int year = _firstYear; year <= _lastYear; year++)
        {
            foreach ((string form, int month, int day) in new[] { ("10-Q", 2, 1), ("10-Q", 5, 2), ("8-K", 7, 15), ("10-Q", 8, 1), ("10-K", 11, 1) })
            {
                var filed = new DateOnly(year, month, day);
                string accession = $"{cik.Value}-{year % 100:D2}-{++sequence:D6}";
                string document = $"{form.ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal)}-{year}.htm";
                yield return new Filing(
                    accession,
                    form,
                    filed,
                    form == "8-K" ? null : filed.AddDays(-30),
                    document,
                    new Uri($"https://www.sec.gov/Archives/edgar/data/{cik.Number}/{accession.Replace("-", string.Empty, StringComparison.Ordinal)}/{document}"));
            }
        }
    }
}
