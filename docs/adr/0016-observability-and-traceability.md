# ADR-0016: Observability and end-to-end traceability

- **Status:** accepted (package versions verified at Phase 1 start)
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
We need to answer "what happened, why, to whom, and how long did it take" for any request, job or analysis, including across API, Worker, databases, providers and the LLM, while keeping PII out of telemetry and staying inside the Oracle Always Free budget.

## Decision

### 1. Stack (vendor-neutral, self-hosted)
OpenTelemetry SDK in both hosts → OTLP → OpenTelemetry Collector → Tempo (traces), Prometheus (metrics), Loki (logs) → Grafana. Dashboards and alert rules are code under `deploy/observability/`. Serilog (JSON, console sink) is the logging API surface; OTel log export carries trace context. A compose profile `obs` runs the stack locally; deployed sizing is reviewed in Phase 5 against the free-tier limits.

### 2. Correlation and traceability identifiers
- **Trace context:** W3C `traceparent`/`tracestate` propagated across every boundary (HTTP in/out, job enqueue → worker, outbox → consumer, LLM call).
- **Correlation id:** `X-Correlation-Id` accepted at the edge if well-formed (UUID, else replaced), generated if absent, echoed in every response header, in every ProblemDetails (`traceId` and `correlationId` extensions), and stamped on every log line as `CorrelationId`.
- **Persisted lineage:** `platform.job_queue`, `platform.outbox_messages`, `platform.audit_log` and Mongo documents (`llm_interactions`, `job_events`, `analysis_context`) store `trace_id`, `correlation_id` and (for user-initiated work) `user_id`. A job inherits the creator's trace context as a span *link* (not parent) so long-running work has its own trace.
- **Data lineage / reproducibility:** each analysis result stores prompt template id+version+checksum, model id and parameters, context-package hash, source payload ids, rule-set version. Each price row stores `source` and `ingested_at`. Signals store `rule_set_version` and machine-readable `reasons`.

### 3. Logging
- Structured JSON only; message templates, never string interpolation (analyzer CA2254 as error). `Console.WriteLine` forbidden in `src/**`.
- Mandatory properties: `Timestamp (UTC)`, `Level`, `Service`, `Module`, `Environment`, `Version (git SHA)`, `TraceId`, `SpanId`, `CorrelationId`; `UserId` (internal UUID) only where needed and never email/name/token.
- Levels: `Error` = needs human attention; `Warning` = degraded but handled; `Information` = business-significant state changes (not per-request noise); `Debug` = off outside dev. No logging inside tight loops.
- Redaction: Serilog destructuring policy + allow-list DTO logging; secrets, tokens, `Authorization`, cookies, provider keys, raw prompts/LLM text and request bodies are never logged (raw LLM text lives in Mongo under retention, not in logs). Tests assert redaction.
- Stable `EventId` catalog per module (`docs/observability/README.md`).

### 4. Metrics (OpenTelemetry Meter per module, Prometheus naming)
Naming `sakerwatch_<module>_<what>_<unit>`; labels low-cardinality only (no user id, ticker, or free text). Mandatory sets:
- **RED** per endpoint (route template, method, status class) — rate, errors, duration histogram.
- **Jobs/queue:** depth, oldest-job age, in-flight, attempts, retries, dead-letter count, duration, lease expirations.
- **Idempotency:** requests with key, replays, payload mismatches (422), concurrent conflicts (409), expired keys purged.
- **Providers:** call duration, errors by class, rate-limit tokens remaining, circuit-breaker state, staleness of latest price (seconds).
- **LLM:** request duration, tokens in/out, schema-validation failures, repair retries, concurrency in use, run outcomes.
- **Business:** watchlist items (gauge), analyses completed, signals by value, ingestion rows upserted/corrected.
- **Retention:** rows/docs deleted per policy, dry-run counts.
- **Runtime/infra:** .NET runtime, EF/Npgsql, Mongo driver, Redis, container resources.

### 5. Tracing
`ActivitySource` per module (`SakerWatch.<Module>`). Spans: one per use case (vertical slice) named `<Module>.<UseCase>`, plus auto-instrumented ASP.NET Core, HttpClient, EF Core/Npgsql, Mongo, Redis. Span attributes follow OTel semantic conventions; never put PII or payloads in attributes. Errors set span status and record exceptions. Sampling: 100% in dev; deployed = parent-based ratio with collector tail-sampling that keeps all error and slow traces.

### 6. Health, readiness, startup
`/health/live` (process), `/health/ready` (Postgres, Mongo, Redis; LLM optional/non-blocking), `/health/startup`. Anonymous, no details in the response body.

### 7. Audit trail (distinct from logs)
`platform.audit_log` is append-only (no UPDATE/DELETE grants; hash chain `prev_hash` for tamper evidence), written in the same transaction as the audited change. Events: provisioning, config/prompt/rule-set changes, data export/delete, authorization failures on ownership checks, admin actions.

### 8. SLOs and alerting
SLIs/SLOs from the product spec; multi-window burn-rate alerts on availability and latency; symptom-based alerts first (queue age, dead-letter > 0, price staleness > 24 h on trading days, provider error rate, LLM failure rate, readiness failing). Every alert links to a runbook in `docs/runbooks/`.

### 9. Testing and enforcement
- Tests: trace/correlation propagation API → job → worker; redaction tests; required metrics emitted; health endpoints.
- Analyzer/architecture rules: no `Console.*`, no string-interpolated log templates, no `DateTime.UtcNow` (use `TimeProvider`), every use-case handler runs inside the standard pipeline (logging/tracing/metrics behavior).
- Definition of Done (already global) requires telemetry for every new code path.

## Consequences
Extra infrastructure to run and size; accepted because it is the basis for the SLOs and runbooks. Cardinality discipline and PII redaction are review blockers.
