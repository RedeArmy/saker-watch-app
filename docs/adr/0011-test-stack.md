# ADR-0011: Test stack

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
xUnit, NSubstitute, **AwesomeAssertions** (avoids FluentAssertions v8+ licensing), Testcontainers, NetArchTest.Rules, FsCheck, WireMock.Net/recorded fixtures, Playwright (web), k6 (load), optional Stryker.NET. Coverage gate ≥ 85% on Domain/Application.
