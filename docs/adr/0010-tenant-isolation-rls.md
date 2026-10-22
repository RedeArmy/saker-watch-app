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

## The table owner and `FORCE ROW LEVEL SECURITY` (decided 2026-10-21)
Row-level security is enabled on the user-owned tables (`identity.users`, `platform.idempotency_keys`, `watchlist.watchlist_items`, `watchlist.portfolios`, `watchlist.portfolio_items`) with `ENABLE`, not `FORCE`: the role that owns a table is exempt from its policies. This is a decision and not an omission (migration review of Phase 2, finding 1):
- The owner is the migrator's admin login. No runtime connection uses it: `Host.Api` and `Host.Worker` connect with logins that are members of `api_role` and `worker_role`, which are `NOSUPERUSER NOBYPASSRLS` and own nothing.
- `FORCE` would also bind the owner of the security-definer functions, and `watchlist.tracked_stock_ids()` (above) depends on that owner reading across users. Making it work under `FORCE` needs one more policy per table for the function's owner, which widens what the policies allow only to keep a flag that nothing here needs.
- What keeps the exception safe is checked instead of assumed: `RuntimeLogins_OwnNothing_AndCannotBypassRowLevelSecurity` fails when a runtime login is a superuser, can bypass RLS, or is, or is a member of, the owner of any table with row-level security. Adding a table to the runtime role, or giving the runtime a more powerful login, fails that test before it reaches a deployment.
- If a future deployment connects the application as the owner, this decision is void and `FORCE` plus the policies of the function are required first.

