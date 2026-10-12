# ADR-0004: PostgreSQL + MongoDB split and cross-store consistency

- **Status:** accepted
- **Date:** 2026-10-12

## Context
Relational/time-series data needs transactions and joins; LLM interactions and provider payloads are large, variable, write-once.

## Decision
Postgres 16+ with TimescaleDB is the source of truth (hypertables for prices). MongoDB holds `llm_interactions, provider_payloads, analysis_context, eval_runs, job_events` with TTL indexes. Redis for cache/rate-limit with versioned key prefixes.
No distributed transactions: write the Mongo document first with deterministic `_id` (`stockId:promptVersion:runDate`), then the Postgres row + outbox message in one transaction. A janitor removes orphaned Mongo docs. Every step is re-runnable.

## Consequences
Mongo migrations via custom idempotent runner (`_migrations`, checksum, lock). Postgres migrations via EF bundle run as a separate deploy step, never on API startup; forward-only, expand/contract.
