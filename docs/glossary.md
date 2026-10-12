# Glossary (ubiquitous language)

- **Purpose:** shared vocabulary; code names match these terms (ADR-0013).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 (grows with each module)

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
