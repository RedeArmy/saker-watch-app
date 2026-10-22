# Glossary (ubiquitous language)

- **Purpose:** shared vocabulary; code names match these terms (ADR-0013).
- **Owner:** Eder · **Status:** approved · **Target phase:** 1 (grows with each module)

| Term | Meaning |
|---|---|
| Stock / Listing (same concept; "listing" in the domain model, "stock" in the UI) | A security traded on one exchange, identified by `(MIC, ticker)`; has a native currency and time zone |
| MIC | ISO 10383 market identifier code of an exchange |
| Watchlist item | A user's tracked listing with notes and optional target price |
| Performance window | One of 1D, 1W, 1M, 3M, 6M, YTD, 1Y, 5Y computed on adjusted closes in native currency |
| Adjusted close | Closing price corrected for splits and dividends |
| Staleness | Age of the latest price; the SLO requires it under 24 h on trading days, and the UI shows `staleSince` when exceeded |
| Analysis run | One execution of a prompt version for a stock on a date; shared across users |
| Prompt template | Versioned, checksummed data defining an analysis and its output schema |
| Signal | Deterministic classification `Interesting`, `Neutral` or `Avoid` from the rules engine, with reasons |
| Rule set | Versioned configuration of signal rules and thresholds |
| Partial purchase | Buying a fraction of a share for a given amount; fraction truncated to 6 decimals |
| FX path | Conversion route to GTQ (direct or via USD) with its as-of date and source |
| Organization (Clavaris) | The tenant that owns Saker Watch's accounts in the identity provider |
| Tombstone | Minimal record kept after local deletion until the identity-provider deletion completes |
| Account (Clavaris) vs User (ours) | An Account is the identity in Clavaris; a User is our `identity.users` row linked by `(issuer, sub)` |
| Job | A unit of background work in `platform.job_queue` with a deterministic job key |
| Lease | Time-limited claim of a job by a worker, protected by a fencing token |
| Soft-deleted | A user in `pending_deletion` during the 30-day grace period |
| Calculation | The partial-purchase computation (also called calculator); "partial share" in prose means a fractional share |
| Portfolio | A named group of the user's watchlist stocks (many to many, optional); at most 20 per user and 100 stocks each (ADR-0026) |
| Delisted | A stock that no longer trades: inactive, with the moment it stopped; its last price and history are kept and it never goes stale (ADR-0039) |
| Ticker history | The tickers a stock used to have, kept so an old ticker still finds the stock after a change of ticker (ADR-0039) |
| Provider fault | Many symbols of one market unknown to the provider at once: taken as a failure of the provider, so nothing is delisted automatically (ADR-0044) |
| Sparkline | A small price chart of the last stored days of a stock, drawn from adjusted closes (ADR-0038) |
| Money kit | `Money`, `ExchangeRate`, `Shares` and `RelativeChange`: exact `decimal` values that are rounded only for display (ADR-0037) |
| Broker fee (`feePercent`) | The optional commission of the calculator, a percentage of the value of the shares; with an amount, the amount is the total paid, fee included (ADR-0036) |
| Stamp | `ingestion_state.last_success_at` of a listing: the version of its data, part of the key of its cached performance windows (ADR-0041) |
| Rate guard | The screening of the exchange rates before they are stored (band, daily change, no silent rewrite); a refused rate is kept apart and never served (ADR-0042) |
| Credit lane | Whose share of the provider's daily credits a call spends: the scheduled refresh, or the on-demand history of a new stock, which has an allowance of its own (ADR-0043) |

