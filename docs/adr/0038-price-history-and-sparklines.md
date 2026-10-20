# ADR-0038: Price history for charts and sparklines

- **Status:** proposed
- **Date:** 2026-10-19
- **Deciders:** Eder

## Context
The Phase 3 screens need two things the API did not give: a stock detail with a chart you can switch between ranges, and a small price chart next to each row of the watchlist. The watchlist list (ADR-0033) shows the newest price and eight changes, not the series behind them. Reading the series is a new way to read the shared market data, so its shape, its size and the rule that decides where a range starts have to be settled once, for every screen.

## Options considered
- **Put a series in each row of the watchlist list.** Simple for the client, but it multiplies the size of the page by the number of points (500 rows of 30 numbers) and defeats the ETag of the list, which would change with every new price.
- **A chart endpoint only; the table fetches one per row.** 500 requests for a table is not acceptable.
- **A chart endpoint for one stock and a separate batch for the table's sparklines.** Chosen.
- **Every range as days.** Five years of days are 1,300 points and all the history can be 5,000; a chart needs far fewer.

## Decision
- **`GET /v1/stocks/{market}/{ticker}/prices?range=&interval=`** for any stock of the catalog (the data is shared, so any signed-in user may read it). `range` is `1W`, `1M`, `3M`, `6M`, `YTD`, `1Y`, `5Y` or `MAX` (default `1Y`), ignoring letter case.
- **A range starts where the performance window of the same name does** (ADR-0030): the same `TargetDates`, so the chart of a range and the change shown for that window begin on the same day, and the first point is the baseline when it was a trading day. `MAX` has no start.
- **Interval:** `day`, `week`, `month` or `auto`. A week or a month is shown by its **last trading day** (the newest day, for the one in progress). `auto` is days up to a year, weeks for five years and months for all the history, which keeps a series to a few hundred points. A caller that wants days for all the history may ask for them.
- **Each point carries both closes:** the close as traded and the adjusted close. A chart draws the adjusted one, which is continuous across splits and dividends (ADR-0029); the other is there for the tooltip. Decimals are strings.
- **The answer is a fact, not a failure, when there is little to say:** a stock with no price yet is an empty series with its status; a stock the catalog does not have is `404`; an unknown range or interval is `400`. The status of the prices (`current`, `stale` with `staleSince`, `pending`, `unavailable`) comes from the one rule the watchlist uses (`PriceStatusRules`), so the chart and the list cannot disagree. The answer has an ETag.
- **`GET /v1/watchlist/sparklines?days=&portfolio=`** returns, for every stock on the caller's watchlist (or of one portfolio, narrowed by the same code the list and the metrics use), the **adjusted closes of its last `days` stored trading days**, oldest first, `days` from 5 to 250 (default 30). A stock with no price yet has an empty line. It is separate from the list so that the list keeps its ETag and the table can ask for the lines after it has drawn.
- **Cost:** a series is one range scan of the primary key of the hypertable (a `DISTINCT ON` over the bucket for weeks and months); the sparklines are one lateral probe per stock, walking the key backwards and stopping after `days` rows, in batches of 500. Neither calls the provider.
- **The module boundary** is `IPriceHistory` in `MarketData.Contracts`: Watchlist asks it for the closes and never reads a market table.

## Consequences
- The Phase 3 detail chart and the table's sparklines need no further backend work.
- A chart of a stock that is delisted or whose data stopped shows what is stored, with the stale status; how a delisted stock is marked is the subject of a later part.
- Weekly and monthly points are computed on read; if that ever costs too much, the daily numbers cache of milestone 2.8 is where it is solved.
