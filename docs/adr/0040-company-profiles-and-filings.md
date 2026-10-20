# ADR-0040: Company profiles and filings from the SEC (ports, a fake and an adapter that is off)

- **Status:** proposed
- **Date:** 2026-10-20
- **Deciders:** Eder

## Context
The product spec (section 2.4) names provider ports for market data, FX and fundamentals, and the SEC's EDGAR for filings, with a declared `User-Agent` and its fair access limits. Market data and FX exist; fundamentals and filings did not. The analysis agent (Phase 4) will need who a company is and what it has filed, and the owner wanted the ports and an adapter prepared before that phase, without tables, endpoints or any call to the SEC until the owner decides to make them.

## Options considered
- **Wait for Phase 4.** Nothing to maintain meanwhile, but the context builder would be written against a provider that has not been tried.
- **Ports and a fake only.** Cheap and safe, but the shape of the registry's answers would be guessed again in Phase 4.
- **Ports, a fake and an adapter that is off, with a live check.** Chosen: the adapter and its failure handling are written and tested now; nothing calls the SEC until `Edgar:Enabled` is on with an identity.
- **Financial statements (XBRL company facts) now.** A large and separate model (concepts, units, periods); decided with the analysis in Phase 4.

## Decision
- **Two ports in `MarketData.Application.Providers`:** `IFundamentalsProvider.GetProfileAsync(ticker)` (the profile of a company: key, name, tickers, exchanges, industry code and description, end of the fiscal year) and `IFilingsProvider.GetFilingsAsync(request)` (the filings of a company, newest first, narrowed by form types, a start day and a limit of 1 to 200). They report expected failures as `Result`s built with `ProviderErrors`, like the market data provider: an unknown ticker or company is symbol-not-found, a 429 is a rate limit with its wait, a refused identity is unauthorized, an outage is transient, and an answer that cannot be understood is invalid. Financial statements are an extension of the first port, in Phase 4.
- **A `Cik` value object** (one to ten digits, kept as ten) is the key of a company, checked before it reaches an address.
- **The adapter (`EdgarProvider`) reads the public JSON of the SEC:** the map of tickers (`www.sec.gov/files/company_tickers.json`, kept in memory for `Edgar:TickerMapHours`, 24 by default) and the submissions of a filer (`data.sec.gov/submissions/CIK##########.json`), which carry both the profile and the recent filings. The address of a filing's document is built from the key, the accession number and the document name, each checked first.
- **The SEC's fair access policy is kept by construction.** Every request carries the declared identity (`Edgar:UserAgent`, a name and a contact address, required when the adapter is on, printable characters only, never logged); a gate keeps requests at least `Edgar:MinRequestIntervalMilliseconds` apart (120, eight a second, under the SEC's ten) across all callers of the process; the named client reaches only `www.sec.gov` and `data.sec.gov`, over https, with a 16 MB cap on the answer and timeouts.
- **What comes back is untrusted.** Text must match a conservative pattern and a size; a row of tickers or filings that is not valid is dropped and counted (log 2701), never repaired; the answer must be about the key that was asked for; the body of an error is classified and never copied into a message or a log. The raw body of a profile or filings answer is kept for lineage in the payload store (best effort); the large map of tickers is not.
- **It is off.** `Edgar:Enabled` is false by default and nothing is registered, not even the named client, so nothing can call the SEC; the settings are validated at startup either way, and an adapter turned on without a contact address stops the host. Consumers come with Phase 4.
- **A deterministic fake** (`FakeFundamentalsProvider`) stands in everywhere a test needs the ports, and the same contract tests run against it and against the adapter, so they cannot drift apart. The fixtures of the adapter have the shape of the SEC's documentation and are marked synthetic: **the real service was not called** (assumption A-028). `scripts/run-live-edgar.sh` runs a live check with the owner's identity and is skipped in CI.

## Consequences
- Phase 4 can write its context builder against ports that are tested and can be swapped for the fake.
- The shape of the SEC's answers is documented, not verified, until the live check is run once with a real identity; a difference shows as invalid answers, not as wrong data.
- The SEC may change a field or block an identity it does not like: the adapter treats both as failures that are classified, counted and logged without the body.
- A company the registry lists under several tickers is found by any of them; one ticker listed for several companies keeps the first row.
