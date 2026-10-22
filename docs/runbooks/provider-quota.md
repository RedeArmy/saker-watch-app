# Runbook — provider credit lanes and the registration cap

- **Purpose:** what to do when new stocks stay `pending`, when users hit the daily limit of new stocks, or when the daily refresh runs out of credits (ADR-0043).
- **Owner:** Eder · **Status:** draft · **Target phase:** 2

## How the day is shared (Twelve Data free plan: 800 credits a day, 8 a minute)
| Work | Lane | What limits it |
|---|---|---|
| the daily refresh of the stocks in the catalog | scheduled | the plan's budget only (key `twelvedata`) |
| the history load of a stock somebody added, and the reload after a rewritten history | on demand | an allowance of its own first, `TwelveData:OnDemandCreditsPerDay` (default 200, key `twelvedata_ondemand`), then the plan's budget |
| adding a stock the catalog does not have | per user | `MarketData:Lookup:RegistrationsPerUserPerDay` (default 20 a UTC day, key `register_{user}`) |

A history costs two credits (adjusted and raw), so 200 credits are 100 new histories a day, and at least 600 credits are left for the refresh.

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

## A user reports "you have added the most new stocks allowed for today"
Expected: the answer is `429` with `Retry-After` until 00:00 UTC. Stocks that are already in the catalog (any other user's) still go in. If the stock is wanted today, the owner can raise the cap and restart; otherwise tomorrow.
