# ADR-0007: Frontend — Next.js with BFF cookie sessions

- **Status:** accepted (owner may swap to Blazor via new ADR)
- **Date:** 2026-10-12

## Decision
Next.js (App Router) + TypeScript + TanStack Query + Recharts; generated typed client from OpenAPI. OIDC code + PKCE handled by a **BFF** layer in Next.js server routes; tokens stay in httpOnly, Secure, SameSite cookies — never in `localStorage`. i18n `es-GT` default; WCAG 2.1 AA; Playwright E2E.

## Consequences
The BFF is mandatory: Clavaris issues confidential clients only (client secret required at the token endpoint, no CORS), so the code exchange cannot happen in the browser (ADR-0009). Refresh is serialized per session to avoid triggering refresh-token reuse detection. BFF adds a hop but removes token exposure to JS. CSP strict by default.
