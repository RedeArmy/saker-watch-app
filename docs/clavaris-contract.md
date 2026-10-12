# Clavaris contract

- **Purpose:** the integration contract between Saker Watch and Clavaris (the owner's identity provider), extracted from the Clavaris repository docs and code. Replaces the earlier question list.
- **Owner:** Eder · **Status:** draft (verified against Clavaris docs; items marked UNCONFIRMED must be checked against a running instance) · **Target phase:** 1

Sources (read-only, in the Clavaris repository; ADR numbers below are Clavaris's own): `docs/03-architecture/api-contracts/api-contract-overview.md`, `integration-design/integration-design.md`, Clavaris ADR-0002, 0006, 0008, 0010, 0013; token/claim customizers under `app/src/main/java/.../infrastructure/`. Secrets files were deliberately not read.

## Answers

| Question | Answer |
|---|---|
| Protocol | Standard OIDC/OAuth2 (Spring Authorization Server). Authorization Code + **PKCE mandatory**. No SDK; standard OIDC client libraries. |
| Tenancy / issuer | **Per-`Organization` issuer, path-based: `{clavarisBaseUrl}/o/{organizationId}`**. Discovery: `{issuer}/.well-known/openid-configuration`. Never hardcode endpoints; discover them. |
| Token signing | **RS256**, per-Organization key pair; JWKS at the `jwks_uri` from discovery (`{issuer}/oauth2/jwks`), contains only that Organization's keys. Rotation with overlap (previous key stays published until its tokens expire); emergency purge exists (zero overlap). |
| Access token format | JWT (consumers verify locally against JWKS). Claims seen in code: `sub` (Account id), `organization_id`, `acr`/`amr` (`pwd`, or social provider name), optional `workspace_id`/`workspace_role`/`workspace_permissions`, profile claims gated by the `profile` scope (`picture`, `given_name`, `family_name`, `name`, `preferred_username`). |
| Client type | **Confidential clients only** (ADR-0013): token endpoint requires `client_secret_basic` (also accepts `client_secret_post`). No CORS anywhere. **The token exchange must run server-side** (our BFF). |
| Consent | Per-client `requireConsent` (default true); a trusted first-party client can disable it. |
| Refresh tokens | Rotating, with **reuse detection**: reusing an old refresh token returns `invalid_grant` and signals compromise → force re-login, never silently retry. Refresh TTL 30 days. |
| Logout | RP-initiated logout: `GET {issuer}/connect/logout`; `post_logout_redirect_uris` are registered per client. Token revocation: `POST {issuer}/oauth2/revoke`. |
| UserInfo | `GET {issuer}/userinfo` (scope-gated claims). |
| MFA | TOTP not built yet (backlog); passkeys shipped; step-up MFA planned v1.1. `acr`/`amr` claims exist. |
| Service-to-service | Management API uses `client_credentials` with a **PlatformClient** token from the platform issuer (`{base}/oauth2/token`, no `/o/...`), operator-only. Saker Watch's Worker needs no Clavaris tokens except in the Phase 6 account-deletion saga (a dedicated PlatformClient; ADR-0009). |
| Account deletion | Management API `POST /api/v1/admin/accounts/{id}:delete` (platform scope). The consumer owns its grace-period policy. Saker Watch will call it (decided). |
| Events | Webhooks (at-least-once), header `Clavaris-Signature` (HMAC-SHA256), dedupe on `event.id`. Catalog includes `account.created`, `account.session_revoked`, `organization.deleted`, `workspace_membership.added`/`removed`/`role_changed`. |
| Management API versioning | URI `/api/v1/admin/...`; OIDC surface is protocol-defined and unversioned. OpenAPI 3.1 via springdoc, Swagger UI only in dev. |
| Error shape | OIDC: RFC 6749 `error`/`error_description`. Management API: problem-detail style JSON. |

## Provisioning checklist for Saker Watch (operator steps in Clavaris)
1. Create an `Organization` for Saker Watch (dev, then production) and note its id → issuer URL.
2. Register an `OAuthClient` under it: confidential, `authorization_code` + `refresh_token`, scopes `openid profile email` (email scope UNCONFIRMED), exact BFF redirect URIs and post-logout redirect URIs, `requireConsent=false` (first-party).
3. Store `client_id`/`client_secret` as secrets for the BFF only (never the API, never the browser).

## Design impact on Saker Watch
- **BFF is mandatory, not optional** (confidential client): the Next.js server layer performs the code exchange, holds the secret and stores tokens server-side behind an httpOnly session cookie (ADR-0007).
- **Refresh must be single-flight per session** (one refresh at a time, serialized, with the result shared), otherwise parallel refreshes trip reuse detection and log the user out.
- **API token validation:** discovery-driven (`Authority = Clavaris:Authority`), RS256 only, validate `iss` equals the configured Organization issuer, signature via cached JWKS (refresh on unknown `kid`, rate-limited), `exp`/`nbf` with ≤ 60 s skew. Audience check configured but UNCONFIRMED (see below).
- **Identity key:** `(external_issuer = Organization issuer URL, external_subject = sub)`; `sub` is a stable Account id. `email` is not a key.
- **Account deletion (Phase 6) — DECIDED:** Saker Watch deletes its own data and then deletes the Clavaris identity (no dead identities). Done by a Worker-only saga with a dedicated least-privilege PlatformClient credential; see ADR-0009. Configuration is deferred to Phase 6.
- **Webhooks (optional, Phase 6):** a signed-webhook receiver could react to `account.session_revoked`/deletion; requires HMAC verification and `event.id` dedupe through the inbox table (ADR-0017).
- **Testing:** CI uses the fake IdP (same contract: per-org issuer path, RS256, discovery); local E2E can run against a real Clavaris instance. Contract tests use recorded discovery/JWKS fixtures from Clavaris.

## Still UNCONFIRMED (verify against a running Clavaris instance)
- [ ] Access-token `aud` value(s) and whether a resource indicator can be requested; access-token TTL (Spring Authorization Server default is 5 minutes unless overridden).
- [ ] Exact claims on the ID token/userinfo for scopes `email` (`email`, `email_verified`) and `locale`.
- [ ] `clavarisBaseUrl` and `organizationId` per environment; whether production uses a custom domain (ADR-0009).
- [ ] Rate limits applying to our BFF's token/refresh calls (per-client refresh limit exists in config).
- [ ] Whether `post_logout_redirect_uri` requires `id_token_hint`.
- [ ] Exact PlatformClient scope that permits `POST /api/v1/admin/accounts/{id}:delete`, whether `:delete` is idempotent (404/204 on repeat), and how the platform issuer token is requested (`client_credentials` at `{base}/oauth2/token`).
