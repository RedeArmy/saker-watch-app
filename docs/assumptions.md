# Assumptions

- **Purpose:** every fact not given by the owner, coded against an interface until confirmed.
- **Owner:** Eder · **Status:** approved · **Target phase:** 0 (living)

| ID | Assumption | Default if unanswered | Needs answer by |
|---|---|---|---|
| A-001 | Name "Saker Watch" has no trademark clearance (ADR-0001). Task: USPTO classes 9/36/42, WIPO, GitHub/NuGet/npm, domains, attorney. | Private use only | Before any public/commercial release |
| A-002 | PARTIALLY RESOLVED 2026-10-12: contract extracted from the Clavaris repo (per-org OIDC issuer, RS256/JWKS, confidential client, PKCE, rotating refresh). Unconfirmed items (audience, access-token TTL, email claims, base URL/org id per environment) are listed in `docs/clavaris-contract.md`; the deletion approach is decided (A-017). | Fake IdP with the same contract | Phase 1 gate |
| A-003 | RESOLVED 2026-10-12 (scope): US equities/ETFs first, then Europe (LSE, XETRA, Euronext), Canada (TSX), LatAm (BMV, B3) and Asia (exchanges TBD) incrementally. Design in ADR-0018. Open: which Asian exchanges; provider coverage/licensing per market. | US enabled first | Per-market enablement |
| A-004 | LLM server undecided (Ollama runtime chosen, hardware/model TBD). Behind `ILlmAnalyzer` + FakeLlm; decide before Phase 4 (CPU-only = small quantized models, concurrency 1, nightly batch). | FakeLlm | Phase 4 start |
| A-005 | Frontend Next.js (ADR-0007). | Next.js | — (answered) |
| A-006 | Test assertions: AwesomeAssertions (ADR-0011). | — | — (answered) |
| A-007 | Provider free tiers may forbid redistribution; needed before multi-user rollout. | Single-user only | Phase 2 ADR / Phase 6 |
| A-008 | RESOLVED 2026-10-12: banker's rounding for monetary display; share fractions truncated to 6 decimals (ADR-0006). | — | — |
| A-009 | Oracle Always Free quotas to be verified at deploy time. | Verify in Phase 5 | Phase 5 |
| A-010 | RESOLVED 2026-10-12: GitHub handle `RedeArmy` used in CODEOWNERS. | — | — |
| A-011 | RESOLVED 2026-10-12: SDK 10.0.112 installed; `dotnet restore/build -warnaserror/format/test` verified green (4 architecture tests). Fixes needed: unique csproj names (slnx), Microsoft.Extensions.Hosting reference for the Worker, CA1707 relaxed for tests. | — | — |
| A-012 | Caddy `handle_path /idp/*` strips the prefix, so the fake IdP issuer URL differs from the public path. Also unverified: that `mock-oauth2-server` supports a two-segment issuer path (`/o/{orgId}`); if not, CI uses a minimal fake IdP written in .NET that reproduces the Clavaris contract. | Decide in Phase 1 | Phase 1 |
| A-013 | RESOLVED 2026-10-12: the dev VM has no AVX, so `mongo:7.0` crashes (exit 132). Compose uses `deploy/docker-compose.no-avx.yml` (mongo:4.4); tests use `SAKER_TEST_MONGO_IMAGE=mongo:4.4` (ADR-0011). Dev therefore runs Mongo 4.4, so code exercised locally must avoid features newer than 4.4. The deployed arm64 host and CI use mongo:7.0. | — | — |
| A-014 | Postgres/Mongo bootstrap users are admins; app roles (`api_role`, `worker_role`, per-db Mongo user) are created in Phase 1 so RLS tests run as non-superuser. | Phase 1 init scripts | Phase 1 |
| A-015 | DECIDED 2026-10-12: solo developer, so "Require review from Code Owners" is NOT enabled. Branch protection on `main` is implemented as a GitHub Ruleset (`docs/runbooks/github-ruleset-main.json`): PR required (0 approvals), required CI checks, linear history, no force push or deletion. GitHub Actions are pinned to full commit SHAs (done 2026-10-12; Dependabot proposes bumps). Revisit when a second contributor joins. | Apply branch protection when the remote repo is configured | Repo creation |
| A-016 | Local dev guardrails (hooks) are defence in depth, not a sandbox. Real boundary = permission rules + OS sandbox + server-side branch protection. | — | — |
| A-017 | DECIDED 2026-10-12: account deletion also deletes the Clavaris identity via a Worker-only saga and a dedicated PlatformClient credential; configuration deferred to Phase 6 (Phase 1 reserves the `IIdentityProviderAdmin` port + fake). Unconfirmed: PlatformClient scope name and `:delete` idempotency. | Local-only deletion until configured | Phase 6 |
| A-018 | CodeQL uploads to GitHub code scanning, which needs a public repository or GitHub Advanced Security. On a private repo the CI analyzes locally and keeps the SARIF as an artifact (14 days). Enable code scanning (or make the repo public) to see alerts in the Security tab. | SARIF artifact only | Repo visibility decision |
| A-019 | SonarCloud free-tier limits for private repositories were not verified; check before importing the project. Fallback: analyzers in the build only (ADR-0019). | Analyzers in build | Before SonarCloud import |
