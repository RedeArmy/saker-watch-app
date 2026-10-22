# Runbook — exchange rate guard

- **Purpose:** what to do when the daily USD/GTQ refresh keeps a rate apart, or when a real change of the rate is being refused (ADR-0042).
- **Owner:** Eder · **Status:** draft · **Target phase:** 2

## Signals
| Signal | Meaning |
|---|---|
| log event 2303 (warning) `N exchange rates USD/GTQ were not believed (...)` | the refresh refused one or more rates; the reasons are `out_of_band`, `jump` or `rewrite` |
| `sakerwatch_marketdata_fx_rejected_total{reason}` increases | the same, as a count; alert on any increase |
| `sakerwatch_marketdata_fx_age_days` growing while the bank publishes | the newest rate is old because the new ones are being refused |

## Look at what was refused
```sql
SELECT as_of_date, rate, reason, seen_at FROM marketdata.fx_rates_rejected WHERE base = 'USD' AND quote = 'GTQ' ORDER BY as_of_date DESC;
SELECT as_of_date, rate, source FROM marketdata.fx_rates WHERE base = 'USD' AND quote = 'GTQ' ORDER BY as_of_date DESC LIMIT 10;
```
Compare the refused rate with the stored ones and with the bank's own page for the day.

## The bank was wrong
Nothing to do: the refused rate is not used, the calculator uses the latest earlier rate (with its own date), and the next refresh asks again. If the bank corrects the day, the corrected rate is stored when it is inside the band and the limit; the row in `fx_rates_rejected` stays as the record.

## The rate really moved
A move larger than `MarketData:Fx:Guard:MaxDailyChange` (3 % by default) in one day, or a rate outside the band (`MarketData:Fx:Guard:Bands:USDGTQ`, 5 to 12), is refused until the settings allow it:
1. Confirm the value with the bank's publication.
2. Widen the setting that refused it (`MARKETDATA__FX__GUARD__MAXDAILYCHANGE=0.1`, or `MARKETDATA__FX__GUARD__BANDS__USDGTQ__MAX=15`) and restart the worker.
3. The next refresh screens again the days within its overlap (`MarketData:Ingestion:FxOverlapDays`) and stores what is now believed. Older days that were refused stay out; the nearest earlier rate is used for them.
4. Bring the setting back when the move is behind you.

Never edit `fx_rates` by hand: the guard exists so that a number is not stored without being checked, and a hand edit does not change what the refresh does next.

## Another pair
A pair with no band has only the daily limit. Add its band when it is taken (`MARKETDATA__FX__GUARD__BANDS__EURUSD__MIN` and `__MAX`); the name is the two codes joined, in capitals.
