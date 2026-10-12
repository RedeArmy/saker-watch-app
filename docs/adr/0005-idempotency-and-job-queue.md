# ADR-0005: Idempotency, outbox/inbox and Postgres-backed job queue

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
- HTTP: `Idempotency-Key` on retryable mutating endpoints; table `platform.idempotency_keys(user_id,key,request_hash,response_status,response_body,expires_at)`, unique `(user_id,key)`. Same key+payload replays; different payload → 422; concurrent duplicate → wait or 409. Prefer natural idempotency (`PUT`).
- Jobs: deterministic identity (`analysis:{stockId}:{promptVersion}:{date}`) with unique index; enqueue `INSERT … ON CONFLICT DO NOTHING`; dequeue `FOR UPDATE SKIP LOCKED`; leases/visibility timeout, attempts, exponential backoff + jitter, dead-letter, lease release on shutdown. Quartz.NET only schedules.
- Outbox for integration events after commit; consumers dedupe via inbox.
- Ingestion upserts `ON CONFLICT (stock_id,date,source) DO UPDATE`.

## Consequences
At-least-once delivery; all handlers must be re-runnable. Retention of idempotency keys 24–72 h.
