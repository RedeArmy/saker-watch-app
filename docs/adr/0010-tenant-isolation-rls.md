# ADR-0010: Tenant isolation — EF filters + PostgreSQL RLS

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
Two layers: EF Core global query filters and Postgres Row-Level Security with `app.current_user_id` set per request by an interceptor. Roles: `api_role` (RLS enforced, least privilege), `worker_role` (only job needs). User id comes only from the validated token. Tests prove cross-user isolation with the app filter bypassed. Every new user-owned table requires an RLS policy.
