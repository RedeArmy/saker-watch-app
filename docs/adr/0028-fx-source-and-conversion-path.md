# ADR-0028: FX source and conversion path

- **Status:** proposed
- **Date:** 2026-10-16
- **Deciders:** Eder

## Context
The calculator and the optional GTQ view need a USD to GTQ rate with its date and source, and later CAD, EUR and JPY to GTQ (ADR-0018 pivots through USD). The Banco de Guatemala publishes the reference exchange rate through a web service.

Facts checked on 2026-10-16: the service at `banguat.gob.gt/variables/ws/TipoCambio.asmx` exposes `TipoCambioDia`, `TipoCambioRango` (`fecha_ini`, `fecha_fin` in `dd/mm/aaaa`), `TipoCambioFechaInicial`, the `...Moneda` variants and `Variables`/`VariablesDisponibles`; range results are sorted by date; day functions return the reference rate where it applies. The bank states the service is free and not an official information source: only its home page is official. The exact request and response format (SOAP versus HTTP) must be taken from the service description when the adapter is written; the currency code mapping is not documented on the page.

## Decision
- **Anchor:** `IFxProvider` has a Banco de Guatemala implementation for USD to GTQ, stored as `marketdata.fx_rates` (`base`, `quote`, `as_of_date`, `source`, `rate numeric`) with one row per day and unique `(base, quote, as_of_date, source)`.
- **Other pairs:** a market FX source (the stock provider's forex endpoint, free for the pairs the plan allows) supplies USD to CAD, EUR and JPY when those markets are enabled. A pair that is not stored is converted through USD; a conversion always returns the path, the date and the source of each leg.
- **Missing days:** weekends and bank holidays have no rate; the latest earlier rate is used and its `as_of_date` is returned, never a fabricated one. A rate older than a configured age is flagged stale.
- **Disclaimer:** the result states that the rate is a reference and that broker rates, fees and minimum order sizes differ; the Banco de Guatemala home page is the only official source.
- The adapter uses a named client with the host allow-list, parses defensively and treats the response as untrusted data.

## Consequences
- The conversion is as good as the bank's daily reference; intraday FX is out of scope.
- The service format is unverified until the adapter is written against its service description and a recorded fixture.
