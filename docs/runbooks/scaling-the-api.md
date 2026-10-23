# Runbook — running more than one API process

- **Purpose:** what to set and what to check before the API runs as more than one process (ADR-0048). One process is the deployed shape (ADR-0021); this is for the day that changes.
- **Owner:** Eder · **Status:** approved · **Target phase:** 5

## Before
1. Say how many processes there will be: `Deployment__ApiReplicas=<n>` on every API process (1 to 64). It is the setting the shared counts start from. If it is left at 1 the processes see each other in Redis within seconds and count with the larger number, and log a warning (event 9110, `sakerwatch_deployment_api_instances{kind="observed"}` above `declared`): that is a mistake to fix, not a configuration to keep.
2. Redis must be there and healthy (`/health/ready` checks it): the rate limiters count in it from `Deployment__ApiReplicas` above 1 (`RateLimiting__Backend=Auto`, or `Redis` to force it). `Memory` keeps one count in each process and is the wrong choice with several.
3. The proxy must be in `ForwardedHeaders` (`docs/runbooks/local-development.md`): the pre-authentication limiter counts by client address, and behind a proxy that is not listed every user is the proxy.

## After
| Check | How |
|---|---|
| the processes see each other | `sakerwatch_deployment_api_instances{kind="counted"}` is the number of processes; no event 9110 in the logs |
| the limiters count in Redis | keys `v1:ratelimit:{global,expensive,preauth}:{32 hex}` appear while the API is used (`redis-cli --scan --pattern 'v1:ratelimit:*' \| head`); they hold neither a user nor an address |
| Redis is being reached | `sakerwatch_http_rate_limiter_fallback_total` stays at zero; an increase means each process is counting alone and allowing the whole limit to a caller |
| the registration cap holds if Redis goes down | `sakerwatch_marketdata_registration_quota_total{source="process"}` rises only while Redis is down; each process then holds the cap divided by the number of processes |

## What is still per process
- The count of the fall back of the registration cap (`RegistrationFallback`) and the limiter of the process that Redis falls back to are per process on purpose: they are what holds while Redis is down.
- The window of the limiters is a sliding one: a caller cannot get twice the limit by sending at the end of one window and the start of the next. `Retry-After` is the time to the end of the current window, a hint.
