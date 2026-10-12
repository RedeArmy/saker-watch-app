# Roadmap and phase gates

- **Purpose:** the phases, their exit criteria and the working agreements.
- **Owner:** Eder · **Status:** approved · **Target phase:** 0 (updated at every gate)

Work proceeds in phases. Each phase ends at a **hard gate**: stop, report (Phase Report), and wait for the owner's approval. The current phase is in `docs/PHASE` (now 1: platform core, pending its plan).

## Working agreements
1. Plan first: list tasks, files, risks and open questions at the start of each phase.
2. Small reviewable increments, trunk-based, Conventional Commits, one logical change per commit.
3. Every significant decision gets an ADR; decided items change only through a new ADR.
4. Never invent facts: unknowns go to `docs/assumptions.md` and code is written against interfaces.
5. No secrets in the repository (gitleaks in pre-commit and CI).
6. Tests are part of the work.
7. **Definition of Done:** warnings-as-errors build; `dotnet format --verify-no-changes` clean; unit, integration and architecture tests green; no new analyzer or vulnerability findings; docs, ADRs and runbooks updated and `DOCS-INDEX.md` statuses advanced; migrations tested up on an empty DB and on the previous schema; telemetry for every new code path.
8. Phase Report: what was built and what was not, test results, known risks, ADRs added, open questions, next-phase plan, review findings with their disposition, stubs past their target phase.

## Phases
| Phase | Scope | Extra DoD |
|---|---|---|
| 0 Design and scaffolding | Docs, ADRs, threat model, repo skeleton, CI, dev compose, standards | Docs index and guards green. **Done; gate approved 2026-10-12** |
| 1 Platform core | Building blocks (results, `TimeProvider`, outbox/inbox, idempotency middleware, job queue with leases, resilience, logging/OTel, health, ProblemDetails, rate limiting); Postgres schemas and migrations, Mongo migration runner, Redis; Identity (`IAuthProvider`, Clavaris adapter, fake IdP, JIT provisioning, policies); RLS with two roles; audit log; minimal Retention slice (purge of idempotency keys, outbox/inbox and finished job rows); `IIdentityProviderAdmin` port with a fake adapter; committed OpenAPI contract gate in CI; package version verification (ADR-0014, ADR-0016) | Authz matrix and JWT negative tests; RLS bypass test; idempotency concurrency test; migrations from empty and previous version |
| 2 Market data, watchlist, calculator (backend) | Stock catalog, providers with fakes and contract tests, ingestion (daily and backfill), `price_daily`, FX, performance math, watchlist and calculator APIs, caching | Performance math on fixtures incl. splits and short histories; stale-data behavior; provider-licensing ADR; k6 baseline |
| 3 Frontend | Product spec section 3, Phase 3 screens (watchlist, stock detail chart, calculator, basic settings) against the real API and the fake IdP; BFF session store; E2E in CI | E2E green |
| 4 Analysis agent | Prompt storage and versioning, JSON schemas, context builder, LLM adapter and FakeLlm, queue-driven pipeline, idempotent two-store writes with janitor, scoring step and signals engine, evaluation harness, analysis/signal UI | Malformed-output tests; prompt-injection corpus; run-twice-same-result; LLM-down degradation |
| 5 Retention, hardening, operations | Full retention policies (data-model section 5), TTL checks, backup/restore with an executed drill, dashboards and alerts, runbooks, ASVS L2 review, load and soak tests, Oracle deployment with smoke tests and rollback | Restore drill documented and executed |
| 6 Multi-user readiness | Quotas for on-demand analysis, onboarding, data export, account deletion (30-day hard delete plus Clavaris identity deletion), legal pages, admin tooling, provider-licensing compliance, capacity review | Compliance check passed |
