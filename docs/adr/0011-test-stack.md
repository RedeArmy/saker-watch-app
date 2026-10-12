# ADR-0011: Test stack

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
xUnit, NSubstitute, **AwesomeAssertions** (avoids FluentAssertions v8+ licensing), Testcontainers, NetArchTest.Rules, FsCheck, WireMock.Net/recorded fixtures, Playwright (web), k6 (load), optional Stryker.NET. Coverage gate ≥ 85% on Domain/Application.

## Test container images
Testcontainers image names are overridable through environment variables (`SAKER_TEST_MONGO_IMAGE`, default `mongo:7.0`; `SAKER_TEST_POSTGRES_IMAGE`, `SAKER_TEST_REDIS_IMAGE`). Machines without AVX set `SAKER_TEST_MONGO_IMAGE=mongo:4.4` (assumption A-013); code exercised by tests must avoid Mongo features newer than 4.4 or be skipped via a documented trait.
