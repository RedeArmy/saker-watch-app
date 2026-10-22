# ADR-0042: A guard for the exchange rate

- **Status:** proposed
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
Every quetzal figure of the product (the calculator, the portfolio figures) comes from one number per day: the USD/GTQ rate of the Banco de Guatemala (ADR-0028). The service is public SOAP and calls itself "not an official source". Until now the only check on a rate was that it was positive, so a misplaced decimal (77.5 for 7.75), a row with a wrong day or a tampered answer was stored and used, and a corrected value in the next refresh replaced the stored one without a trace. Prices have a guard against a rewritten history (ADR-0029); the rate had none (security audit of Phase 2, finding M2; T40).

## Options considered
- **Keep trusting the bank.** Cheapest; one wrong digit corrupts every figure until someone notices.
- **A band only.** Catches the misplaced decimal, but not a plausible wrong value such as 7.2 for 7.7 within the band, and says nothing about a history being rewritten.
- **A band, a limit of change per day and a refusal to overwrite a day with a very different value.** Catches the absurd, the abrupt and the silent rewrite, and costs two settings.
- **A second source to cross-check.** The strongest, but it needs another provider, its terms and a policy for when the two disagree; it is a Phase 5 or 6 matter if the product ever needs it.

## Decision
- **Where:** the daily refresh (`FxRefreshHandler`) screens the rates the bank returns with a pure domain function (`FxRateGuard`) before it writes them. The calculator and the portfolios read `fx_rates` as before and never see a refused rate.
- **Three rules**, in this order, each refusing a rate with its reason:
  1. `out_of_band`: the pair has a band and the rate is outside it (edges are inside). Default: USD/GTQ from 5 to 12 (the quetzal has traded between about 7 and 8 for twenty years).
  2. `rewrite`: a rate is stored for the same pair, day and source and the new one differs from it by more than the daily limit. A smaller difference is a correction and is allowed.
  3. `jump`: the rate differs by more than the daily limit from the rate before it, the newest earlier day that is stored or was just accepted. The comparison is with the previous day and not with the first of the batch, so a gradual drift passes and an abrupt step does not; a refused rate is not the one the next is compared with.
- **The limit** is a fraction (default 3 %). A pair with no band has only the limit, and a first rate with nothing before it has only its band.
- **What happens to a refused rate:** it is not stored in `fx_rates`. It is kept in `marketdata.fx_rates_rejected` with the rate as given, the reason and when it was seen (one row per pair, day and source; the last refusal wins). The API has no grant on that table, the worker can insert and update, and nobody deletes. The refusal is logged (event 2303, a warning) and counted (`sakerwatch_marketdata_fx_rejected_total{reason}`), so a refusal is an alert and never a silent gap.
- **Settings** (`MarketData:Fx:Guard`): `MaxDailyChange` (0.001 to 0.5) and `Bands`, a dictionary by the two codes joined (`USDGTQ`) with `Min` and `Max`, validated at start-up. Bands are added by configuration when another pair is taken.
- **The operator's way out** is in the runbook `docs/runbooks/fx-rates.md`: a real change larger than the limit is accepted by widening the limit or the band and letting the next refresh run; nothing is edited by hand.

## Consequences
- A wrong rate in an answer of the bank does not reach a user. The calculator falls back, as it already does for a missing day, to the latest earlier rate with its own date, and the existing `fxStale` flag says when that is old.
- A real, large move of the quetzal (more than 3 % in a day) is refused until the operator widens the limit: a deliberate choice for a currency that does not move. The refusal is visible the same day.
- Only the days within the refresh's overlap are screened again when the limit is widened; a day refused longer ago stays out and the nearest earlier rate is used for it.
- One more table (`fx_rates_rejected`, forward migration `CreateFxRatesRejected`), no user data, no row-level security.
- A rate that is plausible and wrong (7.2 for 7.7) still passes. Only a second source would catch it; this ADR does not add one.
