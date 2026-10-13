# API guidelines

- **Purpose:** conventions every endpoint must follow; the OpenAPI contract is generated from code (ADR-0014).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1

## Contract
- OpenAPI 3.x per major version; committed at `docs/api/openapi.v1.json`; CI fails on unreviewed diffs and on breaking changes.
- Swagger UI per version (dev; protected or disabled elsewhere).

## Every endpoint must declare
`WithName`, `WithSummary`, `WithDescription`, `WithTags`, typed `Produces<T>` for every status code, `ProducesProblem` (RFC 9457) for errors, auth requirement, request/response examples, and `Idempotency-Key`/pagination/ETag behavior where relevant.

## Versioning
URL segment `/v1`. Additive changes are non-breaking; anything else needs `/v2`. Deprecation: `deprecated: true`, `Deprecation` and `Sunset` headers, at least one release cycle of overlap.

## Naming and shape
Plural kebab-case resources (computations are named as actions, e.g. `/v1/calculations/partial-purchase`), `camelCase` JSON, ISO-8601 UTC timestamps, **decimals always as JSON strings** (so generated clients never use floating point), stable error codes in ProblemDetails `type`. All JSON fields are camelCase, including the calculator and staleness fields.

## Errors
304 not modified (ETag), 400 validation, 401/403 auth, 404 not found, 409 conflict/concurrent duplicate, 412 `If-Match` mismatch, 422 idempotency-key payload mismatch, 428 `If-Match` required, 429 rate limit, 503 dependency unavailable (with `Retry-After`), other 5xx never leak internals; every error carries a correlation id. A replayed idempotent response carries `Idempotent-Replayed: true`. Pure computations such as the calculator are exempt from `Idempotency-Key`.

## Pipeline order (Host.Api)
`UseSakerWeb()` (correlation id, exception handler, status-code pages) first, then routing, authentication/authorization, then `UseSakerRateLimiting()` and the endpoints. The rate limiter must follow authentication so callers are partitioned by user id (client IP only for anonymous callers) and must follow routing so per-endpoint policies and `DisableRateLimiting` (health endpoints) apply. Behind Caddy the real client IP requires forwarded-header handling, which the API host adds when it is composed.
Problem responses use `type = urn:sakerwatch:error:<code>` plus `code`, `correlationId`, `traceId` and, for validation, `errors`; unexpected failures never include their message.
