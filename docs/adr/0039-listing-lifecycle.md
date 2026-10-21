# ADR-0039: The life of a listing: delisting, relisting and a change of ticker

- **Status:** proposed
- **Date:** 2026-10-20
- **Deciders:** Eder

## Context
The catalog is shared and a stock in it is a listing identified by `(market, ticker)` (ADR-0018). Real listings stop trading, come back, or change their ticker. Until now the catalog had an `is_active` flag nothing set, and a provider that no longer knew a symbol failed the listing for good on the first day, which the watchlist showed as `unavailable`, the same as a stock that never loaded. A change of ticker was worse: the new ticker would be registered as a second stock with no history, and every watchlist and link on the old one would stay behind. The spec (section 2.4) asks for symbol changes and delistings to be handled.

## Options considered
- **Mark nothing; leave it to the provider's failures.** What there was; wrong for a stock that stopped trading and no help for a rename.
- **Only by hand, or only automatically.** By hand alone depends on someone noticing; automatic alone cannot tell a rename from a delisting and has no way to undo a wrong call.
- **An explicit state, an alias of the old ticker, an automatic rule and an operator's command.** Chosen by the owner.
- **A new stock row for the new ticker, linked to the old.** Watchlists and prices point at the stock id, so a rename that keeps the id needs no copy and no migration of user data.

## Decision
- **A delisted stock has a state of its own.** `stocks.delisted_at` (set means delisted; a check ties it to `is_active = false`). Ingestion lists only active stocks, so a delisted one is never asked for again, and its history is kept. `corporate_actions` records the `delisting` (and the `symbol_change`) with its effective day and its source (`operator` or `ingestion`).
- **The watchlist, the series and the portfolio metrics say `delisted`.** It is a new price status (`delisted`, with `delistedAt`), ahead of the others: the last known price is shown and does not go stale, nothing new is loaded, and the portfolio metrics count the stock apart and keep the numbers it had.
- **A change of ticker keeps the stock.** The stock's `ticker` changes and the old one goes to `ticker_history (market, ticker, stock_id, changed_on)`. A lookup by a ticker finds a stock that uses it first; only when none does is the history consulted, so a company that later takes a retired ticker is the stock of that ticker. The provider's symbol (`symbol_aliases`) moves to the new ticker, or to the symbol the operator gives. Watchlists, notes, targets, prices and portfolios are untouched: they point at the stock.
- **Ingestion delists on its own, slowly.** When the provider does not know the symbol of a listing that had prices, the day is counted once (`ingestion_state.not_found_days`, `last_not_found_on`); the listing is not failed. When the count reaches `MarketData:Ingestion:DelistAfterNotFoundDays` (default 5, 2 to 60) the stock is delisted. Any success starts the count again. A listing that never loaded (a backfill that does not find the symbol) still fails at once, as before.
- **The operator decides what the system cannot.** `SakerWatch.Hosts.Worker marketdata delist|relist|rename ...` runs one change in one unit of work and exits without starting the worker; the outcome is in the log and the exit code is 0 (done), 1 (refused), 2 (not understood) or 3 (failed, nothing changed). A ticker or a provider symbol another stock took is a refusal, never an exception. There is no admin API before Phase 6. `relist` brings a stock back and makes ingestion try it from scratch; a wrong automatic delisting is undone with it.
- **Privileges.** `api_role` reads `ticker_history` and the new columns and writes none; `worker_role` writes them. Nothing here is user-owned, so there is no row-level security.

## Consequences
- A stock a provider stops serving is not shown as a failure to load but as what it is, after a week of evidence rather than on one bad answer.
- The provider cannot delist stocks by lying for a day: it takes the configured number of different days in a row, each leaving a log and a corporate action, and `relist` reverses it.
- A rename the provider also made needs the operator, because nothing in the free plan tells a rename from a delisting; until the operator renames, the stock is counted as not known and may be delisted, and `relist` after the `rename` fixes it.
- A watchlist or a link on a retired ticker keeps working for as long as the history keeps the row.
