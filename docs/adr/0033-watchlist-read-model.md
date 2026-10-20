# ADR-0033: The watchlist read model: what the list shows and how it is ordered

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-18
- **Deciders:** Eder

## Context
`GET /v1/watchlist` (milestone 2.6c) shows each stock the user follows with its price, how current the price is and its performance windows, sorted, filtered and paged, with an ETag. It joins three owners of data: the user's items (Watchlist), the catalog and the prices (MarketData) and the windows (ADR-0030). The product spec lists sorting by name, price, any change window and signal, a staleness indicator instead of an error, and p95 under 300 ms at 500 items.

## Decision
- **The module boundary is `IMarketSnapshots`** (`MarketData.Contracts`): `FindManyAsync` (catalog details only) and `SnapshotsAsync` (price, status and windows for many stocks at once). Watchlist never reads a market table; MarketData never reads a watchlist table. The snapshot service uses the performance reader (ADR-0030), one statement for the newest raw bar of every stock, one for the failed ingestions and the one staleness rule (`Freshness`, ADR-0029), so the list, the portfolio metrics and the detail cannot disagree.
- **The price shown is the close as traded** of the newest stored bar, in the stock's own currency, with its date and whether the day is final. The windows come from adjusted closes (ADR-0006) and are fractions; no currency conversion enters the list (ADR-0018).
- **`priceStatus` is a fact, not an error:** `current`; `stale` with `staleSince` and the last known price; `pending` (no history yet, the first load is on the way); `unavailable` (no price and loading failed for good). A window without a number carries its reason (`no_data`, `insufficient_history`, `history_gap`, `calendar_unavailable`).
- **Sorting** is by `ticker` (default), `name`, `market`, `createdAt`, `price` or a window (`1d`, `1w`, `1m`, `3m`, `6m`, `ytd`, `1y`, `5y`); a stock without a value for the key is last in either order, and ties break by ticker and market, so the order, and therefore the ETag, is the same every time. There is no sort by signal until the analysis exists (Phase 4); asking for it is a validation error, not a silent fallback. Sorting by price compares numbers in each stock's own currency.
- **Filtering** by market, kind of instrument, text in ticker or name and price status. **Paging** is `limit` (1 to 100, default 50) and `offset` with the total of the matches; a watchlist holds at most 500 stocks (ADR-0031), so a cursor adds nothing.
- **Cost is proportional to what is asked:** a request that sorts or filters on market data reads it for every matching stock (at most 500, a handful of statements); any other request reads it only for the stocks of the page.
- **ETag:** the tag is a hash of the exact bytes of the page and honours `If-None-Match`, so an unchanged list is a `304` with no body. The response is `Cache-Control: private, no-cache` and `Vary: Authorization`, because it belongs to the caller. The list is still built before the comparison; the saving is the transfer, and milestone 2.8 caches the daily numbers per stock and day to cut the work.
- **Decimals travel as strings** across the whole API (docs/api/README.md): prices, targets and changes. A JSON number in a request is refused.
- **A query that reads with raw SQL** carries the `IReadsDatabase` marker, so the pipeline gives it an open connection with the user set for row-level security; a query that does not touch the database (the provider search) holds no connection.

## Consequences
- A stock whose catalog row cannot be found is left out of the list instead of failing it; nothing deletes catalog rows, so it should not occur.
- Sorting by performance reads 500 stocks' windows (about 0.3 s uncached, ADR-0030) until the cache of 2.8 exists.
- The list says nothing about signals or advice; every analysis screen carries its own notice (spec §7.5).
