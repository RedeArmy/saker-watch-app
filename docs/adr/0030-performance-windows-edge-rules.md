# ADR-0030: Performance windows, edge rules and reasons

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-17
- **Deciders:** Eder

## Context
ADR-0006 (accepted) defines the performance windows: computed from adjusted daily closes, 1D is the previous trading close, 1W to 5Y are calendar offsets that use the last trading day on or before the target date, YTD is the last close of the previous year, and a window without enough history is `null` plus a reason. It leaves open what the windows are measured from, how a hole in the stored history is told from a short history, how the reasons are named, and where the calculation lives. The watchlist (2.6), the portfolio metrics and the stock detail all read the same numbers, so the rules must live in one place.

## Options considered
- **Measure from today or from the latest stored day.** From today, a stock whose last session was Friday would change its 1W baseline every day of the weekend and a stale series would silently report a window that ends in the past. From the latest stored day, the numbers only change when new data arrives, and staleness stays a separate, visible fact (`stale_since`, ADR-0029).
- **Load the series and compute in memory, or look up the baselines in the database.** A five-year series is about 1 300 rows per stock; a 500-stock watchlist would move 650 000 rows per request against a p95 of 300 ms. Each window needs one price, so the database returns at most nine rows per stock.
- **Accept any earlier price as the baseline, or refuse a baseline that is too far before the target.** Accepting any would turn a hole in the history (an ingestion gap, a halt, a delisting) into a quietly wrong number.

## Decision
- **The anchor is the newest stored adjusted close of the listing** (provisional or final: it is the current price), and every window is a change from a baseline to it. The result is a `decimal` fraction (`0.0123` is 1.23 %); percentages are a presentation matter (ADR-0006).
- **Targets:** 1D is the previous trading day of the market's calendar before the anchor; 1W is seven days earlier; 1M, 3M and 6M are calendar months earlier and 1Y and 5Y calendar years earlier, each clamped to the last day of a shorter month (31 March minus one month is 28 February; 29 February minus one year is 28 February); YTD is 31 December of the previous year.
- **Baseline:** the last stored close on or before the target. A target on a weekend or holiday therefore takes the last trading day before it.
- **Reasons, never a fabricated number.** A window is either a change or a reason: `NoData` (the listing has no price), `InsufficientHistory` (nothing is stored on or before the target: the listing is younger than the window) or `HistoryGap` (the baseline is further before the target than allowed: a hole, not a short history) or `CalendarUnavailable` (only the one-day window: the market has no calendar to tell the previous trading day). The allowed distance is 0 days for 1D, so the previous session must be present, and 10 days (configurable) for the others, which covers the longest closures of a market around holidays.
- **The reader** (`IPerformanceReader`, milestone 2.5b) finds the newest close of every listing in one statement and, in a second one, the last close on or before each target date of each listing (a lateral lookup that walks the primary key backwards and stops at the first row), in batches of 500 listings. A listing with no price, or not in the catalog, is in the answer with `NoData`; a listing whose market has no calendar gets every window but the first (`CalendarUnavailable`) and one warning per market and batch (event 2401), instead of failing the whole read. The gap tolerance is `MarketData:Performance:MaxGapDays` (10).
- **One implementation.** `PerformanceCalculator` in the domain turns an anchor plus the baselines into the windows; it does no I/O. A reader looks the baselines up in the database for many listings in one statement. The watchlist, the portfolio metrics and the detail use only these two.
- **Splits and dividends** are already in the adjusted series (ADR-0029), so no rule here compensates for them. A test with a 4:1 split asserts that the raw close, which falls by 75 %, is never used.

## Consequences
- The windows change only when data arrives, and a stale listing reports numbers that end at its last stored day with `stale_since` beside them.
- A listing with an ingestion hole shows `HistoryGap` instead of a wrong figure until the hole is filled by the next backfill.
- Measured on the test container (`SAKER_SCALE=1`, outside the default suite because loading the data takes about a minute), 500 listings with six years each take between 0.3 and 0.5 s uncached (two runs) (nine index probes per listing across seven yearly chunks; neither a bounded probe nor turning off the chunk-ordered append made it faster). The watchlist has 300 ms for all of its work at that size, so the daily numbers are cached per listing and day (milestone 2.8).
- The gap tolerance is a setting, so a market with longer closures can be given a larger one without touching the code.
