# Runbook — cache of the daily numbers

- **Purpose:** what to do when the performance windows come from the cache and look wrong, when Redis is down, or when the cache has to be turned off or emptied (ADR-0041).
- **Owner:** Eder · **Status:** approved · **Target phase:** 2

## How it works, in two lines
The windows of a listing are kept in Redis under `v1:perf:{stockId}:{stamp}:{maxGapDays}`, where `stamp` is the UTC ticks of `marketdata.ingestion_state.last_success_at`. A load of data for the listing makes a new stamp and so a new key; the old key is never read again and expires after 26 hours. Nothing is deleted by the application, and a failure of Redis is a miss that reads PostgreSQL.

## Signals
| Signal | Meaning |
|---|---|
| `sakerwatch_marketdata_perf_cache_total{result="hit"}` vs `{result="miss"}` | after the daily load every listing misses once; a miss rate that stays high means keys are not being kept (Redis memory, a TTL, a failing write) |
| `sakerwatch_marketdata_perf_cache_total{result="error"}` increases, log events 2801 and 2802 | Redis could not be reached (2801) or an entry could not be read (2802); the watchlist is slower, not wrong |
| the watchlist sorted by a window is slow again (p95 near 850 ms in the baseline) | the cache is off or missing; see below |

## A number looks wrong
1. Find the listing and compare with the database: the windows come from `marketdata.price_daily` (adjusted closes, ADR-0030). If the database says something else, the cache is stale.
2. A cache entry is stale only when the prices changed without `last_success_at` changing: prices edited by hand in `price_daily`, or a change of the exchange calendars (they decide the previous trading day of the one-day window) without a load. Every other change makes a new key.
3. Fix it: delete the keys of the listing (`v1:perf:{stockId}:*`), or all of them (`v1:perf:*`, with `SCAN`, never `KEYS` in production), or bump `RedisKeys.Version` in a release so every key is new. The next read computes the windows again and keeps them.

## Redis is down
Nothing to do for correctness: every read goes to PostgreSQL and the watchlist is slower (see the baseline). The connection to Redis fails fast while it is down (`BacklogPolicy.FailFast`): a cache read is a miss at once and not after the five seconds of the command timeout, so the cost is the database read and nothing more. The host makes the connection when it starts and waits up to `connectTimeout` (5 s by default) if Redis is not there. Fix Redis; the cache fills itself on the first reads. If the extra load on PostgreSQL is a problem while it is fixed, there is nothing to turn off: the failed calls are already misses.

## Turn it off or change it
- `MarketData:Cache:Enabled=false` (an environment variable `MARKETDATA__CACHE__ENABLED=false`) sends every read to PostgreSQL; the keys already there expire by themselves.
- `MarketData:Cache:Ttl` (default 26 hours, 1 minute to 7 days) only bounds how long unused keys stay.
- A change of `MarketData:Performance:MaxGapDays` needs nothing: it is part of the key.

## Measure it
`bash scripts/run-k6.sh` repeats the load baseline with the cache on and then off on a throwaway stack (see `docs/reports/phase-2-load-baseline.md`).
