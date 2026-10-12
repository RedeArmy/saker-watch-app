# ADR-0007: Frontend — Next.js with BFF cookie sessions

- **Status:** accepted (owner may swap to Blazor via new ADR)
- **Date:** 2026-10-12

## Decision
Next.js (App Router) + TypeScript + TanStack Query + Recharts; generated typed client from OpenAPI. OIDC code + PKCE handled by a **BFF** layer in Next.js server routes; tokens stay in httpOnly, Secure, SameSite cookies — never in `localStorage`. i18n `es-GT` default; WCAG 2.1 AA; Playwright E2E.

## Consequences
BFF adds a hop but removes token exposure to JS. CSP strict by default.
