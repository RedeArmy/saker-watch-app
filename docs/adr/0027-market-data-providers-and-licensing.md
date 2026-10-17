# ADR-0027: Market data providers, routing and licensing

- **Status:** proposed
- **Date:** 2026-10-16
- **Deciders:** Eder

## Context
The product is global by design (ADR-0018) but starts with US equities and ETFs. Phase 2 needs daily adjusted prices, a symbol check, splits and dividends, with no paid service while the app is not in production. The owner's constraints: free tiers for the initial phase and real tests, no monthly payment until production, markets added later without a rewrite.

Facts checked on 2026-10-16 from the providers' own pages (they change; re-check before paying or opening the app to other users):
- **Twelve Data, Basic (free):** 8 credits per minute, 800 per day, US equities, forex and crypto. `time_series` costs 1 credit per symbol, accepts `outputsize` up to 5000 and `start_date`/`end_date`, and has `adjust` = `all | splits | dividends | none` (default `splits`). The documentation does not say whether `adjust` is restricted on the free plan. Errors come as JSON with `code`, `message`, `status`; `429` means the limit was reached. `/exchanges`, `/stocks` and `/symbol_search` cost 1 credit per request. Paid plans start at 29 USD per month (Grow: 55 credits per minute, end-of-day global equities and ETFs, "20+" markets); Pro (99 USD) adds real-time Europe. The pages name neither Canada nor Japan. Individual plans are "personal, internal and non-commercial". The terms forbid using free-tier data for commercial purposes, allow storing data for internal use, forbid redistribution or display to third parties without an add-on, and require deleting data within 30 days of terminating the subscription.
- **EODHD, Sandbox (free):** 20 calls per day and 20 per minute, one year of end-of-day history, 15-minute delayed prices, dividends and splits included; the page does not say which tickers the free key covers. Historian (EOD all-world) costs 19.99 USD per month (16.58 billed yearly), 100 000 calls per day, 30+ years of history. The page does not list exchanges; its terms were not readable.
- **Yahoo-style endpoints (yfinance):** unofficial, no contract, personal use only, fragile.
- **Alpha Vantage:** 25 calls per day free; paid from 49.99 USD.

## Options considered
1. One provider for everything (EODHD paid). Simplest data, but costs money before production and cannot be tested for free beyond 20 calls a day.
2. Twelve Data free for the US now, EODHD later for the other markets (chosen).
3. Yahoo as the main source. Free and broad, but unofficial, fragile and not usable beyond one user.

## Decision
- **Twelve Data free is the first real provider, for US listings only.** It is built behind `IMarketDataProvider` (symbol check, daily adjusted OHLCV for a range, latest quote) with a deterministic fake and recorded-fixture contract tests.
- **A router chooses the provider by market.** Configuration maps each MIC to a provider (`XNYS`, `XNAS`, `ARCX`, `BATS` to Twelve Data). A market with no provider is disabled by a feature flag; nothing else changes to add one. `EodhdProvider` is added when a non-US market is enabled, first against fixtures; the owner may run a short manual check with the free key (list the exchanges, fetch one listing per exchange) before deciding to pay.
- **One listing, one source.** Adjusted closes from two vendors differ in dividend handling and rounding, so a series never mixes them. Every `price_daily` row keeps its `source`; switching the provider of a listing deletes and re-ingests its whole history.
- **Rate budgets per provider** live in a shared Redis token bucket (Twelve Data: 8 per minute and 800 per day, with a safety margin). Backfill is chunked and resumable, so a larger watchlist than the daily budget finishes over several days.
- **Outbound calls only through named `HttpClientFactory` clients** (host allow-list, timeouts, size limit, retries, circuit breaker). API keys come from configuration (environment or Docker secrets), are redacted from logs and never stored in the repository. Provider text is untrusted data and is validated before it is stored.
- **Raw responses** are kept in Mongo `provider_payloads` with a TTL, so a parsing change can be replayed without calling the provider again.
- **Licensing gate:** free tiers are for personal, non-commercial use, so with one user the app stays within their terms. Opening the app to other users, or any commercial use, requires a plan whose terms allow it (display to third parties, storage) and a new ADR; the Twelve Data requirement to delete data within 30 days of ending the subscription is recorded in `docs/runbooks/provider-outage.md` when written.
- **Yahoo** is not adopted. It may be added later as an optional, flag-off adapter for personal use only, never as the only source.

## Verified with a real key (2026-10-16, free plan, `scripts/verify-twelvedata.sh`, 13 requests)
- **History:** one `time_series` request with `outputsize=5000` returned 5 000 daily rows for AAPL (2006-11-30 to 2026-10-16), ascending, no duplicates. The whole history of a listing costs 1 credit.
- **Adjustment:** `adjust=all` works on the free plan and adjusts open, high, low and close for splits and dividends (AAPL closes of 2020-08-28 are below the split-adjusted raw close). The response has a single `close`; there is no separate raw and adjusted close, so the raw series needs a second request with `adjust=none` (only needed for the latest price, which equals the adjusted one on the last day). `splits` and `none` were identical over a window with no split. NVDA around its June 2024 10-for-1 split comes back continuous.
- **Latest bar is provisional:** the bar of the current day, fetched after the US close, had a volume of 1.5 million for AAPL against 25 to 50 million on the previous days. A day is final only on a later fetch.
- **Volume is not reliable** (the default US feed comes from venues with a small share of volume); the product never uses volume for decisions.
- **Non-US on the free plan is blocked:** SHOP on TSX and SAP on XETR return HTTP 404 with "This symbol is available starting with the Grow or Venture plan"; a plan restriction is an HTTP 404 and is told apart from an invalid symbol only by the message text ("symbol or figi parameter is missing or invalid"). `/exchanges` lists 97 exchanges, including TSX (`XTSE`), Tokyo (`XJPX`), Xetra (`XETR`), Euronext Paris (`XPAR`) and London (`XLON`), without saying which are allowed; the Tokyo request in the script used a wrong code (`XTKS`) and was not conclusive (fixed in the script).
- **MIC codes are operating segments:** AAPL is `XNGS` (Nasdaq Global Select) and SPY is `ARCX`, not `XNAS`. The router and `symbol_aliases` map provider MICs to the listing's market (`XNGS`, `XNMS`, `XNCM` to `XNAS`), keeping the provider's code in the alias.
- **Cross-listings:** `symbol_search` returns one row per listing with `mic_code`, `currency` and country (SHOP on Nasdaq in USD, on TSX in CAD, as a CEDEAR in ARS); a ticker alone is ambiguous, so the catalog key is `(MIC, ticker)`. `/stocks?exchange=NYSE` lists 3 073 US symbols; ISIN and CUSIP are an add-on.
- **`end_date` looks exclusive:** asking NVDA for 2024-06-01 to 2024-06-20 returned bars up to 2024-06-18 only (June 19 was a holiday, June 20 a session). The adapter therefore asks for the day after the last wanted day and filters the range again on what comes back, which is correct whichever way the bound works.
- **Budget signals:** every response carries `api-credits-left`, `api-credits-used` (this minute) and `api-credits-request`; `/api_usage` returns `current_usage`, `plan_limit` (8), `daily_usage` and `plan_daily_limit` (800). The JSON error body has `code`, `message`, `status`.
- **Still unverified:** non-US data on a paid plan, the dividends and splits endpoints, and the licence text for display to other users.

## Consequences
- Phase 2 ships with US end to end; Canada, Europe and Japan are modelled (MIC, currency, time zone, calendar) but off by flag until a provider is chosen and paid.
- Real-provider behavior stays unverified until the owner supplies a key; fixtures are marked as recorded from documentation until then.
- Two vendors mean two symbol formats and two sets of fixtures; `symbol_aliases` and the adapters absorb that.
