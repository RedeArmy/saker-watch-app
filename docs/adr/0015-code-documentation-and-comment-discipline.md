# ADR-0015: Code documentation and comment discipline

- **Status:** accepted
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
Documentation must be complete but not noisy. Comments that restate code rot; missing comments on contracts hurt consumers.

## Decision
**Language:** English for code, comments and XML docs.

**XML documentation (`///`) is mandatory for:** every public/protected type and member in `*.Contracts`, every public type in `Domain` and `Application` (aggregates, value objects, domain events, ports/interfaces, handlers, validators), every endpoint handler and DTO. Compiler enforcement: `GenerateDocumentationFile=true` and CS1591 as an error for `src/**` (non-public and test code excluded).
Contents: `<summary>` states *what and why* in one or two sentences; `<param>`, `<returns>`, `<exception>` where they add information; `<remarks>` for invariants, thread-safety, idempotency and side effects; `<example>` for non-obvious APIs.

**Inline comments explain *why*, never *what*.** Allowed: business rules and their source, non-obvious trade-offs, workarounds with a link/ID, performance reasoning, security reasoning, units and rounding rules. Forbidden: restating the code, changelog/author comments, commented-out code, banner separators.

**Markers:** `TODO(owner: <name>, <ticket|ADR>): <what>` is the only accepted TODO form; `HACK` and `FIXME` need a linked issue. CI fails on bare `TODO` without owner.

**Invariants and contracts** are documented on the type that enforces them. Domain terms follow the glossary. Non-trivial algorithms (performance windows, rounding, signal rules) carry a short derivation comment and are linked from the relevant ADR.

**Architecture/design docs** live in `docs/`; code comments never replace ADRs. Public docs and comments are reviewed in code review like code.

## Consequences
Slightly more effort per type; zero tolerance for undocumented public surface in Contracts/Domain/Application. Test projects and internal plumbing are exempt from XML docs but follow the "why only" inline rule.
