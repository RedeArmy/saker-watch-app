# ADR-0021: Persistence — EF Core, a single migrator host, database roles and Redis rate limiting

- **Status:** accepted
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
Phase 1 milestone 1.2 builds the persistence layer decided in ADR-0004, ADR-0005, ADR-0010 and ADR-0017. Three questions were still open: which data-access technology, how several module contexts and MongoDB are migrated as one deploy step, and how database logins relate to the `api_role` and `worker_role` of ADR-0010.

## Decision
- **EF Core is the data-access technology; Dapper is not added.** EF gives the migration tooling, the global query filters and the connection interceptor that ADR-0010 relies on, and maps aggregates with value objects and typed ids. Hot or set-based statements (job claim with `FOR UPDATE SKIP LOCKED`, `INSERT … ON CONFLICT`, price upserts, `COPY`) use raw SQL through EF (`ExecuteSql`, `SqlQuery<T>`) or Npgsql directly, always parameterized and always covered by an integration test against a real PostgreSQL. Revisit only with measurements (Phase 2 read performance).
- **One migrator host replaces per-context EF bundles.** `Host.Migrator` is a console job that applies, in order, the PostgreSQL migrations of every context (platform first, because it creates roles and extensions) and then the MongoDB migrations, using the admin connection, and exits non-zero on the first failure. It remains a separate deploy step; the API and Worker never migrate on startup. An EF bundle is per context and cannot also run the MongoDB runner, so one executable keeps the order explicit and testable. Each context keeps its history table in its own schema (`<schema>.__ef_migrations_history`).
- **Migrations are forward-only.** `Down` throws. Schema changes that are not expressible in the model (roles, extensions, RLS policies, grants, security-definer functions) are hand-written SQL inside migrations; the rest is generated with `dotnet ef` (local tool manifest `dotnet-tools.json`).
- **Roles.** `api_role` and `worker_role` are `NOLOGIN NOSUPERUSER NOBYPASSRLS` group roles created idempotently by the platform migration. Each environment creates its own login roles with secret passwords outside the repository and grants them one group role; per-login settings such as `statement_timeout`, `lock_timeout` and `idle_in_transaction_session_timeout` are set on those logins (role settings are not inherited through membership). The runtime connection string uses a login, never the admin login.
- **`app.current_user_id`.** An EF connection interceptor sets it from the validated principal on every connection open, including the empty value for no user, so pooled connections never carry a previous caller.
- **Rate limiting stays in memory.** There is a single API instance on a single node, so the ASP.NET Core limiter is sufficient; Redis is wired for cache, locks and BFF sessions. A distributed limiter is revisited if the API is ever scaled horizontally. *Superseded in part by ADR-0048: the limiters count in Redis when there is more than one API process.*

## Consequences
One more executable to build, publish and run before each deploy. Operators must create the login roles; a runbook documents it. Raw SQL is allowed but confined to Infrastructure and tested against the real engine.
