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
2. Register an `OAuthClient` under it: confidential, `authorization_code` + `refresh_token`, scopes `openid profile email` (email scope accepted; see "Verified" below for what is actually emitted), exact BFF redirect URIs and post-logout redirect URIs, `requireConsent=false` (first-party).
3. Store `client_id`/`client_secret` as secrets for the BFF only (never the API, never the browser).

## Design impact on Saker Watch
- **BFF is mandatory, not optional** (confidential client): the Next.js server layer performs the code exchange, holds the secret and stores tokens server-side behind an httpOnly session cookie (ADR-0007).
- **Refresh must be single-flight per session** (one refresh at a time, serialized, with the result shared), otherwise parallel refreshes trip reuse detection and log the user out.
- **API token validation:** discovery-driven (`Authority = Clavaris:Authority`), RS256 only, validate `iss` equals the configured Organization issuer, signature via cached JWKS (refresh on unknown `kid`, rate-limited), `exp`/`nbf` with ≤ 60 s skew. Audience check configured but UNCONFIRMED (see below).
- **Identity key:** `(external_issuer = Organization issuer URL, external_subject = sub)`; `sub` is a stable Account id. `email` is not a key.
- **Account deletion (Phase 6) — DECIDED:** Saker Watch deletes its own data and then deletes the Clavaris identity (no dead identities). Done by a Worker-only saga with a dedicated least-privilege PlatformClient credential; see ADR-0009. Configuration is deferred to Phase 6.
- **Webhooks (optional, Phase 6):** a signed-webhook receiver could react to `account.session_revoked`/deletion; requires HMAC verification and `event.id` dedupe through the inbox table (ADR-0017).
- **Testing:** CI uses the fake IdP (same contract: per-org issuer path, RS256, discovery); local E2E can run against a real Clavaris instance. Contract tests use recorded discovery/JWKS fixtures from Clavaris.

## Verified against a running Clavaris (2026-10-13, local instance)
Set up with the management API: Organization, one confidential `OAuthClient` (`authorization_code` + `refresh_token`, scopes `openid profile email`, `requireConsent=false`), one account registered through the hosted form, then a real Authorization Code + PKCE (S256) login and token exchange with `client_secret_basic`.
- Issuer `{base}/o/{organizationId}` (no trailing slash); discovery at `{issuer}/.well-known/openid-configuration`; JWKS at `{issuer}/oauth2/jwks`; RS256 only; header carries `kid`.
- **Access token `aud` is the OAuth `client_id`** (for example `client_6ac1...`), a single value, not a fixed API name. `Clavaris:Audience` must therefore be the client id of the client whose tokens the API accepts (A-021).
- **Lifetime 300 s** (`expires_in: 299`); a refresh token is issued. The BFF must refresh before expiry.
- **`scope` is a JSON array** (`["openid","profile","email"]`), not a space-delimited string. The provider accepts both.
- Access-token claims (re-verified 2026-10-14 after the Clavaris OIDC changes): `sub` (account UUID), `aud`, `iss`, `scope`, `iat`, `nbf`, `exp`, `jti`, `picture` and, with the `profile` scope, `name`, `given_name`, `family_name`. **`email`/`email_verified` are not on the access token by design** (Clavaris keeps it lean); they are on the **ID token and `/userinfo`** when the `email` scope is authorized (`email_verified` is sent as `false` rather than omitted). The ID token also carries `amr`, `acr`, `azp`, `sid`, `auth_time`, `nonce`. So the API gets the display name from the access token but never the email; the BFF reads the email from `/userinfo` (A-022).
- **ID tokens are signed with the same key and carry the same `aud` as access tokens** (verified live: an ID token was accepted as a bearer before the fix). The API therefore refuses any token without a `scope` claim, and any token with an `act` claim (operator impersonation, RFC 8693) until delegated calls can be attributed (`AuthFailureReason.NotAnAccessToken`).
- Refresh (`grant_type=refresh_token`) keeps `sid` and `auth_time` stable and does not add email to the access token.
- Discovery now advertises only `client_secret_basic` and `client_secret_post`, scopes `openid profile email offline_access`, `RS256`, PKCE `S256`.
- Sign-up needs an email verification link; a local instance without SMTP logs `account_registered_verification_email_send_failed`, so a dev account is verified by setting `email_verified_at` in the Clavaris database.
- Platform token: `client_credentials` at `{base}/oauth2/token` (issuer `{base}`, `aud` = the platform client id, TTL 300 s). Creating an Organization needs scope `platform:organizations:write` and an `ownerPlatformAccountId`. Registering an OAuth client is `POST /api/v1/admin/organizations/{id}/clients` with `redirectUris`, `allowedGrantTypes`, `allowedScopes` (`/secret-keys` mints machine clients instead).
- Opt-in checks: `CLAVARIS_LIVE_AUTHORITY`, `CLAVARIS_LIVE_AUDIENCE`, `CLAVARIS_LIVE_TOKEN_FILE` enable `ClavarisLiveTests` (provider) and `ClavarisLiveApiTests` (the real `Host.Api`: authentication, `GET /v1/me`, the idempotent `PUT /v1/me/preferences`, provisioning, audit, RLS). A token for another audience or another Organization's issuer is refused (checked live).

## Still UNCONFIRMED (verify against a running Clavaris instance)
- [ ] Whether a resource indicator (RFC 8707) can be requested to get a stable `aud` instead of the client id.
- [ ] `clavarisBaseUrl` and `organizationId` per environment; whether production uses a custom domain (ADR-0009).
- [ ] Rate limits applying to our BFF's token/refresh calls (per-client refresh limit exists in config).
- [ ] Whether `post_logout_redirect_uri` requires `id_token_hint`.
- [ ] Exact PlatformClient scope that permits `POST /api/v1/admin/accounts/{id}:delete`, whether `:delete` is idempotent (404/204 on repeat), and how the platform issuer token is requested (`client_credentials` at `{base}/oauth2/token`).

## Implementation in Saker Watch (milestone 1.4a)
Token validation lives in `Identity.Infrastructure/Clavaris` behind `IAuthProvider` (`Identity.Contracts`) and is configured only through `Clavaris:*`:
| Key | Meaning | Default |
|---|---|---|
| `Clavaris:Authority` | Organization issuer URL (`https://<host>/o/<organizationId>`); tokens must carry exactly this `iss`; its host is the only host the metadata client may call | required |
| `Clavaris:Audience` | the `aud` tokens must carry for this API | required |
| `Clavaris:ClockSkew` | tolerance for `exp`/`nbf`, at most 60 s | 60 s |
| `Clavaris:MetadataLifetime` | how long discovered keys are trusted | 1 h |
| `Clavaris:MinimumMetadataRefresh` | shortest spacing of forced key refreshes (unknown `kid`) and of retries after a failed fetch | 30 s |
| `Clavaris:MaximumKeyAge` | how long the last known keys keep being used when the provider cannot be reached; older keys are not trusted (fail closed) | 24 h |
| `Clavaris:RequireHttpsMetadata` | HTTPS for the Authority and discovery; turn off only for a local fake | true |
Behavior: RS256 only (any other `alg`, including `none` and HMAC, is refused before a key is looked up); keys come from the discovered JWKS and only RSA signing keys are used; the discovery document must name the configured Authority as its issuer; a token naming an unknown `kid` triggers one awaited, rate-limited refresh, so the first token signed with a freshly rotated key is accepted; if keys cannot be obtained the token is refused (fail closed). Refusal reasons are recorded in the metric `sakerwatch_auth_attempts_total` and never returned to the client.


## Authorization (milestone 1.4c)

- **Default-deny**: `AddSakerAuthorization` sets the fallback policy to "authenticated user", so an endpoint that declares nothing is protected. Only health endpoints are `AllowAnonymous`.
- **Policies**: `authenticated`, `active-user`, `owner` (resource ownership via `IOwnedResource`) and scope policies (`AddScopePolicy`).
- **Object-level access**: `RequireOwnerAsync` reports another user's resource as `404 resource.not_found`, never `403`, so existence is not revealed (OWASP API1). Row-level security remains the second line of defence.
- **Errors**: `401` carries `WWW-Authenticate: Bearer` (`error="invalid_token"` when a credential was refused) and `403` an inactive account or a missing scope; both are ProblemDetails with `code` `auth.unauthorized` / `auth.forbidden`.
- Pipeline order in `Host.Api`: `UseAuthentication` → `UseSakerUserResolution` → `UseAuthorization` → rate limiting.

## Identity provider admin port (milestone 1.4d)

`IIdentityProviderAdmin` (`Identity.Contracts`) is the port the Phase 6 account-deletion saga will use to delete the Clavaris identity. It is separate from `IAuthProvider` because it needs a different, far more privileged credential that only the Worker holds (A-017). `DeleteIdentityAsync` is idempotent: an identity that is already gone reports `AlreadyAbsent`; failures are classified `TransientFailure` (retry) or `PermanentFailure` (dead-letter). Phase 1 ships the port and `FakeIdentityProviderAdmin` (TestSupport); the real adapter and its configuration arrive in Phase 6.
