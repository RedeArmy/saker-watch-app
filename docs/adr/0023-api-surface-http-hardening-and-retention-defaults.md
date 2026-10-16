# ADR-0023: API surface, HTTP hardening and retention defaults

- **Status:** accepted
- **Date:** 2026-10-15
- **Deciders:** Eder

## Context
Milestone 1.5 opens the first public surface of the API (`/v1/me`, an idempotent `PUT`), puts a reverse proxy in front of it (Caddy) and adds the first data-trimming job. ADR-0014 fixes OpenAPI and versioning in principle; ADR-0009 makes the Next.js BFF the only client; ADR-0017 defines idempotency. This ADR records the choices those leave open, each verified in code and tests.

## Options considered
- **Browser access:** (a) CORS open to listed origins so a SPA can call the API; (b) CORS closed, BFF server to server.
- **Forwarded headers:** (a) trust `X-Forwarded-*` from anywhere; (b) trust them only from configured proxies, one hop; (c) ignore them and rate-limit by the proxy address.
- **API description exposure:** (a) always public; (b) public only in Development or by flag; (c) never served.
- **Contract enforcement:** (a) convention in documentation; (b) tests that fail on any endpoint missing version, metadata, documented responses or an idempotency declaration, plus a committed OpenAPI file that must match the code, plus a breaking-change detector in CI.
- **Retention default:** (a) delete from the first run; (b) dry run until an operator turns deletion on.

## Decision
- **CORS is closed** (1a rejected). `Cors:AllowedOrigins` is an explicit exception list of exact origins (https, http only for localhost), validated at startup; no wildcard, path or credentials.
- **Forwarded headers are trusted only from `ForwardedHeaders:KnownProxies` / `KnownNetworks`** (loopback when unset), one hop deep. The proxy network is a deployment setting (A-023).
- **The OpenAPI document needs a token** except in Development or with `OpenApi:Anonymous=true`; Swagger UI exists in Development only. The document pins no server URL.
- **The contract is enforced by tests and CI:** `Contract.Tests` fail when `docs/api/openapi.v1.json` differs from the generated document and when an endpoint lacks a versioned route, name, summary, description, tags, a documented success, 401 and 500, or (if it mutates) `RequireIdempotencyKey()` / `ExemptFromIdempotencyKey(reason)` with 400, 409 and 422 documented. CI compares the contract with the base branch using `oasdiff` (pinned by digest) and fails on breaking changes.
- **Every response carries the security headers**, `Cache-Control: no-store` unless the endpoint decides otherwise, HSTS outside Development, no `Server` header, and request bodies are capped at 1 MiB.
- **Retention starts in dry-run mode** and deletes only finished data (expired keys, processed outbox and inbox, succeeded or dead jobs), in bounded batches, once per UTC day through the job queue.

## Consequences
- A deployment that forgets `ForwardedHeaders` still works but rate-limits anonymous callers by the proxy's address; this is a documented, visible degradation rather than a spoofable one.
- Changing the API means regenerating and reviewing `docs/api/openapi.v1.json` in the same pull request; additive changes pass the breaking check, removals need `/v2` or a deprecation cycle.
- A SPA calling the API directly would need an explicit CORS exception and a new decision.
- The first production run of Retention changes nothing until `Retention:DryRun=false` is set after reading a dry run; inbox deletes do not lock candidate rows because the worker role lacks `UPDATE` there (the result is still exact).
