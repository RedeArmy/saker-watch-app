# API error codes

- **Purpose:** the catalogue of the `code` of every problem response a screen can meet, with its status, what it means and what the screen does; the contract between the API and the Phase 3 BFF and frontend.
- **Owner:** Eder · **Status:** draft · **Target phase:** 2

## How a client reads an error
Every failure is an RFC 9457 problem (`application/problem+json`) with, besides `status`, `title` and `type`:

| Member | Meaning |
|---|---|
| `code` | Stable identifier to switch on. Present on every problem, whichever part of the pipeline wrote it: a failure of a use case, the rate limiter (`rate_limited`), a busy database (`busy`), an unexpected error (`internal_error`), or the framework (`http.404`, `http.405`, `http.415`, `http.400`: `http.` and the status) |
| `type` | `urn:sakerwatch:error:` and the code, for the codes above the framework's own |
| `errors` | Only on `validation.failed`: an object keyed by the field as the JSON body spells it (camelCase, dotted when nested), each with a list of messages |
| `correlationId`, `traceId` | Show the first to the user when they have to report a problem; it finds the request in the logs |
| other members | Listed per code below (`stocksToRemove`, `removalToken`) |

- **`title`, `detail` and the messages in `errors` are English, written for developers, and may change. A screen shows its own text chosen by `code`** (the product is in Spanish, `es-GT`); the column "Screen" proposes it. A code that a screen does not know falls back to the generic text of its status.
- A code keeps its status; a new meaning is a new code (review found `marketdata.fx.rate_unavailable` used with two statuses, and the second use now has its own code). Adding a code is not a breaking change, so a client must have a default for a code it does not know.
- `429` and `503` carry `Retry-After` in seconds; a screen waits that long before it offers to retry. `409 idempotency.in_progress` is retried by the client by itself after a second, with the same `Idempotency-Key`.

## Any endpoint
| Code | Status | When | Screen |
|---|---|---|---|
| `validation.failed` | 400 | A field is missing or breaks a rule; see `errors` | Mark each field with its own text |
| `http.400` | 400 | The body is not valid JSON, or a parameter cannot be read | "No pudimos leer la solicitud." |
| `auth.unauthorized` | 401 | No token, or an expired or invalid one | Go to sign-in |
| `identity.not_authenticated`, `watchlist.not_authenticated`, `idempotency.unauthenticated` | 401 | The use case found no user behind the request | Go to sign-in |
| `auth.forbidden` | 403 | The token is valid but does not allow this | "No tienes acceso a esto." |
| `identity.account_inactive` | 403 | The account is suspended or deleted | "Tu cuenta no está activa." No retry |
| `resource.not_found`, `http.404` | 404 | The resource does not exist or is not the caller's | "No encontramos lo que buscas." |
| `http.405`, `http.415` | 405, 415 | A bug of the client (wrong method or media type) | Generic error with the `correlationId` |
| `rate_limited` | 429 | Too many requests from this user or address | Wait `Retry-After`, then offer to retry |
| `busy` | 503 | The database refused to wait; nothing was changed | Retry after `Retry-After` (one second) |
| `internal_error` | 500 | Unexpected failure; the message is never sent | "Algo salió mal." and the `correlationId` |

## Idempotency (`POST` that needs an `Idempotency-Key`)
| Code | Status | When | Screen |
|---|---|---|---|
| `idempotency.key_required` | 400 | The header is missing | A bug of the client: generate one key per user action |
| `idempotency.key_invalid` | 400 | Not 16 to 64 letters, digits, `-` or `_` | A bug of the client |
| `idempotency.key_reuse` | 422 | The key was used with a different body | A bug of the client: a new key for a new action |
| `idempotency.in_progress` | 409 | The same request is still running | Wait a second and send it again with the same key |
| `idempotency.replay_unavailable` | 409 | The first answer was too large to repeat | Fetch the resource with a `GET` |

## Profile
| Code | Status | When | Screen |
|---|---|---|---|
| `identity.user_not_found` | 404 | The account of the token has no profile | Sign in again |

## Watchlist
| Code | Status | When | Screen |
|---|---|---|---|
| `watchlist.limit_reached` | 409 | The watchlist holds the most stocks allowed (500) | "Tu lista está llena. Quita una acción para agregar otra." |
| `watchlist.item_changed` | 409 | The list changed while the request ran | Reload the list and try again |
| `watchlist.item.notes_too_long` | 400 | Notes over 2 000 characters | Mark the notes field |
| `watchlist.item.notes_invalid` | 400 | Notes with control characters | Mark the notes field |
| `watchlist.item.target_price_invalid` | 400 | Target price not above zero, or one trillion or more | Mark the field |
| `watchlist.item.target_price_precision` | 400 | Target price with more than eight decimals | Mark the field |
| `watchlist.order_mismatch` | 400 | The list to reorder is not exactly the portfolios (or the stocks of the portfolio) | Reload and let the user reorder again |

## Portfolios
| Code | Status | When | Extra members | Screen |
|---|---|---|---|---|
| `watchlist.portfolio.not_found` | 404 | Not found, or not the caller's | | "Esa cartera ya no existe." |
| `watchlist.portfolio.name_required`, `watchlist.portfolio.name_too_long`, `watchlist.portfolio.name_invalid`, `watchlist.portfolio.description_too_long`, `watchlist.portfolio.description_invalid` | 400 | Name empty, over 60 characters or with control characters; description over 500 or with control characters | | Mark the field |
| `watchlist.portfolio.name_taken` | 409 | The caller already has a portfolio with that name (letter case ignored) | | "Ya tienes una cartera con ese nombre." |
| `watchlist.portfolio.limit_reached` | 409 | 20 portfolios | | "Llegaste al máximo de carteras." |
| `watchlist.portfolio.items_limit_reached` | 409 | 100 stocks in the portfolio | | "Esta cartera está llena." |
| `watchlist.portfolio.item_not_on_watchlist` | 404 | The stock is not on the watchlist | | Offer to add it to the watchlist first |
| `watchlist.portfolio.item_changed` | 409 | The portfolio changed while the request ran | | Reload and try again |
| `watchlist.portfolio.delete_requires_confirmation` | 409 | `DELETE` without `confirm=true` and stocks would leave the watchlist | `stocksToRemove` (stock summaries, ordered by ticker), `removalToken` | List the stocks, ask, then send `confirm=true&removalToken=` |
| `watchlist.portfolio.removal_token_required` | 409 | `confirm=true` with no token while stocks would leave | the same two | The same as above |
| `watchlist.portfolio.removal_changed` | 409 | The stocks that would leave are not the ones of the token (one was put in the portfolio since) | the current `stocksToRemove` and `removalToken` | Show the new list and ask again; nothing was deleted |

A token that is not sixteen hex digits is `400 validation.failed` on `removalToken`.

## Stocks, search and prices
| Code | Status | When | Screen |
|---|---|---|---|
| `marketdata.stock.not_found` | 404 | The catalog (or the provider, when adding) does not have that ticker in that market | "No encontramos esa acción en ese mercado." |
| `marketdata.stock.market_not_available` | 400 | The market is not served | "Ese mercado aún no está disponible." |
| `marketdata.stock.currency_mismatch` | 400 | The listing does not trade in the currency of its market | "No pudimos agregar esa acción." |
| `marketdata.stock.provider_listing_taken` | 409 | The provider lists that ticker as another stock of the catalog | "No pudimos agregar esa acción. Avísanos." |
| `marketdata.stock.registration_limit` | 429 | The user added the daily maximum of new stocks (20 a UTC day); stocks already in the catalog still go in | "Agregaste el máximo de acciones nuevas por hoy. Vuelve mañana." (`Retry-After` is the time to 00:00 UTC) |
| `marketdata.search.query_invalid` | 400 | The search text is empty, too long or has characters other than letters, digits, space, `.`, `&`, `-`, `_` | Mark the search box |
| `marketdata.search.busy` | 429 | The shared search budget is spent | "Hay muchas búsquedas. Intenta en un momento." |
| `marketdata.search.provider_unavailable` | 503 | The provider cannot answer for now | "El proveedor de datos no responde. Intenta más tarde." |
| `marketdata.prices.range_invalid`, `marketdata.prices.interval_invalid` | 400 | `range` or `interval` is not one of the allowed values | A bug of the client |

## Calculator
| Code | Status | When | Screen |
|---|---|---|---|
| `calculator.price_unavailable` | 422 | The stock has no price yet and none was given | "Esta acción aún no tiene precio. Ingresa uno." |
| `calculator.amount_invalid` | 400 | The amount is not above zero | Mark the field |
| `calculator.price_out_of_range` | 400 | The price of a share is outside the allowed range | Mark the field |
| `calculator.fee_out_of_range` | 400 | The commission is outside 0 to 100 percent | Mark the field |
| `money.shares_invalid`, `money.amount_out_of_range`, `money.price_invalid`, `money.currency_invalid`, `money.currency_mismatch`, `money.rate_invalid`, `money.baseline_invalid`, `money.division_by_zero` | 400 | A number is out of range, has too many decimals or mixes currencies | Mark the field |
| `marketdata.fx.rate_unavailable` | 404 | No USD/GTQ rate on or before the day | "No hay tipo de cambio disponible." No retry |
| `marketdata.fx.amount_invalid` | 400 | The amount to convert is negative or too large | Mark the field |
| `marketdata.fx.rates_not_combinable` | 400 | The rates of a conversion path cannot be combined within the allowed range | Generic error with the `correlationId` |

## Not shown to a user
Codes of the ingestion, the provider adapters and the operator commands (`marketdata.provider.*`, `marketdata.ingestion.*`, `marketdata.lifecycle.*`, `marketdata.filings.*`) never reach a response; the same holds for `marketdata.fx.rate_not_positive`, `marketdata.fx.same_currency` and `marketdata.fx.source_invalid` (rates the ingestion refuses to store) and for `marketdata.stock.name_invalid` and `marketdata.stock.time_zone_invalid` (a listing the provider described badly, which registration answers as `marketdata.stock.not_found`). `watchlist.item.ids_required`, `watchlist.portfolio.user_required`, `watchlist.portfolio.position_invalid` and `result.uninitialized` mean a bug in the server. `ErrorCatalogueTests` keeps this page and the code in step.
