# ADR-0028: FX source and conversion path

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-16
- **Deciders:** Eder

## Context
The calculator and the optional GTQ view need a USD to GTQ rate with its date and source, and later CAD, EUR and JPY to GTQ (ADR-0018 pivots through USD). The Banco de Guatemala publishes the reference exchange rate through a web service.

Facts checked on 2026-10-16: the service at `banguat.gob.gt/variables/ws/TipoCambio.asmx` exposes `TipoCambioDia`, `TipoCambioRango` (`fecha_ini`, `fecha_fin` in `dd/mm/aaaa`), `TipoCambioFechaInicial`, the `...Moneda` variants and `Variables`/`VariablesDisponibles`; range results are sorted by date; day functions return the reference rate where it applies. The bank states the service is free and not an official information source: only its home page is official. The request and response format was verified live on 2026-10-17 (below).

## Verified against the live service (2026-10-17)
Taken from the service description (`TipoCambio.asmx?WSDL`) and real calls, no key needed:
- **Protocol:** SOAP 1.1 over HTTPS (`POST https://www.banguat.gob.gt/variables/ws/TipoCambio.asmx`, `Content-Type: text/xml; charset=utf-8`, header `SOAPAction: "http://www.banguat.gob.gt/variables/ws/<Operation>"`, namespace `http://www.banguat.gob.gt/variables/ws/`). An unknown `SOAPAction` is an HTTP 500 with a `soap:Fault`.
- **Operations:** `TipoCambioDia` (today: `CambioDolar/VarDolar` with `fecha` and `referencia`), `TipoCambioRango` (`fechainit`, `fechafin`), `TipoCambioFechaInicial`, the `...Moneda` variants (extra `moneda` code) and `VariablesDisponibles` (currency codes).
- **Format:** dates are `dd/MM/yyyy` and rates use a dot with five decimals (`7.64572`). Range rows are `Vars/Var` with `moneda`, `fecha`, `venta` and `compra`; for USD (`moneda` 2) the two are equal and equal to the daily reference. Rows come sorted by date.
- **Weekends and holidays are filled by the bank** with the previous rate (10, 11 and 12 October 2026 carry the same value), so for USD there are no gaps.
- **A future date has a rate**: the bank publishes the next day's rate ahead of time (20 October 2026 answered `7.63672` on the 17th). Ingestion therefore asks only up to today and never stores a date after it.
- **Size:** ten years in one call is about 3 900 rows and 390 KB, so the first load is one request; the adapter caps the response size.
- **Edge cases:** a reversed range answers an empty result (`TotalItems` 0), not an error.
- **Other currencies exist**: `moneda` 3 yen, 7 Canadian dollar and 24 euro, quoted as units per US dollar (`1.4283` for CAD on 12 October). Their rows have gaps on weekends. This may replace the market FX source for CAD, EUR and JPY; to be decided with the Asia/Canada/Europe milestone.

## Decision
- **Anchor:** `IFxRateProvider` has a Banco de Guatemala implementation for USD to GTQ, stored as `marketdata.fx_rates` (`base`, `quote`, `as_of_date`, `source`, `rate numeric`) with one row per day and unique `(base, quote, as_of_date, source)`.
- **Other pairs:** a market FX source (the stock provider's forex endpoint, free for the pairs the plan allows) supplies USD to CAD, EUR and JPY when those markets are enabled. A pair that is not stored is converted through USD; a conversion always returns the path, the date and the source of each leg.
- **Missing days:** the bank fills weekends and holidays itself (verified above), but a day can still be missing (an outage, a pair that has gaps, a date before the first load); the latest earlier rate is used and its `as_of_date` is returned, never a fabricated one. A rate older than a configured age is flagged stale.
- **Disclaimer:** the result states that the rate is a reference and that broker rates, fees and minimum order sizes differ; the Banco de Guatemala home page is the only official source.
- The adapter (`BanguatFxProvider`, milestone 2.4b) uses the named client `banguat` (HTTPS only, host allow-list, 4 MB cap, 30 s total timeout), asks `TipoCambioRango` for USD (`moneda` 2) and reads the sale rate. It parses the XML with DTDs prohibited, keeps only USD rows in the requested range with a `dd/MM/yyyy` date and a positive decimal, drops and counts the rest, classifies failures like the market data providers (a SOAP client-side fault is permanent, any other failure is retryable) and keeps the raw answer in Mongo. It serves USD to GTQ only; other pairs are refused until they are decided.
- **Ingestion (milestone 2.4c):** job `marketdata.fx-refresh.v1`, key `marketdata.fx-refresh:USD:GTQ:{yyyyMMdd}` with the day in Guatemala (UTC-6, no daylight saving), queued by the same scheduler that queues the market sessions (every 10 minutes, no state of its own; `MarketData:Ingestion:FxEnabled`). The first run asks for ten years in one call; later runs ask from `FxOverlapDays` (5) before the newest stored day up to the job's day, so a correction is picked up. An outage retries with the queue's backoff, `FxMaxAttempts` (12) times, about an hour and a half; a permanent failure is logged and the next day asks again. A rate older than `FxMaxAgeDays` (3) is stale (`FxFreshness`), exposed as the gauge `sakerwatch_marketdata_fx_age_days`.
- **Conversion (milestone 2.4d):** `IFxConverter` converts an amount with the rates in force on a day (today in Guatemala by default): for each step it takes the newest stored rate on or before the day, in the asked direction or as the reciprocal of the opposite pair (whichever is newer); a pair that is not stored and has no USD side goes through USD. The result (`FxConversion`) carries the amount converted without rounding, the legs with the rate as applied, the day, the source and whether it was inverted, the combined rate, the oldest day among the legs and `IsStale` (any leg older than `FxMaxAgeDays` for the requested day). Every result carries the notice key `marketdata.fx.reference_only`, which the interface renders in Spanish. A missing step is `marketdata.fx.rate_unavailable`; a negative or overflowing amount is `marketdata.fx.amount_invalid`. Only USD to GTQ is stored today, so CAD, EUR and JPY convert once their pairs against USD are ingested.

## Consequences
- The conversion is as good as the bank's daily reference; intraday FX is out of scope.
- The adapter is written against recorded fixtures of the responses verified above (milestone 2.4b); the bank warns the service is not an official source, so a malformed answer is treated as a failed call, never as a rate.
