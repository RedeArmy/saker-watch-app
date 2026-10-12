# ADR-0003: Modular monolith with hexagonal modules

- **Status:** accepted
- **Date:** 2026-10-12

## Context
One developer, one user today, multi-user later; Oracle Always Free budget. Microservices would add operational cost with no benefit.

## Decision
Modular monolith: modules `Identity, MarketData, Watchlist, Analysis, Signals, Calculator, Retention`, each with `Domain/Application/Infrastructure/Contracts`; vertical slices in Application. Modules talk only via other modules' `Contracts` and in-process events. Two deployables from one codebase: `Host.Api`, `Host.Worker`. Schema-per-module in Postgres.

## Consequences
Boundaries enforced by NetArchTest (no cross-module DB access, no `DbContext` outside Infrastructure). A module can be extracted later because callers depend only on Contracts.
