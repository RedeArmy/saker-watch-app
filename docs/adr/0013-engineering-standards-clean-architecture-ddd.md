# ADR-0013: Engineering standards — Clean Architecture, DDD, vertical slices, design patterns

- **Status:** accepted
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
The project targets big-tech engineering quality. ADR-0003 fixed the macro style (modular monolith, hexagonal modules). This ADR fixes the in-module standards so every module is built the same way.

## Decision
**Clean Architecture per module** (dependencies point inward):
`Domain` ← `Application` ← `Infrastructure`; `Contracts` is the module's public surface. Hosts are composition roots only. Enforced by architecture tests (NetArchTest).

**Domain-Driven Design (tactical, per bounded context = module):**
- Aggregates with a single root; invariants enforced inside the aggregate; no public setters; state changes through intention-revealing methods.
- Entities (identity) and Value Objects (immutable, equality by value): `Money`, `Ticker`, `Percentage`, `FxRate`, `ShareFraction`, `UserId`, `StockId`.
- Strongly typed IDs (UUIDv7); no primitive obsession for domain concepts.
- Domain events raised by aggregates, dispatched after commit through the outbox (ADR-0005).
- Domain services only when logic does not belong to one aggregate. Repositories are interfaces in Domain/Application, implemented in Infrastructure, one per aggregate root.
- Ubiquitous language: names in code match `docs/glossary.md` (created when the first module is built).
- Anti-corruption layers around external providers (market data, EDGAR, Clavaris, LLM): provider models never leak into Domain.

**Vertical slices inside Application:** one folder per use case (`Features/<UseCase>/`) containing the command/query, handler, validator, DTOs and endpoint mapping. CQRS-style separation: commands go through aggregates; queries may use read-optimized projections (Dapper allowed for hot reads). No shared "services" god classes.

**Design patterns (use when they remove a real problem, never decoratively):**
Repository, Unit of Work (EF `DbContext` behind an interface), Specification (reusable query rules), Factory (aggregate creation with invariants), Strategy (`ISignalRule`, rounding, providers), Adapter (providers, Clavaris, LLM), Decorator (caching, resilience, logging around ports), Outbox/Inbox, Result pattern, Options pattern, Mediator-style in-process dispatch (a small custom dispatcher; no heavyweight dependency), Pipeline behaviors (validation, idempotency, transactions, logging), Null Object (`FakeLlm`, no-op providers).

**Clean code rules:** small cohesive types and functions; intention-revealing names; no flag arguments; no commented-out code; guard clauses over deep nesting; immutability by default; SOLID applied pragmatically; cyclomatic complexity and file/method length watched by analyzers (soft limits: method ≤ 40 lines, type ≤ 400 lines, parameters ≤ 5 — exceptions need a justifying comment).

## Consequences
More types and ceremony than CRUD-style code; accepted for testability and long-term maintainability. Architecture tests grow with each module (`Domain` has no framework references, `Application` does not reference `Infrastructure`, aggregates have no public setters).
