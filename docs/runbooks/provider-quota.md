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

## Signals
| Signal | Meaning |
|---|---|
| `sakerwatch_marketdata_stock_lookups_total{operation="ensure",outcome="limit"}` | users are being told they reached the daily limit of new stocks |
| `sakerwatch_marketdata_backfills_total{outcome="waiting"}` increases and new stocks stay `pending` | the on-demand allowance of the day is spent; the loads wait for the next UTC day |
| `sakerwatch_provider_calls_total{outcome="ratelimited"}` while the scheduled refresh runs | the plan's day is spent; check the other counters before blaming the allowance |
| log event 2003 | Redis could not be reached: the budgets fail open and the provider enforces its own limit |

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
