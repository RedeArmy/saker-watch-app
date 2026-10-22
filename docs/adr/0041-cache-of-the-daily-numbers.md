# ADR-0041: A cache for the daily numbers

- **Status:** accepted
- **Date:** 2026-10-21
- **Deciders:** Eder

## Context
The performance windows of a listing (ADR-0030) are read by the watchlist, the portfolio metrics and the calculator through one reader, and they cost nine index probes per listing: 0.3 to 0.5 s for 500 listings with six years each, against a budget of 300 ms for the whole watchlist (ADR-0030, ADR-0033). ADR-0033, 0035 and 0038 left the fix for milestone 2.8. The numbers change only when a listing loads data, which is once a day per market, so almost every read repeats work whose answer cannot have changed.

What must not happen: a user sees a number that the database would not give, or two screens that disagree (ADR-0033 says a list and a portfolio cannot disagree by construction).

## Options considered
- **Delete the keys after ingestion commits.** Easy to explain, but the delete is a second step outside the transaction that wrote the prices: a reader between the commit and the delete, or a delete lost to a Redis outage, leaves a stale report served for as long as its TTL. It also needs the ingestion code to know the cache.
- **Key by the newest stored day.** Needs no extra statement (the newest bar is read anyway), but a backfill that fills a hole in the middle, or a rewrite of the adjusted history after a split (event 2112), changes the windows without changing the newest day.
- **Key by the listing's last successful load.** `ingestion_state.last_success_at` is written by `RecordSuccessAsync` in the same unit of work as every upsert of prices, in the daily refresh and in the backfill. One cheap statement reads it for all the listings of a request.
- **Cache the whole snapshot, status included.** The status depends on the time of the request (a session settles at a moment, ADR-0029) and on the failed flag, so the cache would be wrong across a settle time. The pieces that depend on time are two cheap statements and are not worth caching.
- **An in-process memory cache.** No new moving part, but each replica warms its own copy and the API and the worker would not share it.

## Decision
- **What is cached:** the `PerformanceReport` of a listing (the newest adjusted close and the eight windows), nothing else. The price status, the newest raw bar and the catalog are read as before.
- **The key** is `v1:perf:{stockId}:{stamp}:{maxGapDays}`: `stamp` is the UTC ticks of the listing's `last_success_at`. A load of data makes a new stamp and so a new key; the previous key is never asked for again and expires by its TTL. A change of the gap tolerance (`MarketData:Performance:MaxGapDays`) is a different key too. There is no deletion and so no race with the ingestion transaction: the stamp is read before the report is computed, so a report is never stored under a stamp newer than its data (the worst case is newer data under an older stamp, which is replaced at the next read).
- **What is stored** is text: dates ISO, closes as invariant decimals. The changes are not stored: they are rebuilt from the baseline and the newest close with the same function the calculator uses (`RelativeChange.Between`), so the cache cannot hold a number the calculator would not produce. An entry that is not exactly the stored format (a version, eight windows in order, valid dates and closes) is a miss.
- **A listing with no stamp** (never loaded) or with no price is not stored.
- **Redis is never authoritative.** A Redis failure, a timeout or an unreadable entry is a miss: the numbers come from Postgres, one warning is logged (events 2801, 2802) and the metric `sakerwatch_marketdata_perf_cache_total{result=error}` counts it. The request never fails because of the cache.
- **Settings** (`MarketData:Cache`): `Enabled` (default true) and `Ttl` (default 26 hours: a day plus a margin, so the last key of a listing outlives the next load). Off, every read goes to the database as before.
- **The calculator does not read the windows.** It needs one close, so `IMarketSnapshots.PricesAsync` gives the price and its status (catalog, newest bar and ingestion state) without the performance reader. The status is decided by the same code as the snapshots (a test compares them for every kind of listing), so a calculation and a list cannot disagree about a price.
- **The reader** is a decorator (`CachedPerformanceReader`) over the database reader behind the same `IPerformanceReader`, so the watchlist, the portfolios and the detail all get it and none of them changed.

## Consequences
- A repeated read of the same listings costs one statement for the stamps, one Redis `MGET` and the unchanged statements for the price status, instead of the nine probes per listing.
- A successful load that wrote nothing new still renews the stamp and costs one recomputation per listing; that is the price of a signal that cannot miss a write.
- Prices edited by hand in `price_daily` do not change the stamp. The runbook says what to do (delete the `v1:perf:*` keys, or bump `RedisKeys.Version`).
- A change to the exchange calendars (they decide the previous trading day of the one-day window) is not in the key; it takes effect when the listing next loads or its keys expire. Calendars change with a release, so the release note must say so.
- Memory: about 1 KB per listing and day, a few hundred kilobytes for 500 listings; keys of the previous days expire on their own.
- The runbook `docs/runbooks/cache.md` says how to read, empty and turn off the cache. Redis holds derived, public market data: no user id, no portfolio, no watchlist content. A user's list is assembled from it on each request, so isolation is as before (ADR-0031). No new trust boundary.
- The numbers of the cache and of the database are the same by construction and by a test over a real database and a real Redis (a second read computes nothing, a load in between does).
