# ADR-0043: Provider credit lanes and a daily cap on new stocks

- **Status:** accepted
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
The free Twelve Data plan allows 800 credits a day and 8 a minute, and every process shares one budget in Redis (ADR-0027). Two kinds of work spend it: the scheduled refresh of the stocks already in the catalog, and the history load that follows a registration (two credits: the adjusted and the raw series). A user who adds a stock the catalog does not have causes a registration, and the registration queues a history load. The per-user limit on adding stocks is 10 a minute, so one user could register unknown tickers for about an hour and spend the whole day's credits; the daily refresh would then be refused and every price in the product would go stale (security audit of Phase 2, finding M1; T38, T42).

## Options considered
- **Only a per-user daily cap.** Bounds one user, but several users together can still spend the day, and the product has no way to say how much of the day the refresh keeps.
- **Only an allowance for on-demand work.** Protects the refresh from everybody at once, but one user could use the whole allowance and leave the others with nothing.
- **Both:** a per-user cap of new stocks a day, and a daily allowance of credits for on-demand history loads that is a share of the plan's day. The first keeps users fair to each other; the second keeps the refresh safe whatever the users do.
- **Require an allow-list of stocks or a market-cap signal before registering.** The strongest control on what enters the catalog, but it needs data the providers' free tiers do not give and turns "any listed stock" into a curated list; it can be added later if the cap is not enough.

## Decision
- **A cap per user per UTC day** on registering stocks the catalog does not have (`MarketData:Lookup:RegistrationsPerUserPerDay`, default 20). It is taken from the same shared Redis budget under a key of the user (`register_{user}`), once the provider has confirmed the stock and just before it is registered: a ticker the provider does not know costs a search, not a registration, and a stock that is already in the catalog costs nothing. Past the cap the answer is `429` with the code `marketdata.stock.registration_limit` and `Retry-After` until the next UTC day. A call with no user (a job) is not limited by it.
- **Two lanes of credits.** Every provider call says whose it is (`CallLane`): the daily refresh is `Scheduled`; the history load of a registered stock, and the reload after the provider rewrote an adjusted history, are `OnDemand`. An on-demand call spends first from an allowance of its own (`TwelveData:OnDemandCreditsPerDay`, default 200 of the 800, so 100 new histories a day) and then from the plan's budget; a scheduled call spends only from the plan's. The allowance is spent first because the plan's credits are the scarce ones, and it is validated to be at most the plan's day.
- **When the allowance is spent** the history load is a rate-limit answer with the wait until the next day, which the queue already treats as waiting and not as a failed attempt (ADR-0029): the new stock stays `pending` until tomorrow. The refresh is not affected.
- **Waits are capped at one day** wherever a provider's own `Retry-After` becomes ours, and a user is never told to wait more than five minutes because of a provider's rate limit: the budgets reset every day and nothing legitimate waits longer.
- **Like the other budgets, both fail open** when Redis is unreachable (event 2003): the provider enforces its own limit, and the worst case is a rate-limit answer from it.

## Consequences
- One user cannot spend more than 20 histories (40 credits) a day, and all users together cannot spend more than the allowance: at least 600 credits of the 800 stay for the refresh, which is what 300 stocks need at two credits each. A catalog above that needs a paid plan or another provider (A-027), as before.
- A user who adds more than 20 new stocks in a day is told so and can add the rest tomorrow; the stocks the catalog already has are not limited.
- A burst of registrations over the allowance makes the newest stocks wait for the next day with status `pending`; this is visible in the watchlist and in the `waiting` outcome of the backfill metric.
- A registered but unwanted stock is still in the shared catalog and is refreshed every day (the catalog has no deletion in Phase 2); the caps bound how fast that can happen, not that it happens. Removing stocks nobody follows is a Phase 5 retention matter.
- Retries by the HTTP pipeline would have made the provider's count run ahead of ours (security audit, L5); the Twelve Data client is no longer retried by the pipeline (ADR-0044).
