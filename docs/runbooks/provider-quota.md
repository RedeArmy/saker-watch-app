# Runbook — provider credit lanes and the registration cap

- **Purpose:** what to do when new stocks stay `pending`, when users hit the daily limit of new stocks, or when the daily refresh runs out of credits (ADR-0043).
- **Owner:** Eder · **Status:** approved · **Target phase:** 2

## How the day is shared (Twelve Data free plan: 800 credits a day, 8 a minute)
| Work | Lane | What limits it |
|---|---|---|
| the daily refresh of the stocks in the catalog | scheduled | the plan's budget only (key `twelvedata`) |
| the history load of a stock somebody added, and the reload after a rewritten history | on demand | an allowance of its own first, `TwelveData:OnDemandCreditsPerDay` (default 200, key `twelvedata_ondemand`), then the plan's budget |
| adding a stock the catalog does not have | per user | `MarketData:Lookup:RegistrationsPerUserPerDay` (default 20 a UTC day, key `register_{user}`) |

A history costs two credits (adjusted and raw), so 200 credits are 100 new histories a day, and at least 600 credits are left for the refresh. The two are reserved together before the first call: a load that finds room for one credit and not for two waits whole, instead of spending one and losing it (ADR-0049). Because of that, set `TwelveData__CreditsPerMinute` and `TwelveData__CreditsPerDay` to at least 2 and `TwelveData__OnDemandCreditsPerDay` to 0 or at least 2 (the host refuses to start otherwise); an odd value is allowed and leaves one credit of that minute or day unused by loads, so prefer even numbers (8 a minute is four histories a minute). A series the provider cuts at its row limit fails the load with `marketdata.provider.series_truncated` and is recorded against the listing.

## How many stocks the plan carries
A refresh of a listing costs two credits a day, so the catalog the plan keeps fresh is **(`TwelveData__CreditsPerDay` − `TwelveData__OnDemandCreditsPerDay`) / 2**: 300 on the free plan (800 and 200), and the shared catalog is capped below it, at `MarketData__Lookup__MaxCatalogListings` (250 by default, ADR-0051), to leave room for the reloads of rewritten histories. A new stock past the cap is refused with `409 marketdata.stock.catalog_full` and event 2504; a stock already in the catalog still goes on a watchlist. Change the plan and the cap together (a host that routes a market to Twelve Data refuses to start when twice the cap is more than the credits left for the refresh, and says which cap fits): a paid plan with no daily limit sets `CreditsPerDay` large and the cap to the number of stocks wanted. If prices of the last stocks of the day go stale and no refusals appear, the cap is too high for the credits left after the reloads.

## Signals
| Signal | Meaning |
|---|---|
| `sakerwatch_marketdata_stock_lookups_total{operation="ensure",outcome="limit"}` | users are being told they reached the daily limit of new stocks |
| `sakerwatch_marketdata_backfills_total{outcome="waiting"}` increases and new stocks stay `pending` | the on-demand allowance of the day is spent; the loads wait for the next UTC day |
| `sakerwatch_provider_calls_total{outcome="ratelimited"}` while the scheduled refresh runs | the plan's day is spent; check the other counters before blaming the allowance |
| `sakerwatch_marketdata_refreshes_total{outcome="reload_queued"}` (a day's increase) | stocks whose adjusted history the provider rewrote today (a dividend, a split, or a correction); each costs two credits on the on-demand lane, and the stock gets no new prices until its reload completes |
| log event 2003 | Redis could not be reached: the budgets fail open and the provider enforces its own limit |

## Reloads of rewritten histories
When the daily refresh finds the adjusted history of a stock rewritten (`RewriteTolerance`, a relative change of 0.0001 % in any stored close), it writes nothing for that stock and queues a full reload, which draws on the on-demand allowance like the history of a new stock (ADR-0043). While the reload waits the stock is not updated and shows as stale. Expected volume at 300 stocks is about four reloads a day (eight credits), with days of 20 to 30 around ex-dividend dates (40 to 60 credits), well inside the 200 of the allowance.

The lane is on purpose a bulkhead. If the provider readjusts a whole market, 300 reloads cost 600 credits: on the on-demand lane they take three days and the refresh of every other stock goes on; on the scheduled lane they would use the plan's whole day and stop the refresh. What to expect and do in that case: the stocks affected stay stale for up to three days, new stocks wait behind them (`backfills_total{outcome="waiting"}`), and nothing needs doing unless it lasts longer; do not raise `OnDemandCreditsPerDay` to hurry it (it takes the refresh's credits).

**Reopen the decision** (give reloads a lane of their own with a cap, never the scheduled lane) when one of these holds: `waiting` stays up on several days in a row with no mass rewrite behind it; the service has more than one real user (Phase 6), because each user can add 20 stocks a day; or `reload_queued` is above about 50 on a normal day.

**Calibrate in the first weeks** with real data. Keep the daily increase of `reload_queued` (`sum(increase(sakerwatch_marketdata_refreshes_total{outcome="reload_queued"}[1d]))`) next to the number of stocks, for at least four weeks, and compare the days with the ex-dividend dates of the stocks followed. A rate far above what dividends and splits explain says `RewriteTolerance` (0.000001) is reading provider noise as a rewrite: raise it with a test of the rounding the provider uses, and do not rescale the stored history instead of reloading it (the numbers would be derived and not the provider's, ADR-0030).

**First validation (2026-10-23, offline, on the responses recorded from the real API in `tests/MarketData.Tests/Fixtures/twelvedata`).** The provider gives closes with up to five or six decimals (trailing zeros are dropped, so rows show 0, 3, 5 or 6). The refresh compares only the last `RefreshOverlapDays` (10) days, which are recent prices. Half a unit of the last place is above the tolerance (1e-6 of the price) only for prices under about $5 at five decimals and under about $0.50 at six, so for the stocks of the catalog rounding alone cannot read as a rewrite; a stock under $5 could, if the provider re-rounds a series it did not change. The smallest real event is far above the tolerance: a one-cent dividend on a $500 stock rescales the earlier closes by 2e-5 (20 times the tolerance), and on a $20 stock by 5e-4 (500 times). So the tolerance sits between the noise and the smallest event for prices above $5. **Live result (2026-10-23, 15:56 UTC, during the US session, `scripts/run-live-rewrite-stability.sh`, 12 credits).** Six symbols (AAPL, KO, F, SPY, SIRI, NOK; last closes 348.22, 95.34, 11.94, 763.96, 27.54 and 7.88), 15 completed sessions each, fetched twice 10 minutes apart: **no close moved at all** (largest relative change 0, against a tolerance of 1e-6). So within minutes the provider's adjusted history is stable, and `reload_queued` will not be fed by jitter between consecutive calls. **Still not validated:** (1) a stock under $5, since the lowest of the six was $7.88, where the rounding argument above says the tolerance is tightest (the default set now adds RIG and BB, which have traded under $5, and the report has a Coverage section that says how many symbols closed under $5 and states plainly when none did; if none did, name some with `SAKER_LIVE_REWRITE_SYMBOLS`); (2) a gap of a day or more (the run left `rewrite-stability-snapshot.json` in the output directory; running the script again on another day compares with it), because a provider can recompute overnight; (3) the real rate of `reload_queued`, which needs the worker running for weeks.

## Look at the counters
```
redis-cli --scan --pattern 'v1:budget:twelvedata*:d:*' | xargs -n1 -I{} sh -c 'echo {} $(redis-cli GET {})'
redis-cli --scan --pattern 'v1:budget:register_*' | head
```
The day counters are named by UTC day, so yesterday's disappear on their own.

## Change the shares
- More room for new stocks: raise `TwelveData__OnDemandCreditsPerDay` (never above `TwelveData__CreditsPerDay`; the host refuses to start otherwise) and restart. Every credit given to the allowance is one the refresh may miss, so leave it at least the credits the stocks in the catalog need (two per stock per day).
- A user needs more than 20 new stocks in a day: raise `MarketData__Lookup__RegistrationsPerUserPerDay` for everybody, or wait for the next UTC day. There is no per-user override.
- A paid plan has no daily cap: set `TwelveData__CreditsPerDay` large and the lanes stop binding in practice; keep the registration cap.

## The registration cap and Redis
A registration that creates no stock (the stock was registered by someone else a moment earlier, or the provider's listing belongs to another stock and the answer is `409`) gives its slot back, so only a new stock in the catalog counts against the 20. While Redis cannot be reached the cap is counted in each API process (`RegistrationFallback`, per user and UTC day) and each process holds its share, the cap divided by `Deployment:ApiReplicas` (1 today: set it when the API is scaled out, ADR-0048), so the processes together stay within the cap instead of the cap not holding at all; `sakerwatch_marketdata_registration_quota_total{source="process"}` increasing says the shared budget is down; the plan's credits and the searches keep going through uncounted and the provider enforces its own limit (ADR-0047).

## A user reports "you have added the most new stocks allowed for today"
Expected: the answer is `429` with `Retry-After` until 00:00 UTC. Stocks that are already in the catalog (any other user's) still go in. If the stock is wanted today, the owner can raise the cap and restart; otherwise tomorrow.
