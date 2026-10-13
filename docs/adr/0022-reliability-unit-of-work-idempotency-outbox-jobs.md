# ADR-0022: Reliability machinery — unit of work, idempotency, outbox/inbox and job queue

- **Status:** accepted
- **Date:** 2026-10-13
- **Deciders:** Eder

## Context
Milestone 1.3 implements the mechanisms promised by ADR-0005 and ADR-0017. The open question is how a use case that touches several module contexts, the idempotency row and the outbox can be exactly-once without distributed transactions, given one PostgreSQL database and one DbContext per module.

## Decision
**1.3a Unit of work.** Every request scope owns one `DbSession`: a single `NpgsqlConnection` that all module DbContexts of the scope use. Commands (`ICommand`, `ICommand<T>`, marked `ITransactionalRequest`) run through `UnitOfWorkBehavior`, placed after validation: it begins a transaction on the session, commits on a success result and rolls back on a failure result or an exception. A save interceptor makes each context join that transaction (`UseTransaction`), so writes of several modules, the idempotency row and the outbox commit or roll back together. Calls nest (only the outermost commits; a nested rollback forbids the outer commit). Outside a unit of work EF behaves normally and the user-context interceptor tags each connection open. Queries never open a transaction. Hosts without a database get a no-op unit of work.

**1.3b Idempotency (Postgres-only operations).** The `Idempotency-Key` behavior runs inside the unit of work: it inserts `platform.idempotency_keys` with `ON CONFLICT DO NOTHING` in the same transaction as the effect. A concurrent duplicate blocks on the unique index until the first transaction ends, then reads the committed row and replays it, or proceeds if the first one rolled back; a lock timeout maps to 409 with `Retry-After`. A crash before commit leaves no row and no effect, so a retry executes once. Same key with a different request hash gives 422. Only success results are stored; failure results roll back and the key is not kept, so a retry re-evaluates (deterministic errors repeat). The explicit `in_progress` lease and takeover of ADR-0017 are reserved for cross-store operations (Mongo plus Postgres, Phase 4), where the effect cannot share one transaction; they are introduced with the first such feature. Oversized responses (> 64 KB) are not snapshotted and a replay returns a conflict pointing to a GET.

**1.3c Outbox and inbox.** Handlers add integration events to an outbox writer; the unit of work persists them in the same transaction. The Worker's dispatcher claims pending rows with `FOR UPDATE SKIP LOCKED`, publishes, and marks them processed, with attempts and exponential backoff with jitter. Consumers record `(consumer, message_id)` in `platform.inbox_messages` in the same transaction as their effect.

**1.3d Job queue.** Jobs are enqueued through `platform.enqueue_job` (deterministic `job_key`, `ON CONFLICT DO NOTHING`). The Worker claims with `FOR UPDATE SKIP LOCKED`, assigns a lease token and expiry, and completes or fails only when the token still matches (fencing). Failures retry with exponential backoff and jitter up to `max_attempts`, then become `dead`. On shutdown the Worker releases its leases. Handlers must be re-runnable.

## Consequences
One shared connection per scope means a scope handles one operation at a time, which holds for HTTP requests and job executions. Long transactions are bounded by the login's `statement_timeout` and `idle_in_transaction_session_timeout`. The cross-store lease path stays unimplemented until it has a real consumer, which avoids carrying untested code.
