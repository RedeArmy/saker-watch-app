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
| `sakerwatch_<module>_usecase_duration_seconds` (histogram, meter `SakerWatch`) | usecase, outcome=success\|failure\|cancelled\|exception, error_type (failures only) | RED per use case; module = second namespace segment, `platform` for building blocks |
| `sakerwatch_http_rate_limited_total` (counter) | policy=global\|expensive\|pre-authentication | requests rejected by the rate limiter |
| `sakerwatch_http_server_duration_seconds` (histogram) | route, method, status_class | RED latency/error |
| `sakerwatch_outbox_messages_total` (counter) | outcome=published\|retry\|dead | outbox delivery health; `dead` > 0 pages a human |
| `sakerwatch_auth_attempts_total` (counter) | outcome=success\|<refusal reason> (Malformed, InvalidSignature, UnknownKey, UnsupportedAlgorithm, Expired, NotYetValid, InvalidIssuer, InvalidAudience, MissingSubject, NotAnAccessToken, KeyDiscoveryFailed, Invalid) | access token validation; a spike of refusals or any `KeyDiscoveryFailed` is a symptom |
| `sakerwatch_provider_calls_total` (counter) | provider, operation=search\|daily, outcome=success\|ratelimited\|transient\|planrestricted\|symbolnotfound\|permanent | calls to external market data providers by outcome; a run of `ratelimited` means the free budget is spent, `planrestricted` means a market is routed to a plan that does not include it |
| `sakerwatch_retention_rows_total` (counter) | target=IdempotencyKeys\|OutboxProcessed\|InboxProcessed\|JobsSucceeded\|JobsDead, mode=dry_run\|delete | rows removed by a retention run, or that a dry run found removable; a delete run that keeps reporting `more remaining` means the backlog outgrows the batch limit |
| `sakerwatch_jobs_queue_depth`, `sakerwatch_jobs_queue_oldest_age_seconds`, `sakerwatch_jobs_in_flight`, `sakerwatch_jobs_dead` (gauges, sampled every `Jobs:StatsInterval`) | none | backlog (pending plus leased jobs whose lease expired, which will be reclaimed), staleness (age of the oldest due or abandoned job), running (live leases) and parked jobs; each count reads one partial index, so sampling never scans finished rows. Gauges are sampled per process: with several worker replicas each reports the same queue, so aggregate with `max`, never `sum` |
| `sakerwatch_jobs_total` (counter), `sakerwatch_jobs_duration_seconds` (histogram) | type, outcome=success\|retry\|dead\|lease_lost\|released | job attempts |
| `sakerwatch_jobs_dead_letter_total` (counter) | none | poison messages |
| `sakerwatch_idempotency_events_total` (counter) | outcome=claimed\|replay\|mismatch\|conflict (`takeover` is reserved for cross-store operations, ADR-0022) | idempotency health |
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
| 9005 | Debug | `Use case {UseCase} cancelled after {ElapsedMs} ms` (caller disconnected or host stopping; not an error) | `LoggingBehavior` |
| 9201 | Information | `Worker started` | `WorkerLifecycleService` |
| 9202 | Information | `Worker stopping gracefully` | `WorkerLifecycleService` |
| 9301 | Information | `Migration run started with {StepCount} steps` | `MigrationPipeline` |
| 9302 | Information | `Migration step {Step} completed, {Applied} migrations applied` | `MigrationPipeline` |
| 9303 | Error | `Migration step {Step} failed` (with exception) | `MigrationPipeline` |
| 9311 | Information | `Mongo migration {Version} {Name} applied` | `MongoMigrationRunner` |
| 9312 | Warning | `Renewing the Mongo migration lock failed; it will be retried on the next tick` (with exception) | `MongoMigrationRunner` |
| 9411 | Information | `Idempotent request replayed from the stored first attempt` | `IdempotencyBehavior` |
| 9412 | Warning | `Idempotency-Key reused with a different request on {Method} {Route}` | `IdempotencyBehavior` |
| 9413 | Warning | `Idempotency-Key still in progress on {Method} {Route}` | `IdempotencyBehavior` |
| 9501 | Debug | `Outbox message {Type} {MessageId} delivered` | `OutboxProcessor` |
| 9502 | Warning | `Outbox message {Type} {MessageId} failed on attempt {Attempt}; retrying at {NextAttemptAt}` (with exception) | `OutboxProcessor` |
| 9503 | Error | `Outbox message {Type} {MessageId} parked as dead after {Attempts} attempts` (with exception) | `OutboxProcessor` |
| 9504 | Error | `Outbox dispatcher iteration failed` (with exception) | `OutboxDispatcherService` |
| 9511 | Warning | `Job {Type} {JobId} failed on attempt {Attempt}; it will retry` (with exception) | `JobProcessor` |
| 9512 | Error | `Job {Type} {JobId} parked as dead after {Attempts} attempts` (with exception) | `JobProcessor` |
| 9513 | Warning | `Job {Type} {JobId} lost its lease; this attempt was rolled back` | `JobProcessor` |
| 9514 | Error | `{Count} job(s) parked as dead because their lease expired on the last allowed attempt` | `JobProcessor` |
| 9515 | Warning | `Renewing the lease of job {JobId} failed; it will be retried on the next tick` (with exception) | `JobProcessor` |
| 9516 | Error | `Job dispatcher iteration failed` (with exception) | `JobDispatcherService` |
| 9517 | Warning | `Sampling the job queue for metrics failed` (with exception) | `JobDispatcherService` |
| 9601 | Error | `Could not obtain the identity provider's signing keys; tokens are refused until they can be fetched` (with exception) | `ClavarisAuthProvider` |
| 9602 | Warning | `Access token rejected for an unclassified reason ({ExceptionType})` (with exception) | `ClavarisAuthProvider` |
| 9603 | Warning | `Could not refresh the identity provider's signing keys; serving the cached keys, fetched {AgeMinutes} minutes ago` (with exception) | `ClavarisAuthProvider` |
| 9701 | Information | `Retention {Target} ({Mode}): {Rows} rows, more remaining: {MoreRemaining}` | `RetentionRunner` |
| 9702 | Information | `Retention is disabled; no daily run will be scheduled` | `RetentionScheduler` |
| 9703 | Information | `Retention run enqueued for today ({Mode})` | `RetentionScheduler` |
| 9704 | Warning | `Could not enqueue today's retention run; it will be retried shortly (after at most one minute)` (with exception) | `RetentionScheduler` |
| 9705 | Warning | `Retention is in dry-run mode in the {Environment} environment: nothing is deleted, so expired data accumulates until Retention:DryRun is switched off` (logged at every start outside Development) | `RetentionScheduler` |
| 9101 | Error | `Unhandled exception in {Method} {Endpoint}` (with exception) | `GlobalExceptionHandler` |
| 2001 | Warning | `Twelve Data returned {Rejected} inconsistent daily rows for {Symbol}; they were dropped` | `TwelveDataProvider` |
| 2002 | Warning | `Twelve Data call failed in transit ({ExceptionType})` | `TwelveDataProvider` |
| 2003 | Warning | `The call budget of {Provider} could not be checked ({ExceptionType}); the call goes ahead and the provider enforces its own limit` | `RedisProviderCallBudget` |
| 2004 | Warning | `The raw Twelve Data response of a {Operation} call could not be kept ({ExceptionType}); the data itself was fetched and parsed` | `TwelveDataProvider` |

### Logging configuration and redaction
Every host calls `AddSakerObservability(serviceName)`: Serilog writes **JSON lines to the console** (rendered compact format, picked up by the log collector) through a bounded asynchronous queue of 10 000 events, so a stalled stdout never blocks a request thread; when the queue is full the newest events are dropped rather than waiting with the properties `Service`, `TraceId`/`SpanId` (current activity), `CorrelationId` (request scope) and message-template parameters. Framework namespaces (`Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `System.Net.Http.HttpClient`) log at Warning and above. HTTP requests produce one Serilog request-log line (health probes only at Verbose; status >= 500 or an exception at Error).
`RedactionEnricher` runs last and masks, at any nesting depth, the value of properties named like `password`, `secret`, `token`, `authorization`, `cookie`, `apikey`, `connectionstring`, `pepper`, `email`, `phone`, `displayname`, `prompt`, `rawresponse`, `requestbody`, `responsebody`, and scrubs JWTs and `Bearer`/`Basic` credentials from any string. Enrichers added after `ApplySakerDefaults` are not covered; add extra enrichers before it. Exception messages are not rewritten, so never put secrets into exception text.
Telemetry export: traces and metrics leave the process only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (OTLP to the Collector, ADR-0016); otherwise nothing is exported.

### Local observability stack (profile `obs`)
`make up-obs` starts the OpenTelemetry Collector, Tempo, Loki, Prometheus and Grafana next to the dev infrastructure (services in `deploy/docker-compose.obs.yml`, configs and the starter dashboard in `deploy/observability/`; images and sizing are dev-only and reviewed in Phase 5). Set `GRAFANA_ADMIN_PASSWORD` in `deploy/.env`. Ports are published on loopback only: Grafana `127.0.0.1:3000`, OTLP `127.0.0.1:4317` (gRPC) and `4318` (HTTP).
Run a host against it with `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317` (and `OTEL_METRIC_EXPORT_INTERVAL=5000` for quick feedback). Traces go app -> Collector -> Tempo, metrics app -> Collector -> Prometheus (scraped on `:8889`), logs app (Serilog OTLP sink) -> Collector -> Loki; Loki lines and Tempo traces share the same trace id, which is how Grafana jumps from a log to its trace. The Collector also deletes `Authorization`/`Cookie` headers and URL query strings from traces and logs. Verified end to end with `Host.Api` on 2026-10-12 (metrics in Prometheus, log lines in Loki and traces in Tempo for the same request).

### Spans
One span per use case, named `<Module>.<UseCase>` on activity source `SakerWatch`, tags `saker.module`, `saker.usecase`, `saker.outcome`, `saker.error_code`; status `Error` only for `ErrorType.Failure` and exceptions (expected failures such as validation or conflicts, and cancellations by the caller, `saker.outcome=cancelled`, do not mark the span as failed).

## SLOs (initial, from `docs/product-spec.md`)
Watchlist read p95 < 300 ms @ 500 items · API availability ≥ 99.5 %/month · price staleness < 24 h on trading days · nightly analysis completes in the configured window.

## Alerts (symptom-first, each linked to a runbook)
Burn-rate (availability, latency) · queue oldest-age · dead-letter > 0 · provider error rate · price staleness · LLM failure rate · readiness failing · signing-key refresh failing (event 9603) · outbox rows parked (`attempts >= max`). An idempotency `in_progress` alert returns with the cross-store operations of Phase 4 (ADR-0022).
