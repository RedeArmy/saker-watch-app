# ADR-0014: API contract — OpenAPI, Swagger documentation and versioning

- **Status:** accepted (packages verified 2026-10-14: `Asp.Versioning.Http`/`Asp.Versioning.OpenApi` 10.2.3, `Microsoft.AspNetCore.OpenApi` 10.0.12, `Swashbuckle.AspNetCore.SwaggerUI` 10.2.3 for the UI only)
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
The API must be fully documented, discoverable, versioned and safe to evolve. OpenAPI is the contract; the frontend client is generated from it (ADR-0007).

## Decision
- **Contract-first discipline, code-generated document:** Minimal APIs annotated with endpoint metadata (`WithName`, `WithSummary`, `WithDescription`, `WithTags`, `Produces<T>`, `ProducesProblem`, `Accepts`) feed the OpenAPI 3.x document (built-in `Microsoft.AspNetCore.OpenApi`; Swagger UI served by a Swagger UI package — final package verified for .NET 10 at Phase 1).
- **Every endpoint documents:** summary, description, request/response schemas with examples, all status codes (including 400/401/403/404/409/422/429/5xx as ProblemDetails RFC 9457), auth requirement, `Idempotency-Key` header where applicable, pagination and ETag semantics.
- **XML documentation comments** on DTOs and endpoint handlers are included in the OpenAPI schema descriptions.
- **Versioning:** URL segment `/v1/...` using `Asp.Versioning` (verified at Phase 1). One OpenAPI document and Swagger UI page per version. Rules: additive changes are non-breaking within a version; removing/renaming fields, changing types or semantics requires a new major version. Deprecation uses the `Deprecation`/`Sunset` headers and `deprecated: true` in the document; a deprecated version is supported for at least one release cycle.
- **Swagger UI exposure:** enabled in dev and behind authentication or disabled in deployed environments (decision recorded per environment); never exposes secrets or internal-only endpoints.
- **Contract safety:** the generated OpenAPI file is committed at `docs/api/openapi.v1.json`; CI regenerates it and fails on unreviewed diff; a breaking-change detector (e.g. oasdiff) runs against the previous version in CI.
- **Docs:** `docs/api/README.md` holds guidelines (naming, pagination, errors, idempotency, versioning policy, deprecation policy).

## Consequences
Extra metadata per endpoint, enforced by an architecture/lint test that every mapped endpoint has summary, tags and documented responses. The frontend client is regenerated from the committed contract.
