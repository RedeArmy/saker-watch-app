# ADR-0032: Finding and registering stocks on demand

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-18
- **Deciders:** Eder

## Context
A user adds a stock to the watchlist by market and ticker (milestone 2.6). The shared catalog `marketdata.stocks` is written only by the worker and read by the API, so a stock nobody has tracked yet is not there. Registering it needs a valid MIC, ticker, name, currency, time zone and asset type, the provider's own name for the listing (an alias, because Twelve Data calls Apple `AAPL` on `XNGS` and the catalog keeps `XNAS`), and a pending ingestion state so the worker loads its history. The data-model note said a security-definer function would do this with the watchlist.

## Options considered
1. **The API asks the provider and registers the stock at once.** Immediate answer to the user (a wrong ticker is a 404 now), one code path, nothing in limbo. The API host holds the provider key and makes outbound calls.
2. **The API accepts only stocks already in the catalog**; someone else fills it. Leaves the question of who fills it and cannot serve "any stock".
3. **The API accepts the request and a job resolves it later.** Most resilient to outages, but the watchlist then holds items in a pending state that can fail later, with states, notices and a retry story to design.

## Decision
Option 1, narrowed so the new power is small:
- **`IStockDirectory`** (`MarketData.Contracts`) is the only door other modules use: `SearchAsync` (candidates), `FindAsync` (catalog only, never the provider) and `EnsureAsync` (the catalog stock, registering it first when absent).
- **Nothing a user types reaches the catalog.** `EnsureAsync` takes a market and a ticker, asks the provider, and registers a stock only from the listing the provider returned for that exact canonical market and ticker. The domain (`Stock.Create`) validates every part; the time zone is the one of our own calendar of the market (ADR-0027) and the currency must be the one the market trades in (`MarketCatalogRules`: USD for the US markets), so a cross-listing or a depositary receipt of the same ticker is never registered as this one. Provider segment codes map to the canonical market (`XNGS`, `XNMS`, `XNCM` to `XNAS`).
- **The function is the only way into the catalog for the API.** `marketdata.register_stock` (security definer, fixed `search_path`, `EXECUTE` to `api_role` alone) inserts the stock, the alias and a pending ingestion state, and returns the id of whichever row holds `(market_mic, ticker)`. It never updates an existing stock, so it cannot rewrite a name, a currency or a time zone, and it is idempotent under parallel calls. The table checks still apply to what it inserts. `api_role` has no `INSERT` on the tables.
- **The first load is queued in the same transaction** (`marketdata.backfill:{id}`, deterministic), so a registered stock gets its history without waiting for the next daily run; the API knows the payload name but runs no MarketData handler (`AddJobPayloads`).
- **The provider is spent carefully.** A search or a registration spends one search of a budget shared by every user and replica in Redis (`MarketData:Lookup`: 30 a minute and 2 000 a day by default), after a copy of the last answer (kept 10 minutes under a key made from a hash, never the text) has been tried. The search endpoint is also under the per-user "expensive" limit. The query is plain text only (letters, digits, spaces, `.`, `&`, `-`, `_`, up to 40), so it cannot carry URL syntax to the provider.
- **Failures are told apart without leaking the provider:** a spent budget or a provider rate limit is `429` with `Retry-After`; any other provider failure is `503` with `Retry-After`; its message never reaches the client. Two error categories, `TooManyRequests` and `Unavailable`, were added to the Kernel for this.
- **The watchlist is a `PUT`** on `/v1/watchlist/items/{market}/{ticker}` (201 when added, 200 when updated; the same request twice is the same state, so no idempotency key) and a `DELETE`, which never calls the provider and never registers anything.

## Consequences
- The API host now needs the provider settings (`MarketData:Providers:Markets`, `TwelveData:ApiKey`) and Redis, as the worker does; the key stays in the environment or a secret, never in a URL or a log. Searches use no credit, and the lookup never fetches prices.
- A provider outage blocks adding a stock the catalog does not have yet (503, nothing stored), not editing or removing one, nor adding one already catalogued.
- A registration the user's watchlist then refuses (a full watchlist) rolls back with its transaction, so a refused add leaves no catalog row.
- Option 3 stays open for later, for more users or markets, behind the same contract.
