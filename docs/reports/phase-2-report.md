# Phase 2 report — Market data, watchlist, calculator

- **Purpose:** Phase Report for the Phase 2 gate (`/phase-report`): what was built and not, evidence, risks, review findings with their disposition, and the proposal for Phase 3.
- **Owner:** Eder · **Status:** draft: `security-auditor` and `migration-reviewer` ran (the second time complete), every finding has a disposition and the owner's decisions are made; `code-reviewer` is not run yet, so the gate cannot be proposed for approval · **Target phase:** 2
- **Range:** `7b5a90d` (end of Phase 1, 2026-10-16) to the head of `feat/phase-2-8-caching`. Milestones 2.0–2.7 and their follow-ups are merged to `main` (#38–#56); 2.8 is on its branch.

## 1. Built
48 commits since the Phase 1 gate (several are squashed pull requests that hold a whole milestone): by Conventional Commit type 16 feat, 9 fix, 6 build (Dependabot), 5 docs, 4 test, 4 style, 1 refactor, and 3 squashed pull requests. 339 files changed, about 37.8k lines added (source, tests, migrations, docs).

| Milestone | Content |
|---|---|
| 2.0 | Decisions before code: market data providers and licensing (ADR-0027), the FX source and conversion path (ADR-0028), the ingestion design (ADR-0029), portfolios (ADR-0026); what the Twelve Data free plan really returns, verified with a real key |
| 2.1 | The listing and its market identifiers, the US equity calendar (sessions, holidays, early closes, checked against published sessions), the `marketdata` schema: stocks, symbol aliases, corporate actions |
| 2.2 | The provider port and its failures, the router by market, the Twelve Data adapter checked against recorded responses and five live runs, the shared call budget in Redis, a deterministic fake provider, every response kept as received in Mongo |
| 2.3 | `price_daily` as a Timescale hypertable with an upsert in one statement, the ten-year backfill (adjusted and raw), the daily refresh with a scheduler by market calendar and detection of a rewritten adjusted history, the staleness rule and per-market freshness gauges |
| 2.4 | USD/GTQ from the Banco de Guatemala (SOAP), stored with date and source, direction-safe conversion paths and the daily refresh job |
| 2.5 | One performance calculator (eight windows, edge rules of ADR-0030: splits, short histories, holes), a PostgreSQL reader that finds nine prices per listing, no invented numbers |
| 2.6 | Watchlist and portfolios: on-demand stock registration, the list with performance, filters, sort and paging with ETag, portfolio management and metrics, row-level security |
| 2.7 | The money kit (exact `Money`, typed `ExchangeRate`, `Shares`, `RelativeChange`, rounding only for display; ADR-0037), the partial-purchase calculator and `POST /v1/calculations/partial-purchase`; follow-ups: optional broker fee, price history and sparklines (ADR-0038), the life of a listing: delisting, relisting and a change of ticker (ADR-0039), company profiles and filings from the SEC behind ports, a fake and an adapter that is off (ADR-0040) |
| 2.8 | The cache of the daily numbers in Redis (ADR-0041), a price-only read for the calculator, the k6 baseline with the cache on and off, the cache runbook, threat T51; and the follow-ups of the security and migration reviews: the exchange rate guard (ADR-0042), provider credit lanes and a daily cap on new stocks (ADR-0043), delisting judged against the whole market, no transaction held while a provider is asked and no HTTP retries for Twelve Data (ADR-0044), the owner exemption from `FORCE` row-level security (ADR-0010) |

Also in this phase: the worker smoke test (`scripts/smoke-worker.sh`), the freshness sampler fix (its database reads had no open connection, so its gauges were never measured), the listing-lifecycle runbook, and Dependabot bumps of the compose images and packages.

## 2. Deliberately not built
- The frontend, the BFF and every screen (Phase 3); the analysis agent, the LLM, prompts and the signals engine (Phase 4); the `Signals` module has no code.
- Fundamentals and filings storage, endpoints and any call to the SEC: only the ports, a fake and an adapter that is off (ADR-0040).
- Markets other than the US, and providers other than Twelve Data (A-027 adds Asia, Canada and Europe one market at a time).
- A cache of the price series (the sparklines are computed on read, ADR-0038; the baseline has them at 242 ms p95).
- Capacity and soak tests: the baseline is a latency test with four virtual users on one machine (Phase 5).
- Deployment, production Caddy, backups and restore drills, dashboards and alerts, retention policies for market data (Phase 5).
- Quotas, onboarding, data export and account deletion (Phase 6).

## 3. Test results (head of `feat/phase-2-8-caching`)
| Check | Result |
|---|---|
| `dotnet build -warnaserror` | 0 warnings, 0 errors |
| `dotnet format --verify-no-changes` | clean |
| `bash tests/hooks/run.sh` | 60/60 |
| `bash scripts/check-docs-index.sh`, `check-todos.sh` | OK |
| `dotnet test` (with coverage, as CI runs it) | 2 003 passed, 0 failed, 4 skipped (the opt-in live checks and the scale test): Calculator 73, Application 184, Architecture 17, Identity 76, Contract 13, Observability 41, MarketData 667, Watchlist 135, Web 127, Integration 670 (real PostgreSQL/Timescale, MongoDB and Redis through Testcontainers; 19 minutes on this machine) |
| Coverage gate (`scripts/check-coverage.py 85`) | passes for the ten gated assemblies: MarketData.Application 99.3 %, MarketData.Domain 99.2 %, Watchlist.Application 98.0 %, Watchlist.Domain 99.5 %, Calculator.Application 96.3 %, Calculator.Domain 92.5 %, Kernel 95.1 %, Identity.Application 100 %, Retention.Application 98.5 % |
| `dotnet list package --vulnerable --include-transitive` | no vulnerable packages |
| `docker compose config` of the dev and observability profiles | valid |
| Live checks, run by hand and recorded in the ADRs | Twelve Data (five runs, ADR-0027), Banco de Guatemala (A-026), SEC EDGAR (A-028) |
| Worker smoke (`scripts/smoke-worker.sh`) | `ALL OK`: start, graceful stop, no warning, the `marketdata` commands and their rows |
| Load baseline (`scripts/run-k6.sh`, `docs/reports/phase-2-load-baseline.md`) | cache on: every path under 245 ms p95 (budget 300 ms); cache off: the watchlist sorted by a window at 849 ms p95; with `K6_ENFORCE=1` the run exits 0 with the cache and 99 without |

Phase 2 exit criteria of the roadmap: performance math on fixtures including splits and short histories (ADR-0030, `PerformanceCalculatorTests`, `PerformanceReaderTests`), stale-data behavior (`PriceStatusRulesTests`, `FreshnessTests`), the provider-licensing ADR (ADR-0027) and the k6 baseline: all four have their evidence above.

Could not run here: the CI-only jobs (`api-contract` against the base branch with the real `oasdiff` image was run by hand for 2.7, SonarCloud Quality Gate, CodeQL, Trivy, gitleaks, SBOM upload), because there is no GitHub access from this environment; and the findings SonarCloud reports on each push were fixed from the lists the owner pasted, not from the gate itself.

## 4. Known risks
- **One provider, free plan** (T38): 8 credits a minute and 800 a day, enforced per distinct call; 500 stocks fit only with the daily refresh and a careful backfill. Another market needs another provider (A-027) and the licensing check of ADR-0027.
- **The cache serves what its stamp says** (T51): prices edited by hand, or a change of the exchange calendars, do not change `last_success_at`; the keys expire in 26 hours and the runbook says how to empty them.
- **The sparklines are not cached** and are the slowest path (242 ms p95 with 500 stocks); a screen that asks for the list and the sparklines together spends both.
- **The baseline is a baseline of one machine** (four virtual users, three CPUs shared with k6 and the databases, synthetic smooth data); it says nothing about capacity or about production.
- **US calendars only:** a market without a calendar gets a window less (`CalendarUnavailable`) and no daily refresh; adding a market needs its calendar checked against the published sessions.
- **Free-plan data quality:** a provider can rewrite its adjusted history (detected, and a full reload is queued) or answer "unknown symbol" for a stock that trades (a delisting needs the same answer on five different days, and every change is a row of `corporate_actions`).
- **The EDGAR adapter has never run in production configuration:** it is off, and its identity must carry a contact before it is turned on (T50).
- **CI jobs not seen from here:** SonarCloud needs `SONAR_TOKEN` in the Dependabot secret store too, or Dependabot pull requests fail the gate.
- Carried over from Phase 1: availability of Clavaris (T1/T33), `aud` equal to the client id (A-021), account state cached 5 minutes (T32), forwarded headers (A-023), the audit chain without an off-database anchor (T3).

## 5. ADRs added or changed
Added: 0027 market data providers and licensing, 0028 FX source and conversion path, 0029 ingestion design, 0030 performance windows and edge rules, 0031 watchlist and portfolios storage, 0032 on-demand stock registration, 0033 watchlist read model, 0034 portfolio API, 0035 portfolio metrics, 0036 partial-purchase calculator, 0037 money kit, 0038 price history and sparklines, 0039 listing lifecycle, 0040 company profiles and filings, 0041 cache of the daily numbers, 0042 exchange rate guard, 0043 provider credit lanes, 0044 hardening of delisting, of the registration write and of the provider retries, 0045 hardening of what the API can write to the catalog. Changed: 0010 (the table owner and `FORCE`), 0026 (portfolios, from the decision of 2026-10-16 to accepted).

0026 to 0045 are all accepted by the owner (2026-10-19, 2026-10-21 and 2026-10-22). The notes in 0030, 0033, 0035 and 0038 that said "caching arrives with 2.8" now point to it.

## 6. DOCS-INDEX status
101 approved, 2 draft, 3 stub (at the Phase 1 gate: 57 approved, 11 draft, 4 stub). The stubs are the runbooks `provider-outage`, `llm-outage` and `key-rotation`, all with target phase 5, so none is past its target (`restore` is a draft now: it has a procedure and a drill for the compose stack, and the deployed host stays in Phase 5). The 15 documents that were in review were read against the code on 2026-10-22 and corrected where they were wrong (the observability catalog, the API guide, the data model, the glossary, the local development guide, the queue and Sonar runbooks, the load baseline and the four Phase 2 runbooks); the owner approved them on 2026-10-22. The drafts are the restore runbook and this report, which stays a draft until `code-reviewer` has run and the gate is decided; the Phase 2 pointer is updated only after the owner approves.

## 7. Review findings and disposition
`security-auditor` and `migration-reviewer` ran on 2026-10-21 over `7b5a90d..HEAD`; **`code-reviewer` is still pending** by decision of the owner, so the gate is not proposed yet. Neither reviewer found a blocker: the security audit has 0 critical, 0 high, 2 medium, 8 low and 3 informational findings; the migration review approves with changes.

### security-auditor
| # | Finding | Disposition |
|---|---|---|
| M1 | One user can drain the shared Twelve Data daily quota by registering unknown tickers (10 a minute each costs a backfill credit; 800 a day), and the daily refresh has no reserved lane | **Fixed (ADR-0043, accepted)**: at most 20 new stocks per user per UTC day (`429` `marketdata.stock.registration_limit` with `Retry-After`; a stock already in the catalog costs nothing), and the history load of a new stock draws first on an on-demand allowance of 200 credits a day, so the daily refresh keeps at least 600; runbook `provider-quota`; unit, integration and mutation checks. T38 is now "mitigated" |
| M2 | The exchange rate has no plausibility or jump guard (only `rate > 0`), and a corrected value rewrites a day without a trace | **Fixed (ADR-0042, accepted)**: a band per pair (USD/GTQ 5 to 12), a 3 % limit of change per day and a refusal to replace a stored day with a very different rate; a refused rate is kept in `marketdata.fx_rates_rejected`, logged (2303) and counted, never served; runbook `fx-rates`; unit, integration and mutation checks. T40 is now "mitigated" |
| L1 | Automatic delisting depends on substring matching of the provider's error text | **Fixed (ADR-0044, accepted)**: an unknown symbol is only the documented 404, and no delisting happens while 30 % or more of a market with at least 10 active listings is unknown at once (provider fault; event 2608, outcome `provider_fault`); unit and integration tests |
| L2 | Provider names are stored with only a length check | **Fixed**: control, format (bidirectional, zero-width), separator characters and angle brackets are dropped in `Stock`; tests. A database check constraint would need a new migration and is not added |
| L3 | A note with a NUL (or other control character) makes the write fail with a 500 | **Fixed**: refused with `watchlist.item.notes_invalid` (line breaks and tabs stay); tests |
| L4 | The database connection and transaction stay open during the outbound call that registers an unknown stock (up to 30 s) | **Fixed (ADR-0044, accepted)**: the put command no longer runs in the pipeline's transaction (`ISelfTransactingCommand`); the stock is found or registered first in short units of work and the item is written in one that opens after; an integration test asks the database during the provider call and finds no session idle in a transaction, and fails with the old behaviour |
| L5 | Retries are not re-budgeted (`MaxRetries = 2` for Twelve Data, one credit reserved per logical call) | **Fixed (ADR-0044, accepted)**: the Twelve Data client is not retried by the HTTP pipeline; the job queue retries and reserves again on every attempt |
| L6 | The call budget and the search cache fail open when Redis is down | **Accepted** for the single-user phase and written in T45 (event 2003 is the alert); a local token bucket as fallback is proposed for Phase 6 |
| L7 | A provider's Retry-After was passed to clients without a cap | **Fixed**: capped at five minutes; test |
| L8 | Load and ops scripts: world-writable output directory, the stand-in provider served its signing key, the Twelve Data key in the arguments of `curl` | **Fixed**: output directory mode 700 and k6 runs as the owner's user; the provider serves only a `public` directory and the key is in a private one; the key goes through a file descriptor. New threat T52 (dev only) |
| I1 | The EDGAR regexes anchored with `$` accept a trailing line break | **Fixed**: `\z` in the EDGAR options and provider, the filing form pattern and the search text pattern; tests |
| I2 | Cache entries have no integrity protection | Accepted: the trust domain is the database and entries are read back through the domain functions (ADR-0041) |
| I3 | A provider search answer was cached without a row cap | **Fixed**: at most 50 rows are kept |

Threat-model deltas the audit asked for are in `docs/threat-model/threat-model-v1.md`: T2 (the ticker allow-list exists), T38, T40, T44 (the name is untrusted text; the search text is kept in Mongo), T45 (fail-open as a residual risk), T49, T50 (where the SEC identity lives), the new T52. One observation, not a finding: `docker-compose.no-avx.yml` defaults to `mongo:8.2` (which needs AVX) while the smoke and load scripts default to `mongo:4.4`; they set it themselves.

### migration-reviewer
| # | Finding | Disposition |
|---|---|---|
| 1 | High: `ENABLE` but not `FORCE ROW LEVEL SECURITY` on the three watchlist tables (the same in Identity and Platform); `tracked_stock_ids()` relies on the owner being exempt | **Resolved as a decision (ADR-0010)**: the owner is the migrator's login and no runtime login is or belongs to it; `FORCE` would also bind the function that reads across users. Two tests keep it safe: `RuntimeLogins_OwnNothing_AndCannotBypassRowLevelSecurity` and one that fails when a table the API can read or change holds a `user_id` without a policy |
| 2 | `MigrationsAreForwardOnlyTests` did not scan the Watchlist migrations | **Fixed** (the `Down` bodies do throw) |
| 3 | `ModelSnapshotTests` had no Watchlist case | **Fixed** |
| 4 | No seeded upgrade test for `AddListingLifecycle`, the Watchlist security step, or the real Mongo V1 to V2 | **Fixed**: three tests that seed rows at the previous version, upgrade and check the rows and the new rules |
| 5 | A retired ticker in `ticker_history` can outlive a new registration of the same ticker | **Accepted**: the catalog looks in `stocks` first and the rename path replaces the row (`ON CONFLICT DO UPDATE`); a dead row shadowed by a live stock does no harm |
| 6 | The "no PUBLIC EXECUTE" sweep left out the `watchlist` schema | **Fixed** (it passes) |
| 7 | Check constraints added without `NOT VALID` | Accepted: small catalog tables; the pattern is kept for any larger table |
| 8 | Redundant indexes on `watchlist.portfolio_items` | Deferred: negligible at the ceilings (500, 20, 100); a later migration can drop them |
| 9–11 | Revoke on tables that did not exist yet; `register_stock` poisoning surface (T44); no rollback plan inside the migrations | No change: the first is the safe failure, the second is covered, `docs/runbooks/restore.md` is still a stub of Phase 5 |

Could not verify without a running database, as the reviewer says: real lock times, `create_hypertable` with the foreign key on the exact Timescale image in production, that the deployed roles are `NOBYPASSRLS` and not owners, and the Mongo index build on a production-size collection. The integration suite covers the first three on the test container.

### migration-reviewer, second review (complete, 2026-10-22)
A fresh review of the eleven migrations of that moment (fifteen now, with the four added to answer it: `WidenFxRateColumn`, `AddCatalogTextChecks`, `RegisterStockProviderListingTaken` and `CeilingsSkipRowsThatAlreadyExist`), including `CreateFxRatesRejected` and the tests added after the first review. Verdict: approve with changes; no critical or high finding, and none of the seven earlier dispositions needed reopening. No applied migration was edited (only new files, plus the snapshot and one registration line in `V0001`).

| # | Finding | Disposition |
|---|---|---|
| M1 | `fx_rates.rate` is `numeric(20,10)` while the domain accepts 1e-12 to 1e12: a rate of a pair without a band at or above 1e10 overflows (22003) and one below 5e-11 violates the check; the whole batch fails and the job retries forever | **Fixed (ADR-0045, accepted)**: migration `WidenFxRateColumn` makes the column `numeric(30,12)` (compatible with the previous app version); a store test writes the extremes of the domain range and an upgrade test checks that every stored rate keeps its exact value |
| M2 | The restore plan is a stub; migrations are forward-only, so a backup is the only way back | **Fixed**: `scripts/dev-backup.sh`, `scripts/dev-restore.sh` (restores into new databases and never over an existing one) and a restore drill, `scripts/restore-drill.sh`, that proves on a throwaway stack that the copy has the same rows, migration history, hypertable, grants, row-level security, functions and MongoDB documents, that a repeated or live-name restore is refused and that the migrator applies nothing to the copy (first run `ALL OK`); the runbook `restore` is written for the compose stack (draft). The deployed host, the schedule, off-host encryption and the recovery targets stay a Phase 5 gate item |
| L1 | `symbol_aliases` and `stocks.time_zone` have no format check, and `register_stock` lets `api_role` write them | **Fixed (ADR-0045)**: migration `AddCatalogTextChecks` (provider name, market code, symbol and time zone patterns; plain checks so a row that does not fit stops the deploy); tests with hostile values called directly as the API role, and with what real providers send (`BRK.B`, `BF-B`, `America/Argentina/Buenos_Aires`); an upgrade test shows a bad row stopping the upgrade |
| L2 | `register_stock` hides an alias conflict (a stock left with no alias) | **Fixed (ADR-0045)**: migration `RegisterStockProviderListingTaken` raises a unique violation of `pk_symbol_aliases` and the registration is rolled back; the registry throws `ProviderListingTakenException` and the directory answers `409 marketdata.stock.provider_listing_taken` (OpenAPI description updated); tests for the conflict, for the idempotent repeat of the same stock and for two parallel registrations |
| L3 | The ceiling triggers fire before `ON CONFLICT DO NOTHING`, so a re-add at the ceiling is a 23514 | **Fixed (ADR-0045)**: migration `CeilingsSkipRowsThatAlreadyExist` lets a row that exists through (after the lock) in the three triggers, keeping the pinned search path; tests at each ceiling |
| L4 | `fx_rates_rejected` is not in the retention table | **Fixed**: indefinite, a few rows per pair per day at most, purged by the operator as admin |
| I1 | `watchlist.tracked_stock_ids()` has no caller | **Documented** in the data model: ingestion refreshes every active listing; the function waits for the analysis scheduler (Phase 4). The report does not claim the worker uses it |
| I2 | The user-owned-table test keys on a column named `user_id` | **Fixed**: it also looks for `owner_id` and `account_id`; the convention is written in the data model |
| I3 | `NotFoundShareAsync` scans the market once per call | Accepted: it runs only when a listing reaches the limit of days; negligible at hundreds of listings (revisit if a market grows to many thousands) |
| I4, I5 | No upgrade test for five additive migrations; checks without `NOT VALID` on small tables | **I4 fixed**: `MigrationUpgradeTests` upgrade from the ingestion state, from the rates (values kept exactly), from the registered stocks (good rows stay, a bad row stops the upgrade) and from the Watchlist security steps. **I5 accepted** for the small catalogs; the convention for larger tables (`NOT VALID` then `VALIDATE`) is written in the data model |

Verified meanwhile because the reviewer could not run a database: the first row of a new year opens a chunk of `price_daily` and the worker can write it, the API can read it and cannot write it (`PriceDaily_ANewYearlyChunk_...`). Still unverified without a real deployment: the cost of the queries on production-size tables, a Mongo index build on a populated collection, the restore, and the pre-migration state of the Oracle host.

### code-reviewer
Pending.

### Other checks
What was done during the phase is not a substitute for the formal reviews, but it found real defects, each fixed with a test: a review of the whole 2.7 branch and a second one of the follow-ups (a provider symbol already taken is a refusal, a list of filings must have its essential columns, a ticker must be distinct, the operator command never leaves the process with an exception); the freshness sampler that read without an open connection; a new required property that would have broken the OpenAPI contract for existing clients (caught by the CI `oasdiff` job); and the remote-script scan of CI, which a `curl` in a command substitution of the load script would have failed. Mutation checks were run on the cache, the price-only read, the fee rule and the lifecycle.

## 8. Open questions and Phase 3 plan
Open questions for the owner:
- Run `code-reviewer`, which is pending.
- Put `SONAR_TOKEN` in the Dependabot secret store, and exclude `**/Migrations/**` from Sonar's duplication detection (`sonar.cpd.exclusions`).
- Which markets after the US, and when (A-027); whether the SEC adapter is turned on before Phase 4 (it needs the identity of the owner as the contact).
- Whether the sparklines need their own cache before the Phase 3 screens, or the baseline is enough.

Proposed Phase 3 plan (start in plan mode, after the gate): product spec section 3 and the Phase 3 screens against the real API and the fake IdP: watchlist (with the sparklines and the staleness states), stock detail with the range chart (ADR-0038), the calculator (with the broker fee and the stale and delisted notices), basic settings; the BFF with its Redis session store (ADR-0007); Playwright E2E in CI. The API contract of Phase 2 (`docs/api/openapi.v1.json`) is the input: a breaking change needs a new major version.
