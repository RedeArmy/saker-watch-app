# ADR-0031: Watchlist and portfolios: storage, isolation and limits

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-18
- **Deciders:** Eder

## Context
ADR-0026 (accepted) defines portfolios as named groups of watchlist stocks, with unique names per user, limits of 20 portfolios and 100 stocks each, and a delete that removes the stocks left in no portfolio. ADR-0010 (accepted) requires row-level security on every user-owned table and lets the worker reach user data only through narrow security-definer functions, naming `watchlist.tracked_stock_ids()`. The milestone 2.6 plan needs the storage settled before the use cases and the API are written on top of it.

## Options considered
- **A foreign key from `watchlist_items.stock_id` to `marketdata.stocks`, or only the id.** The key would guarantee the stock exists, but it couples two module schemas in the database (a migration of one could block the other, and the watchlist role would need to read the catalog for the check). The catalog is append and update only: nobody deletes a stock, so a dangling id cannot appear.
- **Enforce the limits only in the use case, or also in the database.** A use case counts and then inserts; two parallel requests both count 99 and both insert. The limits are an abuse control (T38), so the database must hold them.
- **A table of limits that the triggers read, or fixed ceilings in the triggers.** A table is one more thing to keep consistent with the configuration; fixed ceilings are the simplest thing that is still a hard guarantee.

## Decision
- **Tables** in schema `watchlist` (migrations `CreateWatchlist` and `WatchlistSecurity`): `watchlist_items` (unique `(user_id, stock_id)`, optional notes up to 2 000 characters and a target price `numeric(20,8)`), `portfolios` (trimmed name of 1 to 60 characters, unique per user ignoring letter case through an index on `(user_id, lower(name))`, optional description up to 500, `position`) and `portfolio_items` (primary key `(portfolio_id, watchlist_item_id)`, a `position` and `added_at`).
- **The stock is an id with no foreign key** across schemas. The membership table carries `user_id` in both of its foreign keys, which target the unique `(id, user_id)` of the parent, so a portfolio can never hold another user's item even where a policy is bypassed. Deleting a watchlist item or a portfolio removes its memberships by cascade; the stock itself is never removed by deleting a portfolio (that is a use case, in one transaction, after confirmation, per ADR-0026).
- **Isolation:** row-level security is enabled on the three tables with one policy each for `api_role`, comparing `user_id` with `app.current_user_id` for reads and writes. `api_role` has `SELECT`, `INSERT`, `UPDATE` and `DELETE`; `PUBLIC` and `worker_role` have nothing on the tables. A user that is not set sees nothing.
- **Ceilings in the database:** triggers on insert take a transaction-level advisory lock for the owner (or the portfolio), count, and refuse with `check_violation` and a named constraint (`ck_watchlist_items_max_per_user` at 500, `ck_portfolios_max_per_user` at 20, `ck_portfolio_items_max_per_portfolio` at 100). `WatchlistLimits.Ceilings` repeats the numbers in code; the configured limits can be lower, never higher (`IsWithinCeilings`), and raising a ceiling is a migration. The use cases enforce the configured values first and map the named constraint to a friendly error when a race gets past them.
- **The worker's only door** is `watchlist.tracked_stock_ids()`, a security-definer function with a fixed `search_path` (the catalog, the schema and the temporary schema last; `PinWatchlistSearchPaths`, and the guard test now covers the `watchlist` schema) that returns the distinct stock ids across all watchlists, without a user id or a note. `EXECUTE` goes to `worker_role` only.
- **Normalization lives in the domain:** names and descriptions are trimmed, a blank note or description is none, a target price is positive with at most eight decimals (it is refused, not rounded), and `PortfolioName` removes the spaces at both ends so no path stores an untrimmed name; the database repeats the checks.

## Consequences
- A stock id on a watchlist could in theory not exist in the catalog if a path inserted it without registering the stock first; the use case that adds a stock registers it (milestone 2.6b) and an ingestion that finds no catalog row simply skips the id.
- The tests prove the isolation with the application filter out of the way (raw SQL as the API role with another user set), the composite foreign keys with the administrator connection, and the ceilings with parallel transactions that hold their insert open: removing the advisory locks makes those tests fail.
- Portfolio deletion semantics, the on-demand registration of a stock, the list read model and the API are the next parts of the milestone.
