# ADR-0050: The sparklines get no cache of their own before the screens

- **Status:** accepted
- **Date:** 2026-10-23
- **Deciders:** Eder

## Context
`GET /v1/watchlist/sparklines` computes the closes of every stock of the caller on each read (ADR-0038), while the windows of the list are cached (ADR-0041). The Phase 2 baseline had it as the slowest path (240 ms p95 alone with 500 stocks), and the gate report left a question for Phase 3: does a screen that asks for the first page of the list and the sparklines together need a cache of the sparklines first?

The baseline measured each endpoint alone, which is not what the screen does. The watchlist screen opens with two requests at once, the first page (50 rows) and the sparklines of all 500 stocks, because the sparklines endpoint takes no page: it answers for the whole watchlist or one portfolio. What the user waits for is the slower of the two, and the two compete for the same database and CPU.

## Options considered
- **A cache of the sparklines in Redis,** keyed like the windows by the stamp of each listing and warmed by the same job. Reads would become one multi-get. It adds a second cache with its own key schema, its own warming and its own failure modes (T51), for a path that is inside the budget.
- **Page the sparklines** (`limit`/`offset`, or the tickers of the visible rows). It cuts the work by ten for a 50-row page, but the shape of the screen (pages, infinite scroll, a portfolio view) is a Phase 3 decision, and a parameter added now would be a guess. Adding it later is not a breaking change.
- **Embed the sparklines in the list.** One round trip, but it changes a response that clients will already read.
- **Do nothing now and measure the real access pattern:** add the screen's pair of requests to the baseline, judge it against the budget of ADR-0033, and say what would reopen the question.

## Decision
Do nothing to the endpoints and add no cache. The baseline has a new scenario, `watchlist_page` (`tests/load/baseline.js`): the first page and the sparklines of 500 stocks sent at once, judged on the time of the pair against the same 300 ms budget of ADR-0033. Two runs of 2026-10-23 with enforcement on (4 virtual users, 3 CPUs shared with k6 and the databases, 25 s of steady state each) passed: **p95 234 ms and 243 ms with the performance cache on** (median 148 and 149 ms, p99 293 and 299 ms, about 25 pages a second, no failures), against 201 to 224 ms for the sparklines alone and 91 to 104 ms for the list alone. With the performance cache off the pair is at 384 to 568 ms across three runs, over the budget, which is why the cache that ships is the one that is judged. A third run crossed a budget on its first attempt (which one was not recorded: the repeat of the run overwrote its log and then failed on a portfolio name, both fixed in `run-k6.sh` and `baseline.js`); that is the spike the enforcement repeats once, and it is the reason the margin below is stated as thin.

The question is reopened, and the answer is then a cache keyed by stamp or a paged request, in this order of preference, when one of these holds:
- the pair is over the budget in a Phase 3 run against the real BFF, or the p95 of `watchlist_page` in a run of `scripts/run-k6.sh` crosses 300 ms;
- a user follows well over 500 stocks (the ceiling is 500, so only a change of the ceiling);
- the screens ask for the sparklines of more than one portfolio at a time.

## Consequences
- The page is inside the budget with 57 to 66 ms to spare at p95; the margin is thin on a machine like this one and says nothing about production, which is why the scenario stays in the baseline and the enforced run fails if it crosses twice in a row.
- A spike can cross the budget once: one request took 1.5 s in the first run (the maximum of the scenario) and 3.2 s elsewhere in the second, without moving the p99, and one whole attempt crossed. If two attempts in a row cross, or the p95 of `watchlist_page` is above 300 ms in the repeat, the conditions above hold and the question is reopened.
- The cost the screen pays is real work that it throws away: it asks for 500 lines and shows 50. If Phase 3 chooses pages, the paged request (an optional list of tickers, additive) is the first thing to build, before any cache.
- The report of ADR-0038 is unchanged: the sparklines are still computed on read from the stored series.
