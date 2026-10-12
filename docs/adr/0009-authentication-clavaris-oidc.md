# ADR-0009: Authentication via Clavaris (OIDC/JWT adapter)

- **Status:** accepted (contract extracted from the Clavaris repository on 2026-10-12; see `docs/clavaris-contract.md`)
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
Clavaris is the owner's own identity provider: Spring Authorization Server, OIDC/OAuth2, per-Organization issuer (`{base}/o/{organizationId}`), RS256 with per-Organization JWKS, confidential clients only (no browser-side token exchange, no CORS), mandatory PKCE, rotating refresh tokens with reuse detection.

## Decision
- `IAuthProvider` lives in `Identity.Contracts`; `ClavarisAuthProvider` is a **discovery-driven OIDC adapter**: configuration is `Clavaris:Authority` (the Organization issuer URL) plus client credentials for the BFF; endpoints and `jwks_uri` come from the discovery document.
- **API (resource server):** validates JWT access tokens: RS256 only (algorithm allow-list, never `none`/HS), `iss` equals the configured issuer, signature via cached JWKS (refresh on unknown `kid`, rate-limited), `exp`/`nbf`, skew <= 60 s, audience checked once confirmed (contract UNCONFIRMED list).
- **BFF (Next.js server):** the only component holding the client secret; performs the code+PKCE exchange, stores tokens server-side behind an httpOnly session cookie, refreshes tokens **single-flight per session**, treats `invalid_grant` on refresh as forced re-login, performs RP-initiated logout and token revocation.
- **JIT provisioning:** `identity.users` keyed by `(issuer URL, sub)`; internal UUIDv7 `UserId` is the only FK. Email/name are profile data, never keys.
- **Authorization:** default-deny policies in our API; Clavaris Workspace claims are not used in v1 (single-user product); roles, if any, are our own.
- **Dev/test:** a fake IdP reproducing the same contract (per-org issuer path, discovery, RS256 JWKS) for CI; optional local E2E against a real Clavaris instance.
- **Account deletion (decided 2026-10-12, configured later in Phase 6):** deleting a Saker Watch account also deletes the identity in Clavaris, so no dead identities remain there. Accounts in Clavaris are scoped to Saker Watch's own Organization, so this affects no other application.
  - Mechanism: Worker-only saga job `account-delete:{userId}` (deterministic key, ADR-0017). Steps: user requests deletion (soft-delete, 30-day grace) -> after grace, hard-delete personal data and keep a minimal tombstone `(issuer, sub, internal id)` -> call Clavaris `POST /api/v1/admin/accounts/{sub}:delete` -> on success (2xx) or "already gone" (404) remove the tombstone and write the audit event. Failures retry with backoff and jitter, then dead-letter with an alert.
  - Credential: a dedicated Clavaris **PlatformClient** with the narrowest scope that allows account deletion, used by `Host.Worker` only (never the API, the BFF or the browser), stored as a secret, rotated per the key-rotation runbook, and every call audited. Scope name and `:delete` idempotency semantics are UNCONFIRMED (see contract doc).
  - Configuration (PlatformClient creation, secrets, Worker wiring) is deliberately deferred to Phase 6; Phase 1 only reserves the `IIdentityProviderAdmin` port in `Identity.Contracts`, with a fake adapter for tests.

## Consequences
Account deletion adds a high-privilege platform credential to the Worker (threat T21 in the threat model); it is isolated to one job type and one network egress rule. The BFF is mandatory (not a stylistic choice). Refresh handling must be serialized to avoid tripping reuse detection. Remaining unknowns (access-token audience, TTL, email claims) are tracked in `docs/clavaris-contract.md` and must be confirmed against a running Clavaris before the Phase 1 gate on Identity.
