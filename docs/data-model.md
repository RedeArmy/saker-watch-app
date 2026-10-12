# Data model and retention

- **Purpose:** entities per store and the retention policy enforced by the Retention module.
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 (refined as modules are built)

## 1. PostgreSQL (source of truth, schema per module)
- `identity.users`: internal `id` UUIDv7, `external_issuer`, `external_subject` (unique pair), `email`, `display_name`, `locale`, `status` (`active | pending_deletion | deleted`), `deletion_requested_at`, `deletion_due_at`, timestamps. `identity.deletion_tombstones`: `(user_id, external_issuer, external_subject, created_at, idp_deleted_at)` kept until the identity-provider deletion succeeds (ADR-0009). The IdP subject is never a foreign key elsewhere (ADR-0009).
- `marketdata.stocks` (shared catalog: listing identified by `(MIC, ticker)`, currency, time zone, asset type, sector, `is_active`; ADR-0018), `marketdata.price_daily` (hypertable; raw OHLCV and adjusted close; source; `ingested_at`), `marketdata.price_intraday` (hypertable, compression and retention), `marketdata.fx_rates` (pair, rate, `as_of_date`, source), `marketdata.corporate_actions`, `marketdata.symbol_aliases`.
- `watchlist.watchlist_items`: `user_id`, `stock_id`, notes, optional target price, `created_at`; unique `(user_id, stock_id)`. Optional later: simulated holdings (tracking only, no trading).
- `analysis.prompt_templates` (id, name, **version**, body, `output_schema_version`, status `draft|active|retired`, checksum), `analysis.analysis_runs` (stock, prompt version, run date, status, attempts, lease fields; unique `(stock_id, prompt_template_id, run_date)`), `analysis.analysis_results` (run, score, flags JSONB, catalysts JSONB, summary, `mongo_doc_id`, `expires_at`).
- `signals.signals` (stock, signal, reasons JSONB, `rule_set_version`, `computed_at`) and `signals.rule_sets` (versioned config).
- `platform.idempotency_keys` (definition in ADR-0017), `platform.inbox_messages`, `platform.outbox_messages`, `platform.job_queue` (with `trace_id`, `correlation_id`, lease token), `platform.audit_log` (append-only, hash chain, `actor_ref` = HMAC-SHA256 of the internal user id with a secret pepper; never email, name or raw user id, so account deletion needs no audit rewrite). See ADR-0005, ADR-0016, ADR-0017.
- Row-Level Security on every user-owned table with `api_role` and `worker_role`; cross-user worker access only through the security-definer functions and per-table policies listed in ADR-0010 (e.g. `watchlist.tracked_stock_ids()`).

## 2. MongoDB (heavy, variable, write-once documents)
All documents carry `schemaVersion` and `createdAt`; indexes (including TTL) come from versioned migrations.
- `llm_interactions`: rendered prompt, model id and params, raw response, parsed JSON, token counts, latency. TTL 12 months.
- `provider_payloads`: raw provider responses for reprocessing. TTL 30–90 days (configurable).
- `analysis_context`: the exact data package given to the LLM (reproducibility).
- `eval_runs`: golden-set evaluation results.
- `job_events`: execution audit trail. TTL 30 days.
- Cross-store consistency: Mongo document first with deterministic `_id` (`stockId:promptVersion:runDate`), then Postgres row plus outbox in one transaction; janitor removes orphans (ADR-0004).

## 3. Redis
Cache and rate limiting only, with versioned key prefixes (`v1:price:{ticker}`) and TTLs; no migrations.

## 4. Migrations
Postgres: EF Core migrations committed; Timescale features as raw-SQL migrations; EF migration bundle run as a separate deploy step, never on API startup; forward-only with expand/contract. Mongo: custom idempotent runner (`IMongoMigration`, ordered, `_migrations` with checksum and lock) plus read-side upcasters. Every migration is tested on an empty database and on one seeded at the previous version.

## 5. Retention (configurable defaults, enforced by idempotent, batched, observable Retention jobs with dry-run mode)
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
