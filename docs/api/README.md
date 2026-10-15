# API guidelines

- **Purpose:** conventions every endpoint must follow; the OpenAPI contract is generated from code (ADR-0014).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1

## Contract
- OpenAPI 3.x per major version; committed at `docs/api/openapi.v1.json`; CI fails on unreviewed diffs and on breaking changes.
- Swagger UI per version (dev; protected or disabled elsewhere).

## Every endpoint must declare
`WithName`, `WithSummary`, `WithDescription`, `WithTags`, typed `Produces<T>` for every status code, `ProducesProblem` (RFC 9457) for errors, auth requirement, request/response examples, and `Idempotency-Key`/pagination/ETag behavior where relevant.

## Contract workflow (implemented, milestone 1.5a)
- The document is generated from code by `Asp.Versioning.OpenApi` + `Microsoft.AspNetCore.OpenApi` and served at `/openapi/v1.json`. It declares the `bearer` scheme and a `security` requirement on every operation that is not anonymous; it pins no `servers` entry.
- `tests/Contract.Tests` hosts `Host.Api` in memory and fails when the generated document differs from `docs/api/openapi.v1.json`. After an intentional API change run `UPDATE_OPENAPI=1 dotnet test tests/Contract.Tests`, review the diff and commit it with the change.
- The same project enforces the conventions below on every mapped endpoint: versioned route, `WithName`, `WithSummary`, `WithDescription`, `WithTags`, a documented success response, 401 and 500 as problems, and no anonymous API endpoint.
- Exposure: the document is anonymous only in Development or with `OpenApi:Anonymous=true`; elsewhere it needs a valid token. Swagger UI (`/swagger`) is served in Development only.

## Versioning
URL segment `/v1`. Additive changes are non-breaking; anything else needs `/v2`. Deprecation: `deprecated: true`, `Deprecation` and `Sunset` headers, at least one release cycle of overlap.

## Naming and shape
Plural kebab-case resources (computations are named as actions, e.g. `/v1/calculations/partial-purchase`), `camelCase` JSON, ISO-8601 UTC timestamps, **decimals always as JSON strings** (so generated clients never use floating point), stable error codes in ProblemDetails `type`. All JSON fields are camelCase, including the calculator and staleness fields.

## Errors
304 not modified (ETag), 400 validation, 401/403 auth, 404 not found, 409 conflict/concurrent duplicate, 412 `If-Match` mismatch, 422 idempotency-key payload mismatch, 428 `If-Match` required, 429 rate limit, 503 dependency unavailable (with `Retry-After`), other 5xx never leak internals; every error carries a correlation id. A replayed idempotent response carries `Idempotent-Replayed: true`. Pure computations such as the calculator are exempt from `Idempotency-Key`.

## Pipeline order (Host.Api)
`UseSakerWeb()` (correlation id, exception handler, status-code pages) first, then routing, authentication/authorization, then `UseSakerRateLimiting()` and the endpoints. The rate limiter must follow authentication so callers are partitioned by user id (client IP only for anonymous callers) and must follow routing so per-endpoint policies and `DisableRateLimiting` (health endpoints) apply. Behind Caddy the real client IP requires forwarded-header handling, which the API host adds when it is composed.
Problem responses use `type = urn:sakerwatch:error:<code>` plus `code`, `correlationId`, `traceId` and, for validation, `errors`; unexpected failures never include their message.

## Reference endpoints (milestone 1.5)
- `GET /v1/me`: the caller's profile; creates the account on first sight.
- `PUT /v1/me/preferences`: sets the UI locale (`es-GT` or `en-US`); the worked example of a mutating endpoint. It calls `.RequireIdempotencyKey()`, which adds the required `Idempotency-Key` header to the OpenAPI operation. `Contract.Tests` fail for any mutating endpoint that neither calls `RequireIdempotencyKey()` nor `ExemptFromIdempotencyKey(reason)` and for one that does not document 400, 409 and 422.

## Idempotency in code (ADR-0017, ADR-0022)
A command that creates or triggers work implements `IIdempotentRequest`; its endpoint builds the key with `IdempotencyKeyReader.Require(httpContext)`, which returns `400` with `idempotency.key_required` or `idempotency.key_invalid` when the header is missing or not 16-64 characters of letters, digits, `-` or `_`. The key is scoped by user, HTTP method and route template, and recorded in the same transaction as the effect. A repeated key with the same request replays the first successful result with `Idempotent-Replayed: true` and does not run the handler again; a different request under the same key returns `422` (`idempotency.key_reuse`); a duplicate that arrives while the first is still running waits for it and, if it cannot get the lock in time, returns `409` (`idempotency.in_progress`) with `Retry-After: 1`. Only successes are stored: a failure rolls back and leaves no key, so the client may retry. A first response larger than 64 KB is not stored and its replay returns `409` (`idempotency.replay_unavailable`); fetch the resource with `GET`.
