# ADR-0018: Multi-market and multi-currency support

- **Status:** accepted (architecture); market rollout is incremental
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
The owner wants coverage beyond US equities and ETFs: Europe (LSE, XETRA, Euronext), Canada (TSX), Latin America (BMV, B3) and Asia (exchanges not yet chosen). The first release must not hard-code USD or a single trading calendar.

## Decision
- **Identity of a listing:** `Stock` is a listing identified by `(MIC, ticker)` where MIC is the ISO 10383 market code; the instrument carries ISO 4217 `currency` (native trading currency), IANA time zone, and an `asset_type` (equity, ETF).
- **Prices stay in native currency.** Performance windows (1D..5Y) are computed on native-currency adjusted closes, so FX moves never pollute stock performance. A UI toggle may show values converted to GTQ at the as-of rate, labelled as such.
- **FX model:** rates are stored as `(base, quote, as_of_date, source)`. Conversion to GTQ uses a configurable path: direct pair if available, otherwise **USD pivot** (X→USD→GTQ), with the Banco de Guatemala USD/GTQ reference rate as the anchor. The path and date of every conversion are returned to the user.
- **Calendars and schedules:** one exchange calendar per MIC (holidays, half days, sessions); ingestion and "stale" evaluation run per market after that market's close, in its time zone. Staleness SLO is measured per market.
- **Corporate actions and symbols:** handled per listing; ticker validation allows exchange suffix formats per provider mapping (symbol mapping table `marketdata.symbol_aliases`).
- **Provider coverage and licensing gate:** a market is enabled only when (a) a provider with acceptable terms covers it (ADR for provider licensing), (b) its calendar is implemented, and (c) fixtures/contract tests exist. Markets are feature-flagged.
- **Rollout:** Phase 2 ships US equities/ETFs end to end with the model above (multi-currency-capable but only USD enabled). Each additional market is a small, flag-gated increment after Phase 2 (suggested order: TSX, Europe, LatAm, Asia as chosen).
- **Calculator:** the partial-purchase calculator accepts any supported quote currency; GTQ cost uses the FX path above.

## Consequences
More dimensions in the data model and tests (calendar per MIC, FX paths). Free provider tiers rarely cover Asia/LatAm well, so those markets may require a paid or alternative source, which conflicts with the "no paid SaaS" cost constraint; that trade-off is decided per market in the provider ADR.
