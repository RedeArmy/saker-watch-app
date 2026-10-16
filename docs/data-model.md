# Data model and retention

- **Purpose:** entities per store and the retention policy enforced by the Retention module.
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 (refined as modules are built)

## 1. PostgreSQL (source of truth, schema per module)
- `identity.users`: internal `id` UUIDv7, `external_issuer`, `external_subject` (unique pair), `email`, `display_name`, `locale`, `status` (`active | pending_deletion | deleted`), `deletion_requested_at`, `deletion_due_at`, timestamps. `identity.deletion_tombstones`: `(user_id, external_issuer, external_subject, created_at, idp_deleted_at)` kept until the identity-provider deletion succeeds (ADR-0009). The IdP subject is never a foreign key elsewhere (ADR-0009).
- `marketdata.stocks` (shared catalog: listing identified by `(MIC, ticker)`, currency, time zone, asset type, sector, `is_active`; ADR-0018), `marketdata.price_daily` (hypertable; raw OHLCV and adjusted close; source; `ingested_at`), `marketdata.price_intraday` (hypertable, compression and retention), `marketdata.fx_rates` (pair, rate, `as_of_date`, source), `marketdata.corporate_actions`, `marketdata.symbol_aliases`.
- `watchlist.watchlist_items`: `user_id`, `stock_id`, notes, optional target price, `created_at`; unique `(user_id, stock_id)`. Optional later: simulated holdings (tracking only, no trading).
- Phase 2 additions (ADR-0026 to ADR-0029): `marketdata.symbol_aliases`, `marketdata.corporate_actions`, `marketdata.price_daily` (hypertable: `stock_id`, `trade_date`, raw OHLCV, adjusted close, `source`, `ingested_at`; unique `(stock_id, trade_date)`), `marketdata.fx_rates` (`base`, `quote`, `as_of_date`, `source`, `rate numeric`; unique including the source), `watchlist.portfolios` (`user_id`, trimmed `name` unique per user ignoring case, `description`, `position`) and `watchlist.portfolio_items` (`portfolio_id`, `watchlist_item_id`, `user_id`, `position`, `added_at`; a stock may be in several portfolios or none). Shared market tables have no RLS and are written only by the worker; watchlist tables are user-owned with RLS.
- `analysis.prompt_templates` (id, name, **version**, body, `output_schema_version`, status `draft|active|retired`, checksum), `analysis.analysis_runs` (stock, prompt version, run date, status, attempts, lease fields; unique `(stock_id, prompt_template_id, run_date)`), `analysis.analysis_results` (run, score, flags JSONB, catalysts JSONB, summary, `mongo_doc_id`, `expires_at`).
- `signals.signals` (stock, signal, reasons JSONB, `rule_set_version`, `computed_at`) and `signals.rule_sets` (versioned config).
- `platform.idempotency_keys` (definition in ADR-0017; `locked_until` and `response_headers` are reserved for operations that cross stores and are not written yet), `platform.inbox_messages`, `platform.outbox_messages`, `platform.job_queue` (with `trace_id`, `correlation_id`, lease token; states `pending`, `leased`, `succeeded`, `dead` enforced by a check constraint: a failing job goes back to `pending` or ends `dead`, there is no `failed` state), `platform.audit_log` (append-only, hash chain, `actor_ref` = HMAC-SHA256 of the internal user id with a secret pepper; never email, name or raw user id, so account deletion needs no audit rewrite). See ADR-0005, ADR-0016, ADR-0017.
- Row-Level Security on every user-owned table with `api_role` and `worker_role`; cross-user worker access only through the security-definer functions and per-table policies listed in ADR-0010 (e.g. `watchlist.tracked_stock_ids()`).

### 1.1 Security objects (ADR-0010, listed here as ADR-0010 requires)
| Object | Kind | Who | Purpose |
|---|---|---|---|
| `identity.users` policy `users_self` | RLS policy | `api_role` | a user sees and updates only their own row (`id = app.current_user_id`); no direct insert or delete; the `UPDATE` grant covers only `locale` and `updated_at` (everything else changes through `provision_user` or the owner) |
| `identity.provision_user(...)` | security-definer function | `api_role` | just-in-time provisioning keyed by `(issuer, subject)`; returns `(user_id, user_status, was_created)`; idempotent and race-safe; the id passed in is used only for a new user; email and name are refreshed only for active accounts and only when they differ (an unchanged login writes nothing; `updated_at` means "profile changed") |
| `platform.idempotency_keys` policy `idempotency_keys_owner` | RLS policy | `api_role` | keys are visible and writable only for the current user |
| `platform.purge_expired_idempotency_keys(before, batch)` | security-definer function | `worker_role` | retention purge across users without table access |
| `platform.purge_user_idempotency_keys(user_id)` | security-definer function | `worker_role` | removes every idempotency key of one user, expired or not, for the account-deletion saga (Phase 6): stored responses can hold profile data (email, display name). Nothing calls it yet; added by `PurgeUserIdempotencyKeys` |
| `platform.count_expired_idempotency_keys(before)` | security-definer function | `worker_role` | the dry-run counterpart of the purge (the worker cannot read the table); added by `RetentionSupport` together with partial indexes on `outbox_messages(processed_at)`, `inbox_messages(processed_at)` and `job_queue(state, finished_at)` |
| `platform.enqueue_job(...)` | security-definer function | `api_role`, `worker_role` | `INSERT ... ON CONFLICT (job_key) DO NOTHING`; the API has no table access to the queue. A job may carry `user_id`: the worker runs it with that user as the current user (RLS applies); a session of `api_role` can only queue for its own current user (`42501` otherwise), the worker for any user; null means a system job. User-owned tables a job touches need a `worker_role` policy using the same expression as the API one |
| `platform.append_audit(...)` | security-definer function | `api_role`, `worker_role` | the only way to write the audit log; serializes writers and extends the SHA-256 hash chain |
| `platform.verify_audit_chain()` | security-definer function | `worker_role` | returns the first broken entry id, or null |
| `platform.audit_log` triggers | triggers | everyone, owner included | reject UPDATE, DELETE and TRUNCATE (append-only) |
| `platform.outbox_messages` | grants | `api_role` insert; `worker_role` full | the API writes events with the state change; the worker publishes and purges |
| `platform.inbox_messages`, `platform.job_queue` | grants | `worker_role` | consumer dedupe and job leases |

## 2. MongoDB (heavy, variable, write-once documents)
All documents carry `schemaVersion` and `createdAt`; indexes (including TTL) come from versioned migrations.
- `llm_interactions`: rendered prompt, model id and params, raw response, parsed JSON, token counts, latency. TTL 12 months.
- `provider_payloads`: raw provider responses for reprocessing. TTL 30–90 days (configurable).
- `analysis_context`: the exact data package given to the LLM (reproducibility).
- `eval_runs`: golden-set evaluation results.
- `job_events`: execution audit trail. TTL 30 days.
- Enforcement (migration `V0001 base-collections`; a collection that already exists gets the declared validator through `collMod`, which governs later writes and leaves existing documents alone): every collection has a `$jsonSchema` validator requiring `schemaVersion` (int) and `createdAt` (date), and a TTL index `ttl_created_at` on `createdAt`: `llm_interactions` 365 days, `analysis_context` 365 days, `provider_payloads` 90 days (the maximum; Retention can tighten it with `collMod`), `job_events` 30 days, `eval_runs` none.
- Cross-store consistency: Mongo document first with deterministic `_id` (`stockId:promptVersion:runDate`), then Postgres row plus outbox in one transaction; janitor removes orphans (ADR-0004).

## 3. Redis
Cache and rate limiting only, with versioned key prefixes (`v1:price:{ticker}`) and TTLs; no migrations. Keys are built only through `RedisKeys.For(...)` (version prefix `v1`, segments limited to letters, digits and `-_.`). The API rate limiter stays in memory because there is a single API instance (ADR-0021).

## 4. Migrations
Postgres: EF Core migrations committed; Timescale features as raw-SQL migrations; EF migration bundle run as a separate deploy step, never on API startup; forward-only with expand/contract. Mongo: custom idempotent runner (`IMongoMigration`, ordered, `_migrations` with checksum and lock) plus read-side upcasters. Every migration is tested on an empty database and on one seeded at the previous version.

## 5. Retention (configurable defaults, enforced by idempotent, batched, observable Retention jobs with dry-run mode)
Implemented in milestone 1.5d for the platform tables (idempotency keys, processed outbox, inbox, finished and dead jobs); the other rows of this table arrive with their data in later phases. The worker enqueues one `retention.run.v1` job per UTC day (`retention:{dryrun|delete}:{yyyyMMdd}`); the default mode is **dry run** (`Retention:DryRun=true`) until an operator has read a dry run and switches it off. Ages come from `Retention:*Days`; each statement removes at most `Retention:BatchSize` rows and a run stops after `Retention:MaxBatchesPerTarget` batches per target (the next run continues). Unprocessed outbox rows and pending jobs are never removed, and dead jobs are removed 90 days after they died (there is no "resolved" state yet).
| Data | Retention |
|---|---|
| `price_daily`, daily FX | Indefinite |
| `price_intraday` | 90 days (Timescale retention; compression after 7 days) |
| Analysis summary, score, flags, signals | Indefinite |
| `analysis_results.expires_at` | Moment the linked Mongo documents (`llm_interactions`, `analysis_context`) are purged; the summary and score stay |
| `llm_interactions`, `analysis_context` | 12 months, then only the summary remains |
| `eval_runs` | Indefinite |
| `provider_payloads` | 30–90 days |
| `job_events` (Mongo) | 30 days |
| `platform.job_queue` finished rows | 14 days; dead-lettered rows kept until resolved, then 90 days |
| `platform.outbox_messages` / `inbox_messages` processed rows | 7 days |
| `platform.idempotency_keys` | 24–72 hours |
| `platform.audit_log` | 24 months, removed by partition drop by the retention role only (the one permitted exception to append-only) |
| Telemetry (deployed sizing set in Phase 5) | logs 30 days, traces 7 days, metrics 13 months |
| Soft-deleted users' personal data | Hard-deleted after the 30-day grace period; the Clavaris identity is deleted next, then the tombstone is removed (ADR-0009) |

**Function and database hardening (migrations `HardenIdentityPrivileges`, `HardenPlatformPrivileges`).** Every security-definer function of `platform` and `identity` ends its `search_path` with `pg_temp` (a test fails otherwise); functions created later by the migrating role are not executable by `PUBLIC` (default privileges); `PUBLIC` has no `TEMPORARY` on the database.
