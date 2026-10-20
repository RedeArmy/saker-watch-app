# Product specification — Saker Watch

- **Purpose:** what the product does, for whom, and the non-functional targets it must meet.
- **Owner:** Eder · **Status:** approved · **Target phase:** 0

## 1. Product
A personal stock **watchlist and research assistant**. It does **not** execute trades and never connects to a broker. One user today; multi-user ready by design. UI language Spanish (`es-GT`, i18n-ready); code, comments, logs, ADRs and commits in English.

## 2. Functional scope
### 2.1 Watchlist
- Maintain many stocks; sort and filter; paginate; show current price and price change over **1D, 1W, 1M, 3M, 6M, YTD, 1Y, 5Y**.
- Per item: notes and optional target price. List supports ETag/conditional GET.
- Performance is computed from **adjusted daily closes**, correct on non-trading days and short histories (`null` plus a reason, never fabricated). Window definitions: ADR-0006.

### 2.2 Calculator (USD and other quote currencies → GTQ)
- Endpoint `POST /v1/calculations/partial-purchase` (pure computation: no `Idempotency-Key` needed; the logic is also usable client-side). Input `{ticker|price, amount, currency}`; output (camelCase, decimals as strings) `{quoteCurrency, priceNative, fxPath, fxRate, fxAsOf, fxSource, costOneShareGtq, sharesFraction, amountNative, amountGtq}`. `quoteCurrency` is the stock's native currency (USD in the first release); the FX path is direct or via USD (ADR-0018).
- Example: USD 50 on a USD 600 share buys 0.083333 shares.
- Always show the FX date and source and a disclaimer that broker FX, fees and minimum order sizes differ. Optional fee assumption: `feePercent`, the broker's commission as a percentage of the value of the shares, part of the total paid (ADR-0036); a broker's exchange-rate spread is not modelled. Fractional-share availability is informational text, not logic.

### 2.3 Analysis agent
- Runs over every stock in the watchlist using a **self-hosted LLM** (ADR-0008) and versioned prompt templates; stores all results.
- Pipeline per stock: context builder (real data with `source` and `as_of`) → LLM analyzer → JSON-schema validation (+1 repair retry) → idempotent persistence (Mongo then Postgres) → signals engine.
- Three analyses (structural-risk scan, conviction rubric, catalysts); the LLM returns structured assessments and the deterministic scoring step computes the conviction score (ADR-0008); prompt content stays `draft` with `TODO(owner: Eder)` markers until the owner supplies final rubrics; thresholds are configuration.
- Run modes: scheduled (nightly/weekly, configurable) and on-demand per stock (rate-limited, idempotent).
- **Signal** (`Interesting | Neutral | Avoid`) is computed by a deterministic, versioned rules engine, never by the LLM. Example default (configurable): `Interesting` if conviction >= threshold AND no severe red flags AND >= 1 high-probability catalyst; `Avoid` if severe red flags; otherwise `Neutral`. Every signal stores machine-readable `reasons`.
- Analyses are shared per stock + prompt version + date, not per user.
- Evaluation harness: golden set of 20–30 stocks with human-reviewed expected outputs; run before changing model or prompt.

### 2.4 Market data
- Provider ports: market data, FX, fundamentals (adapters with fakes and recorded-fixture contract tests). Candidates (terms and limits verified before use): Twelve Data or Finnhub for prices; SEC EDGAR for filings (declared `User-Agent`, fair-access limits); Banco de Guatemala reference rate for USD/GTQ with a market FX fallback. Free tiers often forbid redistribution; findings go in a provider-licensing ADR and gate multi-user rollout.
- Daily ingestion after each market's close (market-calendar aware), backfill command, splits/dividends via adjusted prices, symbol changes and delistings. Markets and currencies: ADR-0018.
- Degrade gracefully: serve the last known price with a `staleSince` indicator instead of failing the watchlist.

### 2.5 Disclaimer
Every analysis/signal screen and API response schema carries a localized **"informational only — not financial advice"** notice. Wording lives in one resource file for legal review. Signals are research prioritization aids only.

## 3. Frontend
Authenticated Next.js app through the BFF (ADR-0007, ADR-0009). Screens by phase: **Phase 3** watchlist (sortable table, sparkline, change windows), stock detail (chart with range selector), calculator, basic settings; **Phase 4** analysis history and signal reasons on the stock detail; **Phase 6** data export and delete account. WCAG 2.1 AA, responsive, loading/error/stale states, `es-GT` i18n, optimistic updates with rollback, typed client generated from OpenAPI. Playwright E2E for the core journeys.

## 4. Non-functional targets
- **SLOs:** `GET /v1/watchlist` p95 < 300 ms at 500 items; API availability >= 99.5 % monthly; price staleness < 24 h on trading days; nightly analysis for 1,000 stocks within a configurable window (a target; feasibility depends on the LLM hardware decision, A-004).
- **Backups and DR:** daily encrypted `pg_dump` and Mongo dump off-box; RPO <= 24 h, RTO <= 4 h; scripted restore and a restore drill executed once in Phase 5.
- **Cost:** within Oracle Always Free limits (verified at deploy time) plus the separate LLM server; no paid SaaS required to run.
- **Observability and idempotency:** ADR-0016 and ADR-0017. **Security:** `docs/threat-model/` and ADR-0009/0010; target OWASP ASVS Level 2 and API Security Top 10.
- **Feature flags** (config-based) for LLM analysis, provider switches, per-market enablement and multi-user features.
- Time: all timestamps UTC (`timestamptz`), injectable `TimeProvider`, client-side conversion.
