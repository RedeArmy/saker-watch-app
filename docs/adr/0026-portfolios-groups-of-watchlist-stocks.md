# ADR-0026: Portfolios as named groups of watchlist stocks

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-16
- **Deciders:** Eder

## Context
The watchlist will hold many stocks (SLO: 500 items). The owner wants to keep it ordered by theme without losing the product's purpose, observability of real market data: for example a portfolio "Petróleo" with ten stocks and "Imanes" with five. The word *portfolio* in finance suggests holdings; here it is only a grouping for observation. No quantities, no cost basis, no trading (the simulated holdings of the master prompt stay a separate, later option).

## Options considered
1. A single `portfolio_id` on each watchlist item (one portfolio per stock). Simple, but a stock that fits two themes has to be duplicated, and notes and target price diverge.
2. Many-to-many between watchlist items and portfolios, with a stock allowed in none. A stock lives once in the watchlist.
3. Tags instead of portfolios. Flexible, but there is no place for a name, a limit or aggregate metrics.

## Decision
Option 2. Module `Watchlist`, schema `watchlist`, RLS by `user_id` like every user-owned table (the worker needs its own policy with the same expression, see ADR-0022).

- `watchlist.portfolios` (`id`, `user_id`, `name`, `description` optional, `position`, timestamps). The name is trimmed automatically: leading and trailing whitespace is removed before validation and storage (the domain value object does it, so no path can store an untrimmed name), and the stored name never has spaces at its ends. A name that is empty after trimming is rejected. Unique `(user_id, lower(name))` on the stored name: names are unique per user ignoring case.
- `watchlist.portfolio_items` (`portfolio_id`, `watchlist_item_id`, `user_id`, `position`, `added_at`). Primary key `(portfolio_id, watchlist_item_id)`; a stock may be in several portfolios or in none (the "all" view).
- **Limits** (configuration, defaults 20 portfolios per user and 100 stocks per portfolio), enforced in the use case and by a database check inside the same transaction so parallel requests cannot exceed them.
- **Deleting a portfolio** removes from the watchlist the stocks that would be left in no portfolio by that deletion; a stock that is also in another portfolio stays, and stocks that were already in none are untouched. The user can add any of them again later, to a new portfolio if wanted. The API first lists the stocks that would be removed and requires an explicit confirmation (a `confirm` flag on the delete), so nothing disappears by surprise. The whole operation is one transaction and idempotent (deleting twice is a no-op).
- **Aggregate metrics per portfolio** (read-side, computed from the same price data as the watchlist, never from the LLM): average and median change over 1D, 1W, 1M, 3M, 6M, YTD, 1Y and 5Y with the count of stocks that have data for each window, best and worst stock per window, and the count of stocks per signal (`Interesting`, `Neutral`, `Avoid`). Averages are equal-weighted (no quantities exist), use `decimal`, and report staleness the way the watchlist does (`stale_since`). Signals remain research aids, never advice.
- **API** (Phase 2): CRUD of portfolios, `PUT /portfolios/{id}/items/{ticker}` (natural idempotency), reorder, and filtering the watchlist by portfolio. Mutations that cannot be natural take an `Idempotency-Key`.

## Consequences
- The data model, API and tests (RLS, limits under concurrency, delete semantics, metrics with missing data) are part of the Phase 2 plan, together with the watchlist; nothing is implemented before the Phase 1 gate.
- Equal-weighted averages hide size differences; positions would need the later simulated holdings and a superseding ADR.
- Delete-with-removal is destructive for notes and target prices of the removed stocks; the confirmation step and the listed stocks are the guard.
- Update `docs/data-model.md`, the API contract and the threat model (new user-owned tables) when implemented.
