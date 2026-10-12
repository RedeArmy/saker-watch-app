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
Plural kebab-case resources, `camelCase` JSON, ISO-8601 UTC timestamps, decimals as strings or exact JSON numbers (never floating point math), stable error codes in ProblemDetails `type`.

## Errors
400 validation, 401/403 auth, 404 not found, 409 conflict/concurrent duplicate, 422 idempotency-key payload mismatch, 429 rate limit, 5xx never leaks internals; every error carries a correlation id.
