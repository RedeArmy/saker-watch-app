# Assumptions

- **Purpose:** every fact not given by the owner, coded against an interface until confirmed.
- **Owner:** Eder · **Status:** draft · **Target phase:** 0 (living)

| ID | Assumption | Default if unanswered | Needs answer by |
|---|---|---|---|
| A-001 | Name "Saker Watch" has no trademark clearance (ADR-0001). Task: USPTO classes 9/36/42, WIPO, GitHub/NuGet/npm, domains, attorney. | Private use only | Before any public/commercial release |
| A-002 | Clavaris is standard OIDC/JWT (JWKS). Nothing else assumed. | Fake IdP only | Phase 1 gate |
| A-003 | RESOLVED 2026-10-12 (scope): US equities/ETFs first, then Europe (LSE, XETRA, Euronext), Canada (TSX), LatAm (BMV, B3) and Asia (exchanges TBD) incrementally. Design in ADR-0018. Open: which Asian exchanges; provider coverage/licensing per market. | US enabled first | Per-market enablement |
| A-004 | LLM server undecided (Ollama runtime chosen, hardware/model TBD). Behind `ILlmAnalyzer` + FakeLlm; decide before Phase 4 (CPU-only = small quantized models, concurrency 1, nightly batch). | FakeLlm | Phase 4 start |
| A-005 | Frontend Next.js (ADR-0007). | Next.js | — (answered) |
| A-006 | Test assertions: AwesomeAssertions (ADR-0011). | — | — (answered) |
| A-007 | Provider free tiers may forbid redistribution; needed before multi-user rollout. | Single-user only | Phase 2 ADR / Phase 6 |
| A-008 | RESOLVED 2026-10-12: banker's rounding for monetary display; share fractions truncated to 6 decimals (ADR-0006). | — | — |
| A-009 | Oracle Always Free quotas to be verified at deploy time. | Verify in Phase 5 | Phase 5 |
| A-010 | GitHub handle for CODEOWNERS unknown (placeholder `@eder`). | Placeholder | Phase 0 gate |
| A-011 | RESOLVED 2026-10-12: SDK 10.0.112 installed; `dotnet restore/build -warnaserror/format/test` verified green (4 architecture tests). Fixes needed: unique csproj names (slnx), Microsoft.Extensions.Hosting reference for the Worker, CA1707 relaxed for tests. | — | — |
| A-012 | Caddy `handle_path /idp/*` strips the prefix, so the fake IdP issuer URL differs from the public path; revisit when Identity is built. | Re-evaluate in Phase 1 | Phase 1 |
| A-013 | RESOLVED 2026-10-12: dev VM has no AVX; use `deploy/docker-compose.no-avx.yml` (mongo:4.4) locally. Deployed arm64 host and CI use mongo:7.0 / Testcontainers. Keep the app compatible with Mongo 4.4+ features only in dev paths, or enable AVX passthrough in the VM. | — | — |
| A-014 | Postgres/Mongo bootstrap users are admins; app roles (`api_role`, `worker_role`, per-db Mongo user) are created in Phase 1 so RLS tests run as non-superuser. | Phase 1 init scripts | Phase 1 |
| A-015 | GitHub Actions are pinned by tag, not commit SHA (SHAs not verifiable offline). Pin SHAs and enable branch protection (required checks, code-owner review, no force push) when the GitHub repo exists. | Dependabot bumps | Phase 0 gate |
| A-016 | Local dev guardrails (hooks) are defence in depth, not a sandbox. Real boundary = permission rules + OS sandbox + server-side branch protection. | — | Phase 0 gate |
