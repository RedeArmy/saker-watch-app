# ADR-0045: Hardening of what the API can write to the shared catalog

- **Status:** proposed
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
The second migration review of Phase 2 found four things in the objects the API can reach through `register_stock` and the watchlist triggers:
- `fx_rates.rate` was narrower than the rates the domain accepts, so an accepted rate could overflow the column and fail a whole batch (M1).
- The provider name, market code and symbol of an alias and the time zone of a stock had only a length, so a compromised API login could plant hostile or unparseable text that the worker would then read (L1).
- When the provider's listing already belonged to another stock, `register_stock` inserted nothing for the alias and still returned the new stock, which was then left with no alias and could never be loaded (L2).
- The ceiling triggers ran before `ON CONFLICT DO NOTHING` was resolved, so a user at a ceiling who added something they already had got the ceiling error instead of the quiet "already there" (L3).

## Options considered
- **M1: narrow the domain or widen the column.** Lowering `MinRate`/`MaxRate` would shrink what the money kit accepts for no reason of the kit; widening the column (a few thousand rows) makes the two agree and is compatible with the previous version of the application.
- **L1: validate in the application only, or also in the database.** The application already validates what it builds, but `register_stock` is callable by the API's database login directly, which is the case the checks are for.
- **L2: return the stock that owns the alias, or refuse.** Returning the owner would answer a request for ticker X with the stock of ticker Y; refusing is explicit, and the unit of work rolls the registration back.
- **L3: leave it, or let existing rows through the trigger.** The application already looks the row up first, so only races and retries at the limit are affected; letting a row that exists through costs one indexed lookup per insert and makes the statement mean what it says.

## Decision
- `fx_rates.rate` becomes `numeric(30,12)`, the width of `fx_rates_rejected` and of the whole range of the domain (migration `WidenFxRateColumn`).
- The database checks the text the API can write: provider name `^[a-z0-9_]{1,32}$` (the same shape as a price source), provider market code `^[A-Za-z0-9]{1,16}$`, provider symbol `^[A-Za-z0-9][A-Za-z0-9._/-]{0,31}$` and time zone `^[A-Za-z0-9_+-]+(/[A-Za-z0-9_+-]+){0,2}$` (migration `AddCatalogTextChecks`). The checks are plain and not `NOT VALID`: a catalog row that does not fit stops the deploy.
- `register_stock` raises a unique violation of `pk_symbol_aliases` when the provider's listing belongs to another stock, after the alias insert so that two parallel registrations are told apart; the registry turns it into `ProviderListingTakenException` and the directory answers `409 marketdata.stock.provider_listing_taken` (migration `RegisterStockProviderListingTaken`). The same stock registering its own alias again stays idempotent.
- The three ceiling triggers take their lock, let a row that already exists through, and count only for a new one (migration `CeilingsSkipRowsThatAlreadyExist`); they stay callers' triggers, not security definer, with a search path that ends in `pg_temp`.

## Consequences
- A name a future provider sends in capitals must be lower-cased by its adapter (as the price `source` already requires); otherwise its registrations fail the check.
- A conflict of the alias is rare (it needs two tickers that one provider calls the same listing) and now visible at the request, with nothing left behind.
- Every migration here only adds or widens, so the previous version of the application keeps working on the new schema.
