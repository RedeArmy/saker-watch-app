# ADR-0049: Four smaller decisions of the review of Phase 2

- **Status:** proposed
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
The last pass of the review of Phase 2 found four things in decisions that were already taken (ADR-0026, ADR-0043, ADR-0044), each too small for an ADR of its own and each changing a behavior someone could rely on.

## Decisions
- **Confirming a portfolio deletion names what was seen (ADR-0026).** `confirm=true` alone removed the stocks that no other portfolio held *at the time of the delete*, so a stock put in the portfolio after the user looked at the preview left the watchlist unseen, with its notes and target. The preview now carries a `removalToken` (the first sixteen hex digits of the hash of the ids of the stocks that would leave, in any order); `DELETE ?confirm=true&removalToken=...` must send it when anything would leave (`400 watchlist.portfolio.removal_token_required` otherwise) and the repository compares it with the set it computes under the lock of the portfolio's members, which an add to the portfolio also takes, so the set cannot change between the comparison and the delete. A different set is `409 watchlist.portfolio.removal_changed` and nothing is deleted; a confirmation with nothing to remove is a confirmation of the empty set, so a stock that appeared meanwhile is refused too. The API has no released client yet (the screens are Phase 3), so the contract changes now rather than later.
- **The two series of a listing are reserved together (ADR-0043).** A load asks for the adjusted and the raw series with two calls, each reserved on its own: a budget with room for one and not for the other spent a credit on a series that was thrown away when the job waited and started again. `IMarketDataProvider.ReserveDailyAsync` reserves the credits of both before the first call (the calls are then `Prepaid`), a refusal sends none, and `ReleaseDailyAsync` gives back the second when the first fails. A provider that does not meter its calls keeps the default that reserves nothing.
- **A series the provider cut at its row limit is a failure of the listing (ADR-0044).** Asked oldest first, a series of 5 000 rows holds the old part and lacks the latest days; stored, the listing looked loaded and stayed stale. The load now fails with `marketdata.provider.series_truncated`, a permanent failure recorded against the listing (the history asked for is shorter than that today, so this is what a change of it would meet).
- **A search is tried once more (ADR-0044).** The client of the provider makes no retries because a retried `time_series` would spend a credit the budget never counted. A `symbol_search` costs none, so the provider itself tries it once more when the first try met a server error or a timeout, and a blip no longer reaches the user as a 503. An answer that is a 400 is still not read as "unknown symbol": only a 404 is, so a malformed request of ours cannot start the count that delists a stock.

## Consequences
- A client that confirms a deletion must read the token from the preview first: one more request, and the guarantee that what is removed is what was shown.
- The paired reservation is two credits asked of the budget at once: with 7 credits left in the minute a load now waits where it used to spend one and lose it.
- The cost of the extra search try is a request that costs nothing, at most once per search.
