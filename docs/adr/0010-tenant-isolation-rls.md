# ADR-0010: Tenant isolation — EF filters + PostgreSQL RLS

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
Two layers: EF Core global query filters and Postgres Row-Level Security with `app.current_user_id` set per request by an interceptor. Roles: `api_role` (RLS enforced, least privilege), `worker_role` (only job needs). User id comes only from the validated token. Tests prove cross-user isolation with the app filter bypassed. Every new user-owned table requires an RLS policy.

## Worker access under RLS (decided 2026-10-12)
`worker_role` is `NOSUPERUSER NOBYPASSRLS`. Jobs that legitimately span users get access only through narrow, explicit mechanisms, never a blanket `USING (true)` on user tables:
- **Security-definer functions** owned by a migration role with a fixed `search_path`, for specific cross-user reads, e.g. `watchlist.tracked_stock_ids()` (distinct stock ids across all watchlists, no user ids returned) used by ingestion and the analysis scheduler.
- **Per-table policies `TO worker_role`** limited to the exact operation a job needs: Retention purge (`DELETE` of expired rows on `platform.idempotency_keys`, `platform.outbox_messages`, `platform.inbox_messages`, finished `platform.job_queue`), and the deletion saga (`SELECT/DELETE` on rows of one user selected by id from the tombstone/pending-deletion set).
- Each function and policy is reviewed in the migration PR, listed in `docs/data-model.md`, and covered by tests asserting `worker_role` cannot read user-owned rows outside them.
