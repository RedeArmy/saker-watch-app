# Phase 2 load baseline

- **Purpose:** the first measurement of the Phase 2 read paths with k6, with the performance cache (ADR-0041) on and off, so later changes have a figure to be compared with. It is a baseline of one machine, not a promise about production.
- **Owner:** Eder · **Status:** draft · **Target phase:** 2
- **How to repeat it:** `LOAD_NOAVX=1 bash scripts/run-k6.sh` (leave `LOAD_NOAVX` out on a CPU with AVX). It needs docker, python3, openssl and a built solution, and it removes everything it creates.

## What was measured
| Item | Value |
|---|---|
| Data | 500 listings (XNAS `T0001` to `T0500`), six years of weekday prices each (783 000 rows in `price_daily`), every listing loaded successfully just now, one USD to GTQ rate |
| Users | five, each following all 500 listings, each with one portfolio of 100 |
| Stack | PostgreSQL 16 + TimescaleDB, MongoDB, Redis (compose project `saker-load`), `Host.Api` in Release and `Production` mode, all on the same machine as k6 |
| Machine | Intel Core i7-8850H, 3 CPUs available to the run, 9 GB RAM, Linux; nothing else running during the runs |
| Tool | k6 1.3.0 (`grafana/k6` pinned by digest), `tests/load/baseline.js` |
| Shape | five scenarios one after another, each with 4 virtual users for 30 seconds in a closed loop (a user sends the next request when the answer arrives); the numbers of one scenario are that endpoint alone on the machine |
| Identity | a stand-in provider (`scripts/load/idp.py`: static discovery and JWKS plus RS256 tokens signed with a key made for the run); the API validates the tokens as it would a real provider's |
| Rate limits | raised to their maximum for the run, so the limiter is not what is measured |

The scenarios: the watchlist sorted by ticker (the default view, 50 per page), the watchlist sorted by the one-year change (needs the numbers of all 500 stocks, the costly case of ADR-0033), the sparklines of the 500 stocks (30 days), the metrics of a portfolio of 100, and the partial-purchase calculator for a random stock of the catalog.

## Result (2026-10-21)
Latencies in milliseconds; every request answered 200 (0.00 % errors in all ten runs).

### Performance cache on (default)
| endpoint | avg | median | p95 | p99 | max | requests/s |
|---|---|---|---|---|---|---|
| watchlist by ticker | 73.2 | 68.9 | 114.3 | 158.4 | 375.9 | 54.3 |
| watchlist by performance | 141.2 | 133.2 | 224.9 | 285.3 | 335.0 | 28.4 |
| sparklines | 143.9 | 138.0 | 242.2 | 296.4 | 407.7 | 27.7 |
| portfolio metrics | 69.0 | 63.6 | 116.1 | 165.6 | 224.0 | 57.6 |
| calculator | 43.7 | 40.7 | 73.8 | 96.8 | 147.6 | 90.4 |

### Performance cache off (`MarketData:Cache:Enabled=false`)
| endpoint | avg | median | p95 | p99 | max | requests/s |
|---|---|---|---|---|---|---|
| watchlist by ticker | 143.6 | 138.1 | 223.0 | 276.1 | 341.2 | 27.8 |
| watchlist by performance | 581.7 | 571.8 | 848.8 | 956.6 | 1001.4 | 6.9 |
| sparklines | 145.6 | 138.1 | 239.9 | 295.8 | 390.6 | 27.4 |
| portfolio metrics | 165.9 | 160.8 | 253.8 | 325.7 | 419.0 | 24.1 |
| calculator | 49.0 | 43.2 | 82.8 | 133.4 | 690.1 | 80.7 |

## Reading it
- **The budget of ADR-0033 (300 ms for the whole watchlist) holds with the cache on:** the slowest p95 of the five endpoints is 242 ms (the sparklines). With it off, the watchlist sorted by a performance window is at 849 ms p95, almost three times the budget, which is what ADR-0030 predicted (0.3 to 0.5 s for 500 listings alone, more under four concurrent users on three CPUs).
- **What the cache bought:** the costly sort went from 582 ms to 141 ms on average (4.1 times), the default view halved and the portfolio metrics fell by more than half, and the throughput of the watchlist sorted by performance went from 6.9 to 28.4 requests per second.
- **The sparklines do not use the cache** (ADR-0038 computes them from the stored series on read) and sit at 240 ms p95 in both runs. They are inside the budget on their own, but they are the slowest path now, and a watchlist page that asks for the list and the sparklines together would spend both. If the screen needs them in the same budget, caching the series per listing under the same stamp is the next step; it is not done here.
- **The calculator** did not change with the cache because it no longer reads the windows (milestone 2.8b); its half-second maximum with the cache off is a single slow request in a run of 2 400.
- Tail latencies (p99 up to 285 ms with the cache on) leave little room: the figures are for 4 users on 3 CPUs shared with k6, the API and the three databases. A deployed instance has other limits.

## What this does not say
- It says nothing about production hardware or the network, nor about many users at once: 4 virtual users in a closed loop measure latency under light concurrency, not capacity. An arrival-rate test is the next measurement if capacity matters.
- The five users share the same 500 listings, which is the best case for the cache (one computation serves all of them). Users with different stocks would warm it more slowly, but each listing is still computed once a day.
- The data is synthetic: smooth series with no holes, splits or stale listings.
- The runs are one sample each; the trial runs before this one gave the same shape (cache on 165 ms p95 and off 904 ms for the costly sort with 15-second phases). Repeat it before relying on a difference of a few milliseconds.
