# ADR-0046: The pre-authentication limiter is charged only for requests that authentication refuses

- **Status:** accepted
- **Date:** 2026-10-22
- **Deciders:** Eder

## Context
Two limiters protect the API (threat T5, `docs/api/README.md`): one per client address that runs before authentication, so a flood of missing or invalid tokens is stopped before any signature is validated, and one per user after it. The first took one permit for every request that reached it, authenticated or not, from a bucket of `PreAuthenticationPermitLimit` (600 a minute) per address.

The review of Phase 2 found the cost of that: the only client of the API is the Next.js BFF (ADR-0009), and until `ForwardedHeaders` names the proxy network (A-023) every user's request arrives from the same address. All users then shared one bucket of 600 a minute, and valid requests got `429` before authentication ran, so the per-user limit that the design relies on was never the one that applied. A dashboard that fans out a few calls per user would reach it with a handful of users.

## Options considered
- **Keep it and size it for the BFF:** raise the default to a large value. The number would be a guess, would still be one bucket for everyone, and would weaken the one thing the limiter is for.
- **Key the bucket by a hash of the token:** each token gets its own allowance. A flood of random tokens then creates a bucket for each one, which is no limit at all and grows memory.
- **Charge only requests that authentication refuses:** the limiter is checked first without using a permit; a request that ends in `401` takes one. Valid traffic never uses the allowance, and what the allowance was for (invalid and missing tokens) is what it counts.
- **Authenticate first and limit by user only:** the signature validation that the pre-authentication limiter exists to avoid would run for every flooded request.

## Decision
The limiter before authentication is checked without taking a permit (a request is turned away with `429` when the address has none left) and takes one permit after the pipeline when the response is `401`. The per-user limiter after authentication is unchanged and also applies to anonymous callers by address.

## Consequences
- A flood of invalid or missing tokens is still stopped after `PreAuthenticationPermitLimit` refusals in a window, before any further signature is validated.
- Users that share an address (the BFF, or the proxy before A-023 is closed) no longer use each other's allowance.
- Anonymous endpoints that answer without `401` are not counted before authentication; they are still limited by address by the limiter after it (`PermitLimit`).
- Concurrent requests that arrive together can each pass the check before any is charged, so the allowance can be exceeded by the number of requests in flight; the bound is the concurrency of one address, not a leak.
- Changes the statement of threat T5 and `docs/api/README.md`, updated with this ADR.
