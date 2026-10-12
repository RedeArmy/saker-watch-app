# ADR-0009: Authentication via Clavaris (OIDC/JWT adapter)

- **Status:** accepted for adapter shape; Clavaris contract UNKNOWN
- **Date:** 2026-10-12

## Decision
`IAuthProvider` in `Identity.Contracts`; `ClavarisAuthProvider` assumes standard OIDC/JWT: JWKS validation, `iss/aud/exp/nbf`, algorithm allow-list (never `none`, no HS/RS confusion), JWKS cache with rate-limited refresh on unknown `kid`, skew ≤ 60 s. All specifics in `Clavaris:*` options validated at startup. Dev/test fake IdP with local JWKS. JIT provisioning keyed by `(issuer, subject)`; internal UUIDv7 `UserId` is the only FK. Default-deny authorization.

## Consequences
Nothing beyond this is assumed about Clavaris; see `docs/clavaris-contract.md`. Identity is not finalized past the Phase 1 gate without owner answers.
