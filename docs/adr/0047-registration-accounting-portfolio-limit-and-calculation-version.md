# ADR-0047: Accounting of registrations, the limit of a portfolio and the version of the calculation in the cache key

- **Status:** proposed
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
The review of Phase 2 found four places where a decision already taken (ADR-0041, ADR-0043, ADR-0044) leaves a gap that is not a defect of the decision but of what surrounds it:
- **Orphan registrations (B2).** A new stock is registered and committed in a unit of work of its own, before the watchlist item is written, so that no connection is held while a provider is asked (ADR-0044). If the write of the item then fails (a limit reached in a race, a conflict), the stock stays in the shared catalog with its first load queued and the user's registration of the day spent.
- **The registration cap (B3).** The cap of new stocks per user and day (ADR-0043) was charged before the registration: a request that found the stock already registered by another (a race), or that was refused with `409 provider_listing_taken`, still used a slot; and when Redis could not be reached the shared budget let every call through uncounted, so the cap, whose job is to protect the provider's credits, did not hold exactly when the shared state was gone.
- **The cache key (B6).** The key of the performance windows holds the listing, the stamp of its last load and the gap setting (ADR-0041). Nothing in it says which version of the rules made the report: a change of the calculation, deployed without a new load of data, would be served from the old keys for up to 26 hours, beside the new numbers computed elsewhere (the calculator, the sparklines).
- **The limit of a portfolio (B10).** `Watchlist:Limits:MaxItemsPerPortfolio` was read and validated but never applied: only the ceiling of 100 in the database trigger held, so an operator who lowered the setting saw no effect.

## Options considered
- **B2: one transaction around everything, or a compensating delete, or accept.** One transaction holds a connection while the provider answers (the reason of ADR-0044). Deleting the stock when the item write fails races with another user who may have added it meanwhile, and a catalog row is a shared asset: it is valid (the provider confirmed it), its load is idempotent, and the next user who adds the stock finds it ready. Accepting the orphan costs at most the registrations the user could make anyway.
- **B3: charge after, charge before and give back, or a local fallback.** Charging after the registration lets parallel requests exceed the cap. Charging before and giving back when no stock was created keeps the cap exact and makes a refusal or a lost race free. For Redis being down: failing closed would stop every registration; letting it through leaves the credits unprotected; counting in the process keeps a cap that holds per replica.
- **B6: delete keys on deploy, a hash of the code, or a version in the key.** Deleting needs a step in each deploy that someone must remember. A hash of the assembly changes on every build and would empty the cache for no reason. A version number in the key, raised when the rules change and pinned by a test, is explicit and costs nothing when the rules do not change.
- **B10: check before in the handler, or under the lock in the repository.** A count read before the insert lets parallel adds both pass; the repository already takes a lock per portfolio for the order of its members, so the count under that lock is exact.

## Decision
- **B2 is accepted and documented, not changed.** The registration stays committed before the item is written. An orphan stock is bounded by the daily cap of the user (20 by default) and is reused by whoever adds that stock next. Nothing deletes it.
- **A registration slot is given back when no stock was created.** `RegistrationQuota` keeps the slot it took; `StockDirectory` gives it back when the registry answers with the id of a stock another request created, and when `ProviderListingTakenException` is raised. `IProviderCallBudget.ReleaseAsync` (already there for the on-demand allowance) does it in Redis.
- **The cap holds when Redis is down.** `BudgetDecision` says when it let a call through only because the budget could not be reached (`Degraded`). `RegistrationQuota` then counts for itself in `RegistrationFallback`, a count per user and UTC day in the process, with the same cap: the bound while Redis is down is the cap for each replica, and a day later it is forgotten. The other budgets (the plan's credits, the search) keep letting the call through: the provider enforces its own limit.
- **The calculation has a version in the cache key.** `PerformanceCalculator.Version` is part of every key (`v1:perf:{stock}:{stamp}:{maxGap}:c{version}`). `PerformanceCalculatorVersionTests` pins the reports that rules give for a holiday week, the gap rule, a missing baseline and a market without a calendar, and fails with the instruction to raise the version when they change.
- **The configured limit of a portfolio is applied.** `AddMemberAsync` takes the limit and counts the members under the portfolio's lock; a stock that is already a member passes. The ceiling in the database stays behind it.

## Consequences
- A change to the rules of the performance windows is a change of two lines in the same commit (the code and the version) and of one fixture; forgetting the version fails the test, forgetting the fixture fails nothing but means the test no longer pins the rules.
- Keys written by the previous release (`v1:perf:{stock}:{stamp}:{maxGap}`) are never read again and expire after their 26 hours; the first reads after the deploy are misses.
- With several replicas and Redis down, a user can register up to the cap on each replica (the cap times the replicas), which is bounded and small.
- An orphan stock can exist, with its history queued; it is visible in the catalog like any other.
- A portfolio with more members than a newly lowered limit keeps them; only adding is refused.
