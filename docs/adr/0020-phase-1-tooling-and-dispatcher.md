# ADR-0020: Phase 1 tooling — in-house dispatcher and verified package versions

- **Status:** accepted
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
Phase 1 introduces the use-case pipeline (ADR-0013, ADR-0016, ADR-0017) and many third-party packages whose versions were never verified (assumption A-011, ADR-0014 asks for re-verification at Phase 1 start). MediatR is the usual dispatcher, but recent versions moved to a commercial license, which does not fit a personal project that must stay free of license obligations.

## Decision
- **In-house dispatcher.** `BuildingBlocks` ships a small request dispatcher (`ISender`, `ICommand<T>`/`IQuery<T>`, `IRequestHandler`) and ordered pipeline behaviors (tracing span `<Module>.<UseCase>`, logging, validation, idempotency, unit of work with outbox). Handlers are resolved from DI; there is no reflection scanning beyond handler registration per assembly. No runtime dependency on MediatR or similar libraries.
- **Native UUIDv7.** .NET 10 provides `Guid.CreateVersion7(DateTimeOffset)`; no third-party UUIDv7 package. `Guid.NewGuid()` stays banned (`BannedSymbols.txt`); IDs are created from the injected `TimeProvider`.
- **Package versions are verified against nuget.org before use** and added to `Directory.Packages.props` only in the milestone that uses them. Versions verified on 2026-10-12 (latest stable):

| Package | Version | Planned milestone |
|---|---|---|
| Microsoft.EntityFrameworkCore | 10.0.12 | 1.2 |
| Npgsql / Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | 1.2 |
| MongoDB.Driver | 3.12.0 | 1.2 |
| StackExchange.Redis | 3.4.0 | 1.2 |
| Polly (v8) | 8.8.0 | 1.1 |
| FluentValidation | 12.2.0 | 1.1 |
| Serilog.AspNetCore / Serilog.Sinks.Console | 10.0.0 / 6.1.1 | 1.1 |
| OpenTelemetry.Extensions.Hosting / Instrumentation.AspNetCore | 1.19.1 / 1.19.0 | 1.1 |
| Asp.Versioning.Http | 10.2.3 | 1.5 |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | 1.5 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 | 1.4 |
| Swashbuckle.AspNetCore (Swagger UI only) | 10.3.0 | 1.5 |
| Testcontainers (PostgreSql, MongoDb, Redis) | 4.16.0 | 1.0 (PostgreSql), 1.2 |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | 1.1 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | 1.5 |

- **Integration tests use real engines through Testcontainers** (Postgres + TimescaleDB; the image is read from `deploy/docker-compose.yml` at run time, so image versions live in one place). They carry `[Trait("Category", "Integration")]` so they can be filtered, and they need Docker.
- **Test projects are added when they get their first real test**, not as empty shells: `Integration.Tests` now (smoke test proving the container toolchain), `Application.Tests` in milestone 1.1, `Contract.Tests` in milestone 1.5. Structural rules that need types that do not exist yet (aggregates without public setters, endpoint documentation) land with those types, so they are never vacuous.

## Consequences
A little more code to own (the dispatcher is small, roughly a few hundred lines with tests) in exchange for no license risk and full control of pipeline order. Package upgrades go through Dependabot and the same CI gates. The Docker requirement applies to the full test run, locally and in CI.
