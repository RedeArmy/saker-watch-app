# Runbook — queue-backlog

- **Purpose:** what to do when jobs or outbox messages pile up, die, or stop being processed.
- **Owner:** Eder · **Status:** draft · **Target phase:** 5 (first procedure written in Phase 1; completed with the dashboards and alerts)

## Symptoms and where they show
| Signal | Meaning |
|---|---|
| `sakerwatch_jobs_queue_depth`, `sakerwatch_jobs_queue_oldest_age_seconds` growing | the worker is not keeping up or is down |
| `sakerwatch_jobs_dead` (gauge) above 0, or `sakerwatch_jobs_total{outcome="dead"}` / `sakerwatch_jobs_dead_letter_total` increasing | a job used all its attempts (poison payload, bug, or a dependency down for too long) |
| `sakerwatch_jobs_total{outcome="lease_lost"}` increases | handlers outlive their lease or two workers collide; see below |
| outbox messages with `outcome=dead` in `sakerwatch_outbox_messages_total` | a message was parked after `Outbox:MaxAttempts` |

(Exact metric names are in `docs/observability/README.md`.)

## Procedure (read-only first)
1. Is the worker running and healthy? Check its logs for the lifecycle events and the dispatcher start. A stopped worker is the usual cause: restart it, backlog drains by itself.
2. Look at the rows with the admin connection (never the runtime logins):
   - Pending and oldest: `SELECT state, count(*), min(available_at) FROM platform.job_queue GROUP BY state;`
   - Dead jobs and why: `SELECT id, job_key, type, attempts, last_error, finished_at FROM platform.job_queue WHERE state = 'dead' ORDER BY finished_at DESC LIMIT 50;`
   - Parked outbox messages: `SELECT id, type, attempts, last_error FROM platform.outbox_messages WHERE processed_at IS NULL AND attempts >= <MaxAttempts>;`
3. A job whose type is unknown, whose payload does not fit its type or that has no handler is parked at the first attempt (`attempts = 1`, `last_error` names the cause): that is a deploy or data problem (a worker older than the enqueuer, a removed job type), not a transient failure. Decide per dead row: fix the cause first (a provider outage, a bug), then requeue; requeueing without a fix only repeats the failure.
4. Requeue a dead job (the handler must be re-runnable, ADR-0022): `UPDATE platform.job_queue SET state = 'pending', attempts = 0, available_at = now(), lease_token = NULL, lease_expires_at = NULL, finished_at = NULL WHERE id = '<id>' AND state = 'dead';`
5. A parked outbox message has `next_attempt_at = 'infinity'`, so it is never due again by itself (raising `Outbox:MaxAttempts` does not revive it). Requeue it: `UPDATE platform.outbox_messages SET attempts = 0, next_attempt_at = now(), last_error = NULL WHERE id = '<id>' AND processed_at IS NULL;`
6. Dead rows are kept for the retention period (`Retention:JobsDeadDays`, 90 days by default) and then purged by the retention job; parked outbox messages are not purged automatically.

## Handler outlives its lease or its transaction
A job that waits on external I/O for longer than the worker login's `idle_in_transaction_session_timeout` loses its connection and retries until dead (ADR-0022, "keep the unit of work short"). Fix the handler (fetch before the transaction) rather than raising the timeout.
