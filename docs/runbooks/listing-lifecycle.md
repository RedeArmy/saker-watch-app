# Runbook — listing lifecycle

- **Purpose:** change the life of a listing in the shared catalog: it stopped trading, it trades again, or it has a new ticker (ADR-0039).
- **Owner:** Eder · **Status:** approved · **Target phase:** 2

## When
- The logs show `The stock {Market} {Ticker} was marked delisted by ingestion` (event 2601) and the stock did not really stop trading: relist it.
- A company changed its ticker (the provider stops knowing the old symbol). Rename it before the days in a row reach `MarketData:Ingestion:DelistAfterNotFoundDays` (default 5), or relist it after.
- A stock stopped trading and the provider still answers: delist it by hand.

- The logs show event 2608 (`... of ... active listings of a market are unknown to the provider at once`) and the metric outcome `provider_fault`: the provider is failing for a large part of the market, so nothing is delisted automatically (ADR-0044). Check the provider first (`provider-outage`); if the stocks really left, delist them with the command below.

## How
Run the worker with `marketdata` and the change. It does not start the worker; it needs the same configuration as the worker (the connection strings), and it writes its outcome to the log.

```
dotnet SakerWatch.Hosts.Worker.dll marketdata delist  XNAS ABC [--on 2026-10-01]
dotnet SakerWatch.Hosts.Worker.dll marketdata relist  XNAS ABC
dotnet SakerWatch.Hosts.Worker.dll marketdata rename  XNAS FB META [--on 2022-06-09] [--provider-symbol META]
```

- `--on` is the day the change took effect (today in UTC when omitted).
- `rename` keeps the stock (its history, watchlists and portfolios); the old ticker stays in `marketdata.ticker_history` and keeps finding the stock. The provider symbol becomes the new ticker unless `--provider-symbol` says otherwise. Only provider symbols equal to the old ticker are moved; check `marketdata.symbol_aliases` if the provider uses another format. If another stock already has the provider symbol the rename would give, it is refused (`marketdata.lifecycle.symbol_taken`) and nothing changes; pass the right one with `--provider-symbol`.
- Exit code: 0 done (or already so), 1 refused (unknown stock, ticker taken, another stock already has that provider symbol, the stock no longer has that ticker), 2 not understood (also a `--provider-symbol` that is not 1 to 32 letters, digits, `.`, `_`, `/` or `-` starting with a letter or a digit), 130 interrupted with Ctrl-C (nothing is half written: the command runs in one unit of work), 3 failed (the database was down, something nobody foresaw: nothing was changed, and the log has the exception).

### The provider already changed the symbol
If the provider already answers to the new symbol and no longer knows the old one, ingestion counts the old ticker as "symbol not found" and delists the stock after `DelistAfterNotFoundDays` days. Do not wait: run `rename` first (the stock keeps its history), and then `relist` if the stock was already delisted by ingestion in the meantime. `relist` clears the failure and the count of unknown days, and ingestion tries the stock again with the new symbol. Order matters: a `relist` before the `rename` would be counted unknown again on the next run.

### Tuning the market-wide check
`MarketData:Ingestion:MarketWideNotFoundShare` (default 0.3) and `MarketData:Ingestion:MarketWideMinListings` (default 10) decide when many unknown symbols at once are taken as a provider fault. A market with fewer active listings than the minimum is judged one listing at a time.

## Check
- `marketdata.stocks`: `is_active`, `delisted_at`, `ticker`.
- `marketdata.corporate_actions`: a `delisting` or `symbol_change` row with its `source` (`operator` or `ingestion`).
- The watchlist shows the stock as `delisted` (with `delistedAt`), or with its new ticker.
- Metric `sakerwatch_marketdata_listing_changes_total{operation,outcome}` and the logs 2601 to 2607.

## Undo
- A delisting: `relist`. Ingestion tries the stock from scratch (the failure and the count of unknown days are cleared).
- A rename: `rename` the other way; the ticker in use leaves the history.

## A listing marked failed
`marketdata.ingestion_state.status = 'failed'` with a `failure_code` leaves the listing out of the daily refresh. Three codes are a stop by the configuration and clear themselves: `market_disabled` (no provider for the market), `no_calendar` and `no_symbol_alias`. Those listings are asked again every day without calling the provider while the cause stands, and the first run after it is fixed marks them `ok`. Any other code (for example `series_truncated`, or a symbol the provider never knew when the first history was loaded) stays until the operator decides: fix the cause, then reset the row so the next refresh queues the load again:

```sql
UPDATE marketdata.ingestion_state SET status = 'pending', failure_code = NULL, not_found_days = 0, last_not_found_on = NULL
WHERE stock_id = (SELECT id FROM marketdata.stocks WHERE market_mic = 'XNAS' AND ticker = 'EXAMPLE');
```

Run it as the admin login, one stock at a time. (A command for this, like `relist`, is not built: a failed listing is rare and always worth a look first.)

## Smoke test
`bash scripts/smoke-worker.sh` (add `SMOKE_NOAVX=1` on a CPU without AVX) builds nothing: run `dotnet build` first. It starts a throwaway compose project `saker-smoke` (other ports, random passwords, removed at the end), applies the migrations, runs the worker for 25 seconds and checks that it starts, runs its jobs and stops gracefully with no warning, and then tries every command above and the rows they leave. It prints `ALL OK`, or one `FAIL` line per check that did not hold. The dev stack is not touched. The worker reaches the Banco de Guatemala for the exchange rate; nobody else is called.

Result of the first run (2026-10-21): all checks passed. The run found that the freshness sampler read through a database session that had no connection (a warning at start-up, and neither the listings nor the age of the exchange rate were ever measured); it now reads inside a unit of work.
