# Assumptions

- **Purpose:** every fact not given by the owner, coded against an interface until confirmed.
- **Owner:** Eder · **Status:** draft · **Target phase:** 0 (living)

| ID | Assumption | Default if unanswered | Needs answer by |
|---|---|---|---|
| A-001 | Name "Saker Watch" has no trademark clearance (ADR-0001). Task: USPTO classes 9/36/42, WIPO, GitHub/NuGet/npm, domains, attorney. | Private use only | Before any public/commercial release |
| A-002 | Clavaris is standard OIDC/JWT (JWKS). Nothing else assumed. | Fake IdP only | Phase 1 gate |
| A-003 | Market scope: US stocks + ETFs; "other exchanges" requested but unspecified. Stock catalog keeps `exchange` and `currency`. FX beyond USD→GTQ deferred. | NYSE/NASDAQ/ETFs in USD | Phase 2 start |
| A-004 | LLM runtime Ollama; GPU hardware/model unknown. | Behind `ILlmAnalyzer` + FakeLlm | Phase 4 start |
| A-005 | Frontend Next.js (ADR-0007). | Next.js | — (answered) |
| A-006 | Test assertions: AwesomeAssertions (ADR-0011). | — | — (answered) |
| A-007 | Provider free tiers may forbid redistribution; needed before multi-user rollout. | Single-user only | Phase 2 ADR / Phase 6 |
| A-008 | Rounding: banker's for GTQ display, ToZero for share fractions (ADR-0006). | As stated | Phase 2 start |
| A-009 | Oracle Always Free quotas to be verified at deploy time. | Verify in Phase 5 | Phase 5 |
| A-010 | GitHub handle for CODEOWNERS unknown (placeholder `@eder`). | Placeholder | Phase 0 gate |
| A-011 | RESOLVED 2026-10-12: SDK 10.0.112 installed; `dotnet restore/build -warnaserror/format/test` verified green (4 architecture tests). Fixes needed: unique csproj names (slnx), Microsoft.Extensions.Hosting reference for the Worker, CA1707 relaxed for tests. | — | — |
| A-012 | Caddy `handle_path /idp/*` strips the prefix, so the fake IdP issuer URL differs from the public path; revisit when Identity is built. | Re-evaluate in Phase 1 | Phase 1 |
| A-013 | Dev VM exposes no AVX (VirtualBox), so `mongo:7.0` crashes (exit 132). Options: enable AVX passthrough / run on bare metal, use `mongo:4.4` locally, or point dev at a Mongo elsewhere. Oracle Ampere (arm64) is unaffected. | Tests run Mongo via Testcontainers on CI | Phase 1 |
| A-014 | Postgres/Mongo bootstrap users are admins; app roles (`api_role`, `worker_role`, per-db Mongo user) are created in Phase 1 so RLS tests run as non-superuser. | Phase 1 init scripts | Phase 1 |
| A-015 | GitHub Actions are pinned by tag, not commit SHA (SHAs not verifiable offline). Pin SHAs and enable branch protection (required checks, code-owner review, no force push) when the GitHub repo exists. | Dependabot bumps | Phase 0 gate |
| A-016 | Local dev guardrails (hooks) are defence in depth, not a sandbox. Real boundary = permission rules + OS sandbox + server-side branch protection. | — | Phase 0 gate |
