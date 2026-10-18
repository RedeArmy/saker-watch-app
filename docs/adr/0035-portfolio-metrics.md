# ADR-0035: Portfolio metrics: what a group of stocks did

- **Status:** proposed
- **Date:** 2026-10-18
- **Deciders:** Eder

## Context
ADR-0026 promised aggregate numbers per portfolio. A portfolio holds no quantities (ADR-0031), so there is no money to add up: the only honest numbers are about how the stocks moved. The watchlist already gives each stock eight performance windows with an explicit reason when one is missing (ADR-0030, ADR-0033). The question is how to combine them without inventing data.

## Decision
- **Route:** `GET /v1/portfolios/{id}/metrics`, scoped to the caller (another user's portfolio is a `404`), with an ETag. `GET /v1/watchlist` gains `portfolio={id}` to list only the members of one portfolio; an unknown or foreign id is a `404`, a malformed one a `400`.
- **Equal weight.** For each window the average is the plain mean of the changes of the stocks that have one. Without quantities there is no weight to apply, and the response says so; it is how the stocks moved, not how a sum of money did.
- **Median** is the middle change, or the mean of the two middle ones when the count is even. It is given next to the average because one outlier moves the average and not the median.
- **A stock without a number for a window is left out of that window, never counted as zero.** Each window reports `count`, the number of stocks that contributed; with none, average, median, best and worst are `null`. Pending, unavailable and too-young stocks therefore never drag a figure towards zero.
- **Best and worst** name the stock with the highest and lowest change; a tie goes to the lowest `MARKET:TICKER` label so the answer is stable.
- **Arithmetic is `decimal`** end to end and travels as strings (ADR-0033); the figures are not rounded beyond what the windows already carry.
- **Staleness is shown, not hidden.** The response counts the stocks by price state (`current`, `stale`, `pending`, `unavailable`) and gives `staleSince`, the oldest moment any stale price went stale (`null` when none is). A stale stock still contributes its last known windows, as it does in the watchlist.
- **No signal in the metrics.** Signals do not exist before Phase 4; when they do, a count per signal can be added without breaking the response. Nothing here is advice.

## Consequences
- The numbers are computed on read from the same snapshots as the watchlist, so the two views agree by construction (a test compares them). The cost is the one noted in ADR-0033; caching arrives with 2.8.
- An empty portfolio answers `200` with zero counts, which a client can render without a special case.
- A window whose `count` is low is a weak statement; clients should show the count beside the figure.
