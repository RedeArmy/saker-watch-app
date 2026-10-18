# ADR-0034: The portfolio API: membership, order and deleting safely

- **Status:** proposed
- **Date:** 2026-10-18
- **Deciders:** Eder

## Context
ADR-0026 decided what a portfolio is and that deleting one removes the stocks it alone holds from the watchlist, after a confirmation. Milestone 2.6d turns that into routes. Several choices were left open: how a stock gets into a portfolio, what "confirm" means when nothing would be lost, how order is kept, and how a delete stays correct when another request touches the same stocks at the same time.

## Decision
- **Routes** (all under `/v1`, all scoped to the caller; another user's portfolio is a `404`): `POST /portfolios` (with an `Idempotency-Key`, `201` and a `Location`), `GET /portfolios` and `GET /portfolios/{id}` (both with an ETag), `PUT /portfolios/{id}` (replaces name and description), `PUT /portfolios/order`, `PUT /portfolios/{id}/items/{market}/{ticker}`, `DELETE /portfolios/{id}/items/{market}/{ticker}`, `PUT /portfolios/{id}/items/order`, `GET /portfolios/{id}/deletion-preview` and `DELETE /portfolios/{id}?confirm=true`. Every `PUT` and `DELETE` is idempotent by nature and takes no key.
- **A portfolio only groups what the caller follows.** Putting a stock in a portfolio requires it to be on the watchlist already (`404 watchlist.portfolio.item_not_on_watchlist`, with the same answer for a stock the catalog does not know); the client adds it to the watchlist first. Adding to the watchlist is where a stock is registered on demand and where its limit is counted (ADR-0032), and hiding that inside a portfolio route would make a cheap call spend a provider search.
- **Order is explicit and exact.** A portfolio has a `position` among the caller's portfolios and a stock has one inside a portfolio, both from zero. A new portfolio or member goes to the end. Reordering takes the complete list and refuses (`400 watchlist.order_mismatch`) a list with a missing, repeated or foreign element, so a stale client cannot silently drop one.
- **Deleting is guarded in proportion to the loss.** The preview lists the stocks that are in this portfolio and in no other; they would leave the watchlist with their notes and targets. `DELETE` without `confirm=true` answers `409 watchlist.portfolio.delete_requires_confirmation` and changes nothing, but only when that list is not empty: an empty portfolio, or one whose stocks other portfolios also hold, is deleted at once. A portfolio that is already gone answers `204`.
- **The delete is correct against parallel requests.** In one transaction it locks the portfolio row and the watchlist items of its members (`FOR UPDATE`), removes the items no other portfolio holds, and deletes the portfolio. A parallel request that puts one of those stocks in another portfolio takes a share lock on the item through its foreign key, so one of the two waits for the other: if the membership commits first, the stock is no longer an orphan and stays; if the delete commits first, the membership fails because the item is gone and is answered as "not on the watchlist". Tests hold each side open to prove both orders.
- **Limits and names** are the ones of ADR-0031: 20 portfolios and 100 stocks each, enforced by the use case and by the database; names are trimmed and unique per user ignoring letter case, and a race for a name is an outcome (`ON CONFLICT DO NOTHING`), not an aborted transaction.

## Consequences
- The confirmation is a flag on the request, not a token tied to the preview: between the preview and the delete the set of orphaned stocks can change (the user, or another device, moved a stock). The delete removes the stocks that are orphaned when it runs, which is what the confirmation said in spirit; a client that wants to be exact repeats the preview right before.
- Reordering needs the whole list, which is at most 20 portfolios or 100 stocks.
- Metrics per portfolio and the watchlist filter by portfolio are the next part (2.6e).
