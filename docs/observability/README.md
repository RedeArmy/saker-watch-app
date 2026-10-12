# Observability catalog

- **Purpose:** concrete telemetry contract: identifiers, metric names, log events, SLOs, alerts (ADR-0016).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 (dashboards/alerts: 5)

## Identifiers
`traceparent` (W3C) · `X-Correlation-Id` (echoed on every response and ProblemDetails) · `trace_id`/`correlation_id` columns on jobs, outbox, audit log and Mongo documents.

## Metric prefixes
`sakerwatch_http_*`, `sakerwatch_jobs_*`, `sakerwatch_idempotency_*`, `sakerwatch_provider_*`, `sakerwatch_llm_*`, `sakerwatch_watchlist_*`, `sakerwatch_analysis_*`, `sakerwatch_retention_*`. Labels: low cardinality only.

| Metric (type) | Labels | Purpose |
|---|---|---|
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

## SLOs (initial, from the product spec)
Watchlist read p95 < 300 ms @ 500 items · API availability ≥ 99.5 %/month · price staleness < 24 h on trading days · nightly analysis completes in the configured window.

## Alerts (symptom-first, each linked to a runbook)
Burn-rate (availability, latency) · queue oldest-age · dead-letter > 0 · provider error rate · price staleness · LLM failure rate · readiness failing · idempotency `in_progress` stuck.
