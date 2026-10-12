# ADR-0007: Frontend — Next.js with BFF cookie sessions

- **Status:** accepted (owner may swap to Blazor via new ADR)
- **Date:** 2026-10-12

## Decision
Next.js (App Router) + TypeScript + TanStack Query + Recharts; generated typed client from OpenAPI. OIDC code + PKCE handled by a **BFF** layer in Next.js server routes; the browser holds only an opaque, httpOnly, Secure, SameSite=Lax session cookie; tokens never reach the browser and never touch `localStorage`. i18n `es-GT` default; WCAG 2.1 AA; Playwright E2E.

## Consequences
The BFF is mandatory: Clavaris issues confidential clients only (client secret required at the token endpoint, no CORS), so the code exchange cannot happen in the browser (ADR-0009). Refresh is serialized per session to avoid triggering refresh-token reuse detection. BFF adds a hop but removes token exposure to JS. CSP strict by default.

## Session store and refresh locking (decided 2026-10-12)
- **Server-side sessions in Redis** under key prefix `v1:bff:session:{sessionId}` (high-entropy random id, rotated on login, idle timeout 8 h, absolute timeout 30 days aligned with the refresh-token lifetime). Tokens are stored **encrypted** (AES-256-GCM; key from a secret, rotatable with a key id in the payload). Redis has AOF enabled; losing sessions only forces a re-login.
- **Single-flight refresh:** a per-session Redis lock (`SET key token NX PX 15000`, released with a compare-and-delete script) serializes refresh across BFF instances; waiters re-read the session after the lock holder updates it. `invalid_grant` on refresh destroys the session and forces re-login.
- CSRF: SameSite cookies plus a double-submit token on state-changing BFF routes; strict CSP.
