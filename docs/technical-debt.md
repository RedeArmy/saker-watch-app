# Technical debt register

- **Purpose:** every known shortcut, gap and deferred decision of the codebase, with what it costs, how big it is and what makes it worth paying, so that the debt is chosen and not discovered. Present debt is in the code or the deployment today; future debt is what the plan will create or what growth will expose.
- **Owner:** Eder · **Status:** draft · **Target phase:** 2 (reviewed at every phase gate)
- **Source:** the full-repository review and the Phase 2 gate reviews (`docs/reports/phase-2-report.md`, section 7), a scan of `src/**` for concurrency and allocation patterns, and measurements taken on 2026-10-23 (`docs/reports/phase-2-load-baseline.md`, the job-claim experiment below).

**Scale.** Effort: S under half a day, M one to three days, L one to two weeks, XL more than two weeks. Priority: **P0** blocks a production deployment, **P1** must be paid before the next scale step (a second user, 1,000 stocks, a second replica), **P2** is paid when the code is touched. "Phase" is the phase that owns it in `docs/roadmap.md`.

## 1. Resolved by the review of 2026-10-23
| ID | What it was | Evidence |
|---|---|---|
| TD-000 | The job claim selected from both states at once, so its cost grew with the backlog (12.5 ms and 484 buffers with 30,000 pending jobs, on every claim of every worker loop) | `EXPLAIN (ANALYZE, BUFFERS)` on a copy of `platform.job_queue` with 30,000 pending, 200 leased and 100,000 finished rows: the new statement is an index probe on the leased index followed by an index scan on the pending index that stops at the first row, 22 buffers and 0.06 ms for the pending lookup. Commit `473dc6a`; `AnAbandonedLease_IsReclaimedBeforeTheBacklogOfPendingJobs_AndThePendingOnesGoOldestFirst` pins the order |
| TD-008 | The free Twelve Data plan cannot carry the spec: a refresh costs two credits per stock, so 500 stocks need 1,000 credits a day against 800, and nothing stopped the catalog growing past what the plan refreshes | The shared catalog is capped at 250 active listings (`MarketData:Lookup:MaxCatalogListings`); a new stock past it is `409 marketdata.stock.catalog_full`; the spec is corrected (ADR-0051, accepted; the host refuses to start with a cap its credits cannot refresh). Not solved: the 1,000-stock analysis and a second market need a paid plan, which stays a decision |
| TD-026 | Connection pools were not sized: Npgsql's default of 100 per process against PostgreSQL's `max_connections` of 100, shared by the API, the worker and the migrator | `ConnectionStrings:PostgresMaxPoolSize` (default 20) is applied to the runtime string unless it names a size itself; an unreadable string stops the host without echoing it; the `Npgsql` meter is exported; sizing formula in `docs/runbooks/scaling-the-api.md`. `PostgresPoolingTests` |
| TD-027 | `IngestionOptions` was bound without validation: its ranges were never enforced and its timespans had none (zero looped on the database, a negative value stopped the host) | `ValidateDataAnnotations` and `ValidateOnStart`, ranges on `SettleDelay`, `CheckInterval` and `FreshnessInterval`; `MarketDataRegistrationTests` |
| TD-028 | `SensitiveText` did not mask `api-key` or `x-api-key` | The names are `api[-_]?key`; tests for the header and the query form. The `name: value` form with spaces is still one word (T35) |
| TD-029 | The registration slot of the day was not given back when the database failed or the request was cancelled | `StockDirectory` releases it before the failure goes on; a test with a failing registry |
| TD-032 | The worker's operator commands ignored Ctrl-C, and a `--provider-symbol` the database would refuse ended in exit 3 with a trace | Ctrl-C cancels the command (exit 130); the symbol is checked against the form of the database check (exit 2) |

## 2. Present debt that blocks production (P0)
| ID | Debt | Impact | Effort | Phase |
|---|---|---|---|---|
| TD-001 | **No Dockerfile exists** for `Host.Api`, `Host.Worker` or `Host.Migrator`, and CI builds no image | Nothing can be deployed; image scanning and signing have nothing to scan | M | 5 |
| TD-002 | The production compose has the datastores, Caddy and the fake IdP but **no application service**; the Caddyfile answers a fixed string (`TODO` phase 5); datastore images are not pinned by digest | The deployed shape is untested; supply-chain pinning is open | M | 5 |
| TD-003 | **Backups and the restore drill run only on the dev compose** (`scripts/dev-backup.sh`, `restore-drill.sh`); no schedule, no off-host copy, no encryption check on the deployed host. The spec asks RPO 24 h and RTO 4 h | The recovery promise is unproven where it matters | M | 5 |
| TD-004 | **No alert rules** and only the dashboards of the dev observability profile. The signals exist (`rate_limiter_fallback_total`, freshness gauges, queue depth and oldest age, credit lanes, `reload_queued`) and nothing pages anyone | An outage or a stale market is found by the user | M | 5 |
| TD-005 | **A-023: the proxy network is not configured** (`ForwardedHeaders:KnownProxies`). Behind Caddy every client shares one address, so the pre-authentication limiter can lock out all users with 600 bad tokens a minute | Self-inflicted denial of service from one anonymous source | S | 5 |
| TD-006 | The **frontend and the BFF do not exist** (`web/` is absent); the API contract has never been used by a client | The first client will find the contract mistakes that tests cannot | XL | 3 |
| TD-007 | **Clavaris is verified against a fake IdP and one live check of the `aud` claim (A-021)**, not end to end with the real provider, including the account lifecycle (suspension, deletion) | The only login of the product is unproven | M | 3 |
| TD-009 | Runbooks `provider-outage`, `llm-outage` and `key-rotation` are **stubs**; there is no procedure to rotate the Twelve Data key, the Clavaris client secret or the database logins | An incident is handled from memory | M | 5 |

## 3. Present debt in the product scope
| ID | Debt | Impact | Effort | Phase |
|---|---|---|---|---|
| TD-010 | `Analysis` and `Signals` are empty (16 lines each): no prompt store, context builder, LLM adapter, scoring, signals engine or evaluation harness. **A-004 (LLM hardware and model) is undecided** | The differentiator of the product is not started and its feasibility (nightly runs on small hardware) is unknown | XL | 4 |
| TD-011 | One market data provider and **US calendars only**; a market without a calendar gets no refresh and a window less. The SEC adapter exists but is off (A-028) | Single point of failure for prices; no second market | L per market | after 3 |
| TD-012 | Retention covers only the platform tables (idempotency keys, processed outbox and inbox, finished and dead jobs: `RetentionTarget`). Not enforced yet, though `docs/data-model.md` section 5 sets the policy: the TTL of `provider_payloads` (30 to 90 days) and `job_events` in Mongo, the 24-month partition drop of `audit_log`, and the expiry of analysis documents | The raw provider answers, which are kept as received on every call, grow without bound | M | 5 |
| TD-013 | Multi-user features absent: quotas for analysis, onboarding, data export, account deletion (30-day hard delete plus Clavaris deletion), admin tooling, licence compliance for redistribution | Cannot be opened to a second person | XL | 6 |
| TD-014 | Weekly and monthly sparkline points are **computed on read**; `price_daily` has no compression or retention policy and there are no continuous aggregates | Fine at 500 stocks, grows linearly with the catalog | M | after 5 |
| TD-015 | **The Oracle Always Free VM is 2 OCPUs and 12 GB**, not the 4 and 24 the plan was written for (Oracle's documentation, 2026-10-24). PostgreSQL with Timescale, MongoDB, Redis, the API, the worker, Caddy, the observability stack and any model have to share it | The deployment shape, the cost line of the spec and the place of the LLM all depend on it; a stack that fits in 24 GB may not fit in 12 | decision, then M | RFC-0002, 5 |

## 4. Present debt in the code
| ID | Debt | Impact | Effort | Pri |
|---|---|---|---|---|
| TD-020 | Types over the 400-line guideline: `EdgarProvider` 526, `TwelveDataProvider` 471, `JobProcessor` 457, `PortfolioRepository` 412 | Harder review; each mixes parsing, I/O and mapping or several SQL statements | M each | P2 |
| TD-021 | Seven `[SuppressMessage("S138")]` marks on declarative registrations (endpoint maps, `Add…` extensions, `OnModelCreating`) | Accepted by ADR-0049; the list may only shrink | S | P2 |
| TD-022 | **Two data-access styles**: EF Core for the model and migrations, hand-written Npgsql SQL in the repositories and readers (one `AsNoTracking` in the code base). SQL strings are checked only by integration tests, not by the compiler | A column rename compiles and fails at run time; the schema is described twice | L | P1 |
| TD-023 | The watchlist **loads the catalog entry of every stock of the user to sort by ticker** (`FindManyAsync` of up to 500) on every list request, because ordering is done in memory across the module boundary | O(n) per request, flat at 500, a problem above a few thousand | M | P1 |
| TD-024 | `UserResolver` has **no single-flight on a cache miss**: concurrent first requests of one identity all provision (idempotent, but duplicated writes); a full cache drops the new identity instead of evicting the oldest | A burst of new users costs writes; no correctness risk | S | P2 |
| TD-025 | Every request opens a transaction with `set_config` at session level and relies on the pool reset to clear it, which costs round trips (`set_config`, `BEGIN`, `COMMIT`) and a reset per connection. A transaction-local `set_config(..., true)` removes the leak by construction | Latency and a subtle isolation dependency on the driver | M | P1 |
| TD-030 | `RedisProviderCallBudget.ReleaseAsync` decrements the **current** minute and day, not those of the reservation; across a boundary a credit too many can pass | The provider answers 429 earlier than the budget expected | S | P2 |
| TD-031 | The EDGAR client retries (`MaxRetries = 2`) **inside** the pace gate, so a retry can exceed 10 requests a second | Off by default; matters when the adapter is turned on | S | P2 |
| TD-033 | An **expired idempotency key replays** (or mismatches) until the purge job runs; `expires_at` is not read by the claim | A key reused after 48 h can return a stale answer | S | P2 |
| TD-034 | `ScrubbingSink` replaces the exception with a scrubbed copy; the OTLP exporter may then group every exception under `ScrubbedException` (**unverified**) | Alerts by exception type would collapse | S | P1 |
| TD-035 | `ConditionalResults` honours `If-None-Match` only for `GET`; `HEAD` never gets a `304` | Negligible | S | P2 |
| TD-036 | The streak of unknown-symbol days counts days **attempted**, not consecutive sessions; a day that never reached the listing does not reset it | ADR-0039 says "in a row"; the code is looser | S | P2 |
| TD-037 | The Redis Lua scripts of the limiter build key names inside the script instead of declaring them in `KEYS`, so they would not run on Redis Cluster | Fine on one Redis (ADR-0021); a trap if that ever changes | S | P2 |
| TD-038 | The limiter falls back to the process **with the whole limit**, so N replicas allow N times the limit while Redis is down (the registration cap divides by `IApiInstances`, the limiters do not) | Weaker protection exactly during the outage | S | P1 |
| TD-039 | A listing marked `failed` for a reason other than the configuration needs a manual SQL reset (runbook `listing-lifecycle`); there is no command | Operator toil, rare | S | P2 |
| TD-040 | `RewriteTolerance` (1e-6) was validated within minutes for prices from $1.29 to $759 (11 symbols, no close moved, 3 of them under $5: runbook `provider-quota`, 2026-10-24). **Not validated across days**: the next run of `scripts/run-live-rewrite-stability.sh` on another day compares with the snapshot this one left | A provider that recomputes overnight could still read as a rewrite and queue reloads | S | P1 |
| TD-041 | History reloads share the on-demand credit lane (ADR-0043); a mass rewrite of a market takes three days to clear, and a reloading stock is stale meanwhile | Bounded by design; reopen conditions are in the runbook `provider-quota` | M | P2 |
| TD-042 | The sparklines endpoint is **unpaged**: 500 lines for a page of 50 (ADR-0050). p95 of the screen is 234 to 243 ms against a 300 ms budget, a thin margin | The first thing to build when the budget is crossed | M | P1 |
| TD-043 | The idle workers poll every second (four loops, `PollInterval` 1 s) with no jitter and no wake-up: four claim statements a second per worker for nothing, and loops that start together stay together | Constant low load; a latency floor of one second for a new job | M | P2 |
| TD-044 | `HealthEndpoints` and the freshness sampler guard shared state with `lock`; each is held for a few instructions, but `lock` is the only primitive in use and there is no review rule for it | None today | S | P2 |

## 5. Debt in tests, tooling and process
| ID | Debt | Impact | Effort | Pri |
|---|---|---|---|---|
| TD-050 | The **integration suite takes about 12 minutes** (each API test starts its own host for isolation, ~3 s each) and runs serially for the shared containers | The longest wait of every pull request and of every local change | L | P1 |
| TD-051 | The agent tooling (`CLAUDE.md`, `AGENTS.md`, `.claude/`, `tests/hooks`) is **not in the repository** (owner's decision of 2026-10-23); its 69 tests are not run by CI and one person holds the context | Bus factor; the guardrails that protect secrets are untested in CI | M | P1 |
| TD-052 | The worker smoke (`scripts/smoke-worker.sh`) and the restore drill run by hand only | A regression in start-up or shutdown surfaces when an operator runs them | M | P1 |
| TD-053 | `RedisRateLimiterTests.AProcessThatStopsBeating_IsDroppedWhenItsLeaseEnds` uses a 1 s lease and can race on a slow runner; it also does not assert the final count | A flaky test | S | P2 |
| TD-054 | The Sonar token and the Mongo password reach an argument list in CI and in the dev backup scripts (`ps` shows them for the length of the command) | Low (CI runner, dev stack) | S | P2 |
| TD-055 | `docs/PHASE` and `docs/roadmap.md` still say "Phase 2, pending its plan"; the "Current phase" pointer lives in a file outside the repository | Documents contradict the state | S | P2 |
| TD-056 | The k6 baseline is **one machine, four virtual users**; there are no capacity, soak or spike tests, and the page scenario has not run against a real BFF | The margin is unknown on production hardware | L | 5 |
| TD-057 | Coverage is gated for ten assemblies; Infrastructure assemblies (97 to 98 % measured) and the hosts have no gate, and `Host.Migrator` is at 60 % | A regression in the least-tested host goes unseen | S | P2 |
| TD-058 | Four tests are skipped by design (live checks, the scale test) and run only by hand | The live checks are the only proof against real providers | S | P2 |

## 6. Future debt: what the plan will create
| ID | Risk | When it appears | What to do before |
|---|---|---|---|
| TD-100 | **Prompt and model drift** in the analysis: a model or prompt change moves the signals with no code change | Phase 4 | The evaluation harness and the golden set first (spec 2.3), versions stored with every analysis (ADR-0008) |
| TD-101 | **Prompt injection through provider text** (company names, filings) reaching the LLM | Phase 4 | Corpus of injection cases in the test suite; the LLM has no tools and its output is validated data (already a rule) |
| TD-102 | **LLM cost and latency at 1,000 stocks nightly** on small hardware (A-004) | Phase 4 | Measure tokens per stock and throughput on the chosen hardware before the pipeline is built |
| TD-103 | **A second market multiplies the calendar, currency and provider work**: each needs a verified calendar, a live check, a pass over `docs/api/errors.md` and the FX path | After Phase 3 (A-027) | One market at a time; the checklist in A-027 |
| TD-104 | **Licensing of free data tiers** forbids redistribution; opening the app to a second user may breach it | Phase 6 | The provider-licensing ADR (ADR-0027) is the gate; budget a paid plan or a licensed source |
| TD-105 | **Scale-out of the API** needs Redis-backed limiters (done), shared session store for the BFF, and sticky nothing; pools and `max_connections` are sized together (TD-026, the formula is in the runbook) | A second replica | `docs/runbooks/scaling-the-api.md`, TD-026 |
| TD-106 | **Schema growth**: `price_daily` yearly chunks, no compression; `provider_payloads` in Mongo without a TTL | 1,000+ stocks or a year of operation | Compression policy and retention before they are needed (TD-012, TD-014) |
| TD-107 | **Breaking API changes** after the first client exists need a new major version (ADR-0014) | Phase 3 | Do the contract changes before the screens (done for steps 5 to 8); keep `oasdiff` as a required check |
| TD-108 | **Dependency churn**: .NET 10, EF Core, Npgsql, Testcontainers, k6 and Sonar analyzers move quickly; the analyzers are errors, so an update can fail the build | Every Dependabot batch | Group updates; keep the build green on `main` before merging a bump |
| TD-109 | The **owner is the only reviewer** (A-015): every review since Phase 1 was an agent or the author | Always | The independent reviews at each gate have found real defects; keep them, and add a second human before multi-user |

## 7. Opportunities that this debt is in the way of
These are the points that set the product apart from a plain watchlist; each has a precondition from the register.

| Opportunity | Why it differentiates | Needs first |
|---|---|---|
| Every number carries its **source and `as_of`**, and stale, pending and delisted are states, not errors | Most trackers show a price and hide how old or wrong it can be | Nothing: it is built; the screens must show it (TD-006) |
| **Signals with machine-readable reasons**, computed by a versioned rules engine and never by the LLM | Explainable and testable, unlike a model that scores | TD-010, TD-100 |
| **Analyses shared per stock, prompt version and date**, not per user | One run serves every user: the cost of the LLM does not grow with users | TD-010, TD-102 |
| **The partial-purchase calculator in quetzales**, with the Banco de Guatemala reference rate, the FX path and the broker fee | A niche no global product serves | TD-006 |
| **A self-hosted LLM** | Watchlists and notes never leave the owner's machine | A-004 |
| **A golden-set evaluation harness** run before any change of model or prompt | Makes the quality of the analysis a number | TD-010 |
| **Provenance and audit** (hash-chained audit log, lineage of every analysis) | Trust and compliance | TD-012 |
