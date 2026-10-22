# ADR-0044: Hardening of automatic delisting, of the registration write and of the provider retries

- **Status:** accepted
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
The security audit of Phase 2 left three low findings that change how three paths behave. They share a decision record because each refines an accepted ADR (0039, 0032 and 0027) without reversing it.

- **L1, automatic delisting.** Ingestion delists a stock when the provider has not known its symbol on five different days in a row (ADR-0039). "Not known" was decided by the provider's HTTP status or by looking for the words "missing or invalid" or "not found" in its message. A provider that changed its wording, or a request of ours that was wrong, could turn into "unknown symbol" for every stock at once, and five days later delist a whole market.
- **L4, the registration write.** Putting an unknown stock on the watchlist asked a provider inside the request's unit of work, so the API held a database connection and an open transaction for as long as the call took (up to the client's 30 s timeout, plus retries). At 10 registrations a minute per user a few users, or a provider that hangs, could empty the connection pool of the whole API.
- **L5, retries and credits.** The Twelve Data client retried up to twice on a timeout or a 5xx. The budget is reserved once per logical call, before the first attempt, while the provider charges a credit per request, so the shared count could fall behind the real use (ADR-0043 noted it as open).

## Options considered
- **L1: keep matching text, or accept only the documented 404.** Matching text is what failed the design test; only the 404 is documented and recorded in the live runs (ADR-0027). A market-wide check is added because no per-answer rule can tell a provider fault from stocks that left.
- **L4: shorten the call, or move the call out of the transaction.** A shorter timeout still holds a connection for seconds. The call needs no transaction at all, so it is taken out: the pipeline stops wrapping this one command, and the handler opens short units of work only for what it reads and writes.
- **L5: re-budget each attempt, or do not retry.** Re-budgeting per attempt needs a handler inside the retry strategy that knows each request's price and lane; simple to get wrong. Not retrying is a one-line policy, and what retrying would fix is already retried by the job queue, which reserves again on every attempt (and a search that fails is the user's to try again).

## Decision
- **Unknown symbol is the documented 404 and nothing else** (`TwelveDataProvider`). A 4xx whose message happens to contain the words is a permanent failure of that request, not an unknown symbol, and does not count towards delisting.
- **Delisting is judged against the whole market.** When a listing reaches the number of days in a row, ingestion first asks how many of the active listings of its market are in a streak that is still going (the last unknown day is the day or the day before). If at least `MarketWideNotFoundShare` of them (default 30 %) are, and the market has at least `MarketWideMinListings` active listings (default 10), the streaks are taken as a fault of the provider: nothing is delisted automatically, the outcome `provider_fault` is counted and event 2608 is logged. The streaks keep their count; the day the provider recovers every success clears them. A market that really closed is delisted by the operator command. Below the minimum size a market is judged one listing at a time.
- **A command can opt out of the pipeline's unit of work.** `ISelfTransactingCommand<T>` is a state-changing request the pipeline does not wrap; its handler opens the units of work itself. Only `PutWatchlistItem` uses it. The stock is found or registered first, in short units of work of their own (`IStockCatalog` is decorated so each read is a short unit that joins an open one; `StockRegistration` writes the stock and its first load in one), and the item is written in one that opens after. The price is that the registration and the item are two commits: a registration that succeeds and an item that then fails leaves a stock in the shared catalog that nobody follows, which the catalog already accepts (it is shared and refreshed anyway, ADR-0043). The commonest such failure, a full watchlist, is refused first: the handler counts the list in a short unit of work and, when it is full, refuses a stock the catalog does not have before any provider is asked or any row is made (a full list can still edit a stock it holds).
- **The Twelve Data client is not retried by the HTTP pipeline** (`MaxRetries = 0`), so each request that costs a credit was reserved. Timeouts and the circuit breaker stay.

## Consequences
- A change in the provider's wording, or a wrong request, can no longer delist stocks. A real, large loss of stocks at once (a market closing) is not delisted automatically either, by design: it needs the operator, who sees the warning.
- The API holds a connection for the milliseconds of a read or a write and not for a provider call; a test asks the database from outside, during the call, for sessions idle in a transaction.
- An interactive search that fails with a timeout or a 5xx is a `503` the user retries, where the pipeline used to retry silently. Searches cost no credit but do spend the shared search budget, which now counts every attempt.
- Request telemetry and logging still wrap the command; only the transaction does not. A future command that needs this must say so with the marker and give the same reason.
