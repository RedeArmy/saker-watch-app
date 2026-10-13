# ADR-0017: Idempotency — detailed design

- **Status:** accepted (extends ADR-0005; the algorithm for Postgres-only operations is refined by ADR-0022)
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
Retries happen at every layer (clients, proxies, job redelivery, restarts, provider timeouts). Every operation with side effects must be safe to repeat and must produce exactly one effect.

## Decision
Idempotency is implemented in layers; each layer states its mechanism and its test.

| Layer | Mechanism | Verified by |
|---|---|---|
| HTTP create/trigger | `Idempotency-Key` pipeline behavior + `platform.idempotency_keys` | concurrency + mismatch + replay tests |
| HTTP update/delete | Natural idempotency (`PUT` upsert, `DELETE` returns 204 even if absent), optimistic concurrency via ETag/`If-Match` | repeat-request tests |
| Commands/handlers | Business-key uniqueness (unique indexes) + domain state checks (re-applying a transition is a no-op) | handler run-twice tests |
| Jobs | Deterministic `job_key` unique index, claim by `FOR UPDATE SKIP LOCKED`, lease with fencing token | crash/redelivery tests |
| Events | Outbox (publish after commit) + inbox dedupe by `(consumer, message_id)` | duplicate-delivery tests |
| Ingestion | `INSERT … ON CONFLICT (stock_id, date, source) DO UPDATE` | re-run/backfill tests |
| Mongo | Upsert by deterministic `_id`; janitor for orphans | partial-failure tests |
| Outbound calls | Idempotent verbs only (GET/PUT); LLM calls cached by request hash; provider "idempotency keys" if supported | resilience tests |

### HTTP `Idempotency-Key` contract
- Required on `POST` endpoints that create or trigger work (e.g. add watchlist item if modeled as POST, trigger on-demand analysis, account export/delete); optional elsewhere; ignored on safe methods.
- Key: client-generated, 16–64 chars `[A-Za-z0-9_-]`, typically UUIDv4/v7. Invalid → 400.
- Scope: `(user_id, http_method, route_template, key)`. The same key on another route/user is a different record.
- `request_hash`: SHA-256 of canonical request (method, route template with resolved parameters, normalized body — JSON canonicalized, insignificant whitespace removed, selected headers excluded).

### Table (`platform.idempotency_keys`)
```
user_id uuid not null, key text not null, method text not null, route text not null,
request_hash bytea not null, state text not null check (state in ('in_progress','completed')),
locked_until timestamptz, response_status int, response_headers jsonb, response_body bytea,
created_at timestamptz not null, expires_at timestamptz not null, trace_id text,
primary key (user_id, method, route, key)
```
Row-level security applies (user-owned). Retention: delete after `expires_at` (24–72 h, configurable) by the Retention module.

### Algorithm
1. Validate key; compute `request_hash`.
2. `INSERT … ON CONFLICT DO NOTHING` with `state='in_progress'`, `locked_until = now + lease`.
3. If inserted → execute the handler **in the same database transaction** as the idempotency row update where the operation is Postgres-only; on success set `completed` with the response snapshot (body cap 64 KB; larger responses must be re-derivable via GET and return a `303`/link instead), commit, return the response.
4. If not inserted → read row:
   - `request_hash` differs → **422** (`idempotency-key-reuse`).
   - `completed` → replay stored status/headers/body with header `Idempotent-Replayed: true`.
   - `in_progress` and `locked_until > now` → **409** with `Retry-After` (optionally wait up to a bounded time).
   - `in_progress` and lease expired (crashed owner) → take over via conditional `UPDATE … WHERE locked_until < now` (compare-and-set); only one taker wins.
5. Failures: unexpected 5xx releases/deletes the row so the client can retry; deterministic 4xx validation errors are not stored (they fail before step 2); business conflicts (409/422 from domain) may be stored as the result.
6. Cross-store operations (Mongo + Postgres, ADR-0004) set the Mongo write first with a deterministic `_id`, so a retry after a crash re-writes the same document.

### Jobs and consumers
- `job_key` examples: `analysis:{stockId}:{promptVersion}:{yyyy-MM-dd}`, `ingest:{exchange}:{yyyy-MM-dd}`. Enqueue is `INSERT … ON CONFLICT DO NOTHING`.
- Completion/failure updates are guarded by the **lease token (fencing)**: a worker whose lease expired cannot overwrite a newer attempt's result.
- Handlers declare they are re-runnable; effects use upserts or "already done" checks; time comes from `TimeProvider`, randomness is injected.
- Consumers record `(consumer, message_id)` in `platform.inbox_messages` in the same transaction as their effect.

### Observability and operations
Metrics and logs per ADR-0016 (replays, mismatches, conflicts, takeovers, key age). A runbook documents inspecting and clearing stuck `in_progress` keys.

## Testing requirements (Definition of Done for any mutating feature)
- N parallel identical requests → exactly one effect, others replay or 409.
- Same key, different payload → 422.
- Crash between effect and response → retry yields the same result with one effect.
- Handler/job executed twice → identical final state ("run-twice-same-result").
- Property tests for canonicalization/hash stability.

## Consequences
Extra table, a pipeline behavior and discipline in every handler. Response snapshots consume space (bounded by TTL and the size cap).
