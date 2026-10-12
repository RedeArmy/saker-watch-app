# Clavaris contract — questions for the owner

- **Purpose:** answers needed before Identity is finalized (do not guess).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 gate

Until answered, `ClavarisAuthProvider` stays behind the fake IdP.

- [ ] Token format: JWT access tokens? opaque + introspection? ID token too?
- [ ] Signature: JWKS URL (rotation policy) or shared key? Allowed algorithms (RS256/ES256/EdDSA)?
- [ ] Issuer (`iss`) and audience (`aud`) values per environment.
- [ ] Claim names: subject, email, display name, locale, roles/scopes, tenant.
- [ ] Is `sub` stable and never reused? Can email change?
- [ ] Token lifetime; refresh token model; rotation; revocation/introspection endpoint.
- [ ] Login flow: OIDC authorization code + PKCE supported? Redirect URI rules, discovery document?
- [ ] Logout: RP-initiated, back-channel/front-channel logout?
- [ ] MFA: enforced by Clavaris? Reflected in `amr`/`acr` claims?
- [ ] Service-to-service tokens (client credentials) for the Worker?
- [ ] Rate limits, availability SLA, test/sandbox environment?
- [ ] User deletion/export events (for account deletion in Phase 6)?
