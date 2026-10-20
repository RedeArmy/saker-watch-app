# Runbook — listing lifecycle

- **Purpose:** change the life of a listing in the shared catalog: it stopped trading, it trades again, or it has a new ticker (ADR-0039).
- **Owner:** Eder · **Status:** draft · **Target phase:** 2

## When
- The logs show `The stock {Market} {Ticker} was marked delisted by ingestion` (event 2601) and the stock did not really stop trading: relist it.
- A company changed its ticker (the provider stops knowing the old symbol). Rename it before the days in a row reach `MarketData:Ingestion:DelistAfterNotFoundDays` (default 5), or relist it after.
- A stock stopped trading and the provider still answers: delist it by hand.

## How
Run the worker with `marketdata` and the change. It does not start the worker; it needs the same configuration as the worker (the connection strings), and it writes its outcome to the log.

```
dotnet SakerWatch.Hosts.Worker.dll marketdata delist  XNAS ABC [--on 2026-10-01]
dotnet SakerWatch.Hosts.Worker.dll marketdata relist  XNAS ABC
dotnet SakerWatch.Hosts.Worker.dll marketdata rename  XNAS FB META [--on 2022-06-09] [--provider-symbol META]
```

- `--on` is the day the change took effect (today in UTC when omitted).
- `rename` keeps the stock (its history, watchlists and portfolios); the old ticker stays in `marketdata.ticker_history` and keeps finding the stock. The provider symbol becomes the new ticker unless `--provider-symbol` says otherwise. Only provider symbols equal to the old ticker are moved; check `marketdata.symbol_aliases` if the provider uses another format. If another stock already has the provider symbol the rename would give, it is refused (`marketdata.lifecycle.symbol_taken`) and nothing changes; pass the right one with `--provider-symbol`.
- Exit code: 0 done (or already so), 1 refused (unknown stock, ticker taken, another stock already has that provider symbol, the stock no longer has that ticker), 2 not understood, 3 failed (the database was down, something nobody foresaw: nothing was changed, and the log has the exception).

## Check
- `marketdata.stocks`: `is_active`, `delisted_at`, `ticker`.
- `marketdata.corporate_actions`: a `delisting` or `symbol_change` row with its `source` (`operator` or `ingestion`).
- The watchlist shows the stock as `delisted` (with `delistedAt`), or with its new ticker.
- Metric `sakerwatch_marketdata_listing_changes_total{operation,outcome}` and the logs 2601 to 2607.

## Undo
- A delisting: `relist`. Ingestion tries the stock from scratch (the failure and the count of unknown days are cleared).
- A rename: `rename` the other way; the ticker in use leaves the history.
