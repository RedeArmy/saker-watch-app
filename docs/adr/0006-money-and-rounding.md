# ADR-0006: Money, FX and rounding rules

- **Status:** accepted (owner confirmed banker's rounding 2026-10-12)
- **Date:** 2026-10-12

## Decision
- `decimal` only for prices, FX, quantities. Never `double`/`float` (architecture test).
- Storage: prices `numeric(19,6)`, FX rates `numeric(19,8)`, share fractions `numeric(19,8)`.
- Calculation uses full precision; **round only at the presentation boundary**.
- Rounding: monetary display (USD/GTQ) 2 decimals `MidpointRounding.ToEven` (banker's); share fractions 6 decimals `ToZero` (never over-state purchasable shares).
- Performance windows from **adjusted** daily closes: 1D=previous trading close; 1W/1M/3M/6M/1Y/5Y=calendar offset, using the last trading day on or before the target date; YTD=last close of previous year. Insufficient history → `null` + reason.
- Property tests (FsCheck): fraction×price≈amount, monotonicity, positive-only inputs, FX round-trip within tolerance.

## Consequences
Confirmed by the owner: banker's rounding (`MidpointRounding.ToEven`) for monetary display.
