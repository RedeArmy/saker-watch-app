# Phase 1 report — Platform core

- **Purpose:** Phase Report for the Phase 1 gate (`/phase-report`): what was built and not, evidence, risks, review findings with their disposition, and the proposed Phase 2 plan.
- **Owner:** Eder · **Status:** approved 2026-10-16 (gate passed) · **Target phase:** 1; the "Current phase" pointer moved to 2
- **Range:** `e59898c` (end of Phase 0) to the head of `feat/phase-1-6-closing`; milestones 1.0–1.5 are merged to `main`, 1.6 is on its branch.

## 1. Built
Conventional Commit subjects since Phase 0 (excluding merges): 21 feat, 9 fix, 6 test, 5 chore, 3 docs, 2 ci. 238 files, about 18.9k lines in `src/` and `tests/`.

| Milestone | Content |
|---|---|
| 1.0 | Tooling decisions (ADR-0020), Testcontainers integration project, pinned and verified package versions |
| 1.1 | Kernel (results, errors, `TimeProvider`), request pipeline (validation, unit of work, idempotency, telemetry), ProblemDetails, health, rate limiting, forwarded headers, outbound HTTP resilience with host allow-lists, Serilog/OpenTelemetry with redaction |
| 1.2 | PostgreSQL + Timescale schemas, EF Core, `Host.Migrator` (ADR-0021), two database roles with row-level security, tamper-evident audit log, MongoDB migration runner, Redis wiring |
| 1.3 | Unit of work, Postgres-only idempotency, outbox/inbox, job queue with leases and fencing, dispatcher, dead-lettering (ADR-0022) |
| 1.4 | Identity: `IAuthProvider`, Clavaris adapter (RS256, JWKS cache, audience, scope, ID-token and `act` refusal), fake IdP, JIT provisioning, authorization policies, ownership checks, `IIdentityProviderAdmin` port with a fake adapter; verified live against the owner's Clavaris |
| 1.5 | Versioned API (`/v1/me`, `PUT /v1/me/preferences` with `Idempotency-Key`), committed OpenAPI contract with a CI breaking-change gate, HTTP hardening (security headers, CORS, HSTS, body limit), minimal Retention slice with dry run (ADR-0023) |
| 1.6 | Coverage gate (Domain/Application ≥ 85 %) and CycloneDX SBOMs in CI, unit tests for Identity.Application, ADR-0024, completed agents and skills, review fixes, threat model and docs brought up to date, this report |

Scaffolding completed in this milestone (local files, not versioned because `.claude/` is excluded in `.git/info/exclude`): agents `migration-reviewer`, `test-writer`, `docs-curator`; skills `new-module`, `add-migration`; the rule `data-and-migrations.md` now names the migrator and forward-only `Down`.

## 2. Deliberately not built
- Project MCP servers (ADR-0024, `proposed`: needs the owner's decision).
- Multi-arch images, the deploy job, digest pinning of compose images, production Caddy with TLS (Phase 5).
- Account deletion and the Clavaris identity deletion flow (Phase 6); key rotation against the real Clavaris (needs a platform token with `platform:signing-keys:rotate`).
- Email on the API side (A-022): the BFF reads `/userinfo` in Phase 3.
- Any market data, watchlist, calculator, analysis or frontend code.

## 3. Test results (head of `feat/phase-1-6-closing`)
| Check | Result |
|---|---|
| `dotnet build -warnaserror` | 0 warnings, 0 errors |
| `dotnet format --verify-no-changes` | clean |
| `bash tests/hooks/run.sh` | 60/60 |
| `bash scripts/check-docs-index.sh`, `check-todos.sh` | OK |
| `dotnet test` | 604 passed, 0 failed: Application 112, Architecture 12, Contract 13, Observability 41, Identity 76, Web 84, Integration 266 (real PostgreSQL/Timescale, MongoDB, Redis through Testcontainers) |
| Coverage gate (`scripts/check-coverage.py`, merged line coverage) | passes: BuildingBlocks.Application 99.5 %, Kernel 95.7 %, Identity.Application 100 %, Retention.Application 98.5 %. The Domain assemblies have no executable lines yet |
| `dotnet list package --vulnerable --include-transitive` | no vulnerable packages (security-auditor) |
| gitleaks | clean on the branch |

DoD extras of the roadmap: authorization matrix and JWT negative tests green (`AuthorizationMatrixTests`, `ClavarisAuthProviderTests`); RLS bypass tests green (`PlatformSecurityTests`, `IdentitySecurityTests`); idempotency concurrency test green (`IdempotencyIntegrationTests`); migrations verified from empty and from the previous version (see the gaps in section 7).

Could not run here: the CI-only jobs (`api-contract`, SonarCloud Quality Gate, CodeQL, Trivy, SBOM upload) because there is no GitHub access from this environment; the Quality Gate has never been inspected. MongoDB tests run on mongo:4.4 locally and 9.0 in CI. Host.Api against `make up` was not run (the compose environment file is not readable by the assistant).

## 4. Known risks
- Availability of Clavaris: the API serves the last known signing keys for up to 24 h during an outage (T1/T33), then refuses tokens.
- `aud` of access tokens equals the client id (A-021); a resource indicator would make it stable.
- Account state is cached 5 minutes per instance (T32).
- A compromised API database login can use `provision_user`, `append_audit` and table-wide `UPDATE` on `identity.users` (T31).
- The audit chain is tamper-evident only; there is no off-database anchor yet (T3).
- Forwarded headers need the Caddy network configured at deployment (A-023); a wrong value makes every client share one IP.
- Handlers must not wait on external I/O inside the unit of work longer than the 2-minute idle-in-transaction timeout (ADR-0022).

## 5. ADRs added or changed
Added: 0020 tooling and dispatcher, 0021 persistence and migrator, 0022 reliability, 0023 API surface/HTTP hardening/retention defaults, 0024 MCP servers deferred. Changed: 0014 (OpenAPI), 0017 (status line: takeover applies only to cross-store operations). ADR-0023 and ADR-0024 are `proposed` and wait for the owner.

## 6. DOCS-INDEX status
49 approved, 16 draft, 4 stub. The stubs are the runbooks `provider-outage`, `llm-outage`, `restore` and `key-rotation`, all with target phase 5, so none is past its target. `queue-backlog` moved from stub to draft. New: ADR-0024 and this report.

## 7. Review findings and disposition
Three read-only reviews ran on the whole Phase 1 diff: `code-reviewer`, `security-auditor`, `migration-reviewer`. No blockers and no critical findings. Verdicts: approve with changes / pass with conditions / approve with conditions.

**Fixed in 1.6c/1.6d**
| Finding | Disposition |
|---|---|
| Signing-key cache: provider outage locks every user out and serializes requests (code major, security M2) | Fixed: stale keys served up to `MaximumKeyAge`, throttled retries, event 9603, test |
| Rate limiter ran after authorization, so 401s were never limited (security M1; contradicted T5/T27) | Fixed: pre-authentication per-IP limiter, tests at unit and real-host level; docs and threat model corrected |
| Retention job payload could switch off the configured dry run (security L1) | Fixed with a mutation-verified test |
| Outbox failure record not fenced by the attempt count (code 5) | Fixed (`attempts = @expected`) |
| Readiness checks hid the exception (code 9); HTTP handler never recycled connections (code 11) | Fixed |
| Sequential units of work on one context (code 1, unverified) | Not a bug: test added and passes |
| No guard against model drift (migration 9) | Test added for both contexts |
| Doc drift: metric labels, `in_progress` alert, ADR-0017 takeover text, queue-backlog stub, worker comment, enum summary, pipeline description, retention batch claim (code 4, 6, 7, 13, 14) | Fixed |
| Job handlers vs idle-in-transaction timeout (code 3) | Rule recorded in ADR-0022 |
| Threat model not updated for Phase 1 (security) | Updated (T1–T6, T9, T15, T19, T20, T27, T28; new T29–T33) |

**Decided by the owner and closed after the review (Phase 1.7 hardening)**
1. `Down` of `CreatePlatformTables` and `CreateUsers` now throws; `MigrationsAreForwardOnlyTests` keeps every `Down` that way (ADR-0025).
2. `audit_log` is kept for ever until it measurably matters; revisit with the off-database anchor in Phase 5 (ADR-0025, accepted).
3. ADR-0023 and ADR-0024 (`.mcp.json` stays empty) were accepted by the owner together with the gate on 2026-10-16.

**Closed in 1.7 (risk, security, performance, debt and future-debt series A–F)**: jobs run as the user they work for (`user_id`, checked by `enqueue_job`); column-level `UPDATE` on `identity.users`; `pg_temp` last in every definer function, default privileges that do not expose new functions to `PUBLIC`, no `TEMPORARY` for `PUBLIC`; Mongo validator `collMod` on existing collections; upgrade tests for `ProvisionUserWithStatus` and `RevokePublicFromAuditTrigger`; locale claim normalized to a supported locale; log strings and failure text scrubbed of credentials and emails; the migrator honors SIGTERM; pepper decoding no longer rejects long secrets; module-boundary and unwired-handler architecture tests; readiness caching and per-check timeouts.

**Remaining backlog**: restrict the job types `enqueue_job` accepts (the user rule is in); off-database audit anchor (P5); Mongo checksum; `CREATE SCHEMA IF NOT EXISTS platform` in the foundation and tolerant role creation (would edit an applied migration, so only if a new environment needs it); one transaction per retention run instead of per batch (acceptable for the small platform tables, T28); outbox dead-message gauge (the outcome counter already carries `dead`); `/health/ready` only on the internal network (P5); `AllowedHosts`, NU1900 as error and a dependency review step (P5); `packages.lock.json` with `--locked-mode`, worker health probe, Dockerfiles and image digests (P5).

## 8. Open questions and Phase 2 plan
Open (owner): exact markets/exchanges and currencies beyond USD→GTQ; market data provider and its licensing (Phase 2 ADR); LLM hardware (Phase 4); production base URL of Clavaris.

Proposed Phase 2 plan (start in plan mode): stock catalog and provider port with fakes and contract tests; ingestion jobs (daily, backfill) on the Phase 1 queue; `price_daily` hypertable and FX with date/source; performance math on fixtures (splits, short histories); watchlist and calculator APIs; caching; provider-licensing ADR; k6 baseline. First step: the Phase 2 plan, which now also includes portfolios (ADR-0026, proposed); the `audit_log` retention decision and the cheap migration hardening are already done.
