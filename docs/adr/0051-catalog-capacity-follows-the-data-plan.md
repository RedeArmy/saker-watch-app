# ADR-0051: The size of the shared catalog follows what the data plan can refresh

- **Status:** accepted
- **Date:** 2026-10-23
- **Deciders:** Eder

## Context
The product spec asks for a watchlist of 500 stocks and a nightly analysis of 1,000. The only market data plan in use is the free Twelve Data one: 8 credits a minute and 800 a day (ADR-0027). A daily refresh of a listing fetches the adjusted and the raw series, two credits, so a catalog of 500 stocks needs 1,000 credits a day, more than the plan has; and 200 credits a day are kept for loading the history of new stocks (ADR-0043), so the refresh has 600. The plan keeps **about 300 listings fresh**, less the credits that reloads of rewritten histories use (about eight a day on average, peaks of 40 to 60 credits, `docs/runbooks/provider-quota.md`).

Nothing stopped the catalog from growing past that. With 400 stocks the refresh would run out of credits every day for the last 100: those stocks would go stale one after another, silently, which breaks the staleness target of the spec (under 24 hours on trading days) in a way no test or alert shows. The gate review of Phase 2 listed it as the contradiction between the spec and the plan (TD-008).

## Options considered
- **Pay for a plan that has no daily limit** (Twelve Data Grow: $79 a month on the pricing page of 2026-10-23, which also shows $29 and different request rates in other places on the same page; verify before paying). It lifts the cap and the 1,000-stock analysis becomes possible, and it also changes "no paid SaaS" in the spec and must come with a licence that allows what the product does (ADR-0027 gate). With one user and a few dozen stocks it buys nothing today.
- **Spend fewer credits per stock.** Fetch only the adjusted series (one credit) and derive the raw close: the two differ by the cumulative factor of dividends and splits, and a derived close is not the one that traded; "no invented numbers" (ADR-0030) rules it out. Refresh the stocks nobody looks at less often: possible later, and with one user the catalog is the user's own list.
- **Cap the catalog at what the plan can refresh** and refuse a new stock past it, with a clear error. Cheap, testable, honest, and reversible by changing a number when the plan changes.
- **Do nothing and alert on stale prices.** The failure is then found by the user, and the alert (TD-004) does not exist yet.

## Decision
- **The shared catalog is capped at `MarketData:Lookup:MaxCatalogListings` active listings, 250 by default** (the 300 the plan sustains, less 50 for reloads). `StockRegistration.ReserveAsync` asks `CatalogCapacity` before it spends one of the user's registrations of the day; past the cap the answer is `409 marketdata.stock.catalog_full` and nothing is registered or queued. A stock that is already in the catalog still goes on a watchlist: it creates nothing. Delisted listings do not count: they are not refreshed.
- The count is read once per registration, so registrations in parallel can pass the cap by their number. It is a margin under the plan's credits, not an exact limit, and an exact one (a count under a lock in `register_stock`) would need a migration for a bound that has 50 listings of slack.
- **The cap follows the plan, and the host checks it.** The rule is `(TwelveData:CreditsPerDay − TwelveData:OnDemandCreditsPerDay) / 2`, less headroom; whoever changes the plan changes both settings together (`docs/runbooks/provider-quota.md`). `TwelveDataOptionsValidator` refuses to start a host that routes a market to Twelve Data when twice the cap is more than the credits the refresh has, and the message names the cap that fits: the invariant this decision rests on cannot be broken by editing one number. A paid plan with no daily limit sets a cap that fits the number of stocks the owner wants. A host that routes no market to Twelve Data is not checked.
- **The spec is corrected** (`docs/product-spec.md`, section 4): 500 is the most one user may follow, reached only when the catalog is large enough; 1,000 stocks for the nightly analysis need a paid plan.

## Consequences
- The 250th stock is the last one a user can add on the free plan, and the 251st gets an error that says why instead of a price that goes stale in a week. The screens of Phase 3 show it (`docs/api/errors.md`).
- The "500 items" SLO of `GET /v1/watchlist` is measured on a catalog that is larger than the free plan allows (the k6 baseline seeds 500 listings); it stays a valid performance target for a paid plan and says nothing about how many stocks the free plan holds.
- The cap is a setting that can be wrong in both directions: too high and some stocks go stale, too low and the owner is refused stocks the plan could carry. `sakerwatch_marketdata_stock_lookups_total{outcome="catalog_full"}` and event 2504 show the refusals, and the freshness gauges show the other failure.
- The cap counts every active listing, also those whose ingestion failed for good and are not refreshed: it errs towards refusing. And it is **one cap for the whole catalog while the credits are one provider's**: when a second provider serves other markets the cap should become one per provider (a decision for the day a second market arrives, A-027), and until then it protects the only provider there is.
- It does not solve the cost of the analysis for 1,000 stocks, or a second market: both need credits and are Phase 4 and later decisions.
