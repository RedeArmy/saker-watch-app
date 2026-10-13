# Observability catalog

- **Purpose:** concrete telemetry contract: identifiers, metric names, log events, SLOs, alerts (ADR-0016).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 (dashboards/alerts: 5)

## Identifiers
`traceparent` (W3C) · `X-Correlation-Id` (echoed on every response and ProblemDetails) · `trace_id`/`correlation_id` columns on jobs, outbox, audit log and Mongo documents.

## Metric prefixes
This table is the starter set; ADR-0016 section 4 defines the complete mandatory sets, which are added here as each module is built.

`sakerwatch_http_*`, `sakerwatch_jobs_*`, `sakerwatch_idempotency_*`, `sakerwatch_provider_*`, `sakerwatch_price_*`, `sakerwatch_llm_*`, `sakerwatch_watchlist_*`, `sakerwatch_analysis_*`, `sakerwatch_retention_*`. Labels: low cardinality only.

| Metric (type) | Labels | Purpose |
|---|---|---|
| `sakerwatch_<module>_usecase_duration_seconds` (histogram, meter `SakerWatch`) | usecase, outcome=success\|failure\|exception, error_type (failures only) | RED per use case; module = second namespace segment, `platform` for building blocks |
| `sakerwatch_http_rate_limited_total` (counter) | policy=global\|expensive | requests rejected by the rate limiter |
| `sakerwatch_http_server_duration_seconds` (histogram) | route, method, status_class | RED latency/error |
| `sakerwatch_jobs_queue_depth` / `_oldest_age_seconds` (gauge) | queue | backlog and staleness |
| `sakerwatch_jobs_dead_letter_total` (counter) | queue | poison messages |
| `sakerwatch_idempotency_events_total` (counter) | outcome=replay\|mismatch\|conflict\|takeover | idempotency health |
| `sakerwatch_provider_call_duration_seconds` (histogram) | provider, outcome | provider health |
| `sakerwatch_price_staleness_seconds` (gauge) | market | freshness SLO |
| `sakerwatch_llm_tokens_total` (counter) | direction=in\|out, model | cost/capacity |
| `sakerwatch_llm_run_total` (counter) | outcome | pipeline health |
| `sakerwatch_retention_deleted_total` (counter) | policy, dry_run | retention evidence |

## Log events
Each module reserves an EventId range (Identity 1000s, MarketData 2000s, Watchlist 3000s, Analysis 4000s, Signals 5000s, Calculator 6000s, Retention 7000s, Platform 9000s); events are listed here when introduced.

### Registered log events
| EventId | Level | Template | Source |
|---|---|---|---|
| 9001 | Information | `Use case {UseCase} completed in {ElapsedMs} ms` | `LoggingBehavior` |
| 9002 | Information | `Use case {UseCase} rejected with {ErrorCode} ({ErrorType}) in {ElapsedMs} ms` | `LoggingBehavior` (expected failure) |
| 9003 | Error | `Use case {UseCase} failed with {ErrorCode} in {ElapsedMs} ms` | `LoggingBehavior` (`ErrorType.Failure`) |
| 9004 | Error | `Use case {UseCase} threw after {ElapsedMs} ms` (with exception) | `LoggingBehavior` |
| 9201 | Information | `Worker started` | `WorkerLifecycleService` |
| 9202 | Information | `Worker stopping gracefully` | `WorkerLifecycleService` |
| 9101 | Error | `Unhandled exception in {Method} {Endpoint}` (with exception) | `GlobalExceptionHandler` |

### Logging configuration and redaction
Every host calls `AddSakerObservability(serviceName)`: Serilog writes **JSON lines to the console** (rendered compact format, picked up by the log collector) with the properties `Service`, `TraceId`/`SpanId` (current activity), `CorrelationId` (request scope) and message-template parameters. Framework namespaces (`Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `System.Net.Http.HttpClient`) log at Warning and above. HTTP requests produce one Serilog request-log line (health probes only at Verbose; status >= 500 or an exception at Error).
`RedactionEnricher` runs last and masks, at any nesting depth, the value of properties named like `password`, `secret`, `token`, `authorization`, `cookie`, `apikey`, `connectionstring`, `pepper`, `email`, `phone`, `displayname`, `prompt`, `rawresponse`, `requestbody`, `responsebody`, and scrubs JWTs and `Bearer`/`Basic` credentials from any string. Enrichers added after `ApplySakerDefaults` are not covered; add extra enrichers before it. Exception messages are not rewritten, so never put secrets into exception text.
Telemetry export: traces and metrics leave the process only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (OTLP to the Collector, ADR-0016); otherwise nothing is exported.

### Spans
One span per use case, named `<Module>.<UseCase>` on activity source `SakerWatch`, tags `saker.module`, `saker.usecase`, `saker.outcome`, `saker.error_code`; status `Error` only for `ErrorType.Failure` and exceptions (expected failures such as validation or conflicts do not mark the span as failed).

## SLOs (initial, from `docs/product-spec.md`)
Watchlist read p95 < 300 ms @ 500 items · API availability ≥ 99.5 %/month · price staleness < 24 h on trading days · nightly analysis completes in the configured window.

## Alerts (symptom-first, each linked to a runbook)
Burn-rate (availability, latency) · queue oldest-age · dead-letter > 0 · provider error rate · price staleness · LLM failure rate · readiness failing · idempotency `in_progress` stuck.
