# Code standards the build enforces

- **Purpose:** what the compiler, the analyzers and the checks of this repository refuse, and the limits that are guidelines kept by review; for anyone who changes `src/**` or `tests/**`. The decisions behind them are ADR-0013 (engineering standards), ADR-0015 (documentation and comments) and ADR-0016 (observability).
- **Owner:** Eder · **Status:** draft · **Target phase:** 2

## What fails the build (`dotnet build -warnaserror`)
| Rule | Where it lives |
|---|---|
| Warnings are errors; nullable reference types on | `Directory.Build.props` |
| XML documentation on the public surface of `src/**` (CS1591) | `Directory.Build.props` |
| Banned APIs in `src/**`: `Console.Write*`, `DateTime(Offset).Now/UtcNow` (inject `TimeProvider`), `Thread.Sleep`, `Guid.NewGuid()`, `Guid.CreateVersion7()` without a clock, `new Meter(name)` and `new ActivitySource(name)` outside `SakerTelemetry` | `BannedSymbols.txt`, `RS0030` as an error in `.editorconfig` |
| Log message templates only, never interpolation (`CA2254`) | `.editorconfig` |
| A method of `src/**` has at most 40 lines of code (Sonar `S138`); a method that needs more carries `[SuppressMessage("Major Code Smell", "S138:...", Justification = "...")]`, and `grep -rn S138 src` lists them (the list may only shrink). EF migrations are exempt | `.editorconfig`, `SonarLint.xml` |
| At most 7 parameters (Sonar `S107`, the threshold of the CI quality profile) | `.editorconfig` |
| A cognitive complexity of at most 15 per method (Sonar `S3776`, the threshold of the CI quality profile) | `.editorconfig` |
| A string literal repeated is a constant (`S1192`) | `.editorconfig` |
| Formatting (`dotnet format --verify-no-changes`) | CI `dotnet` job |

## What fails a check
| Rule | Check |
|---|---|
| `TODO(owner: name[, ref]): text` is the only form; `HACK` and `FIXME` need the ref | `scripts/check-todos.sh` |
| Every document and script is listed in `docs/DOCS-INDEX.md` with a status | `scripts/check-docs-index.sh` |
| Module dependencies point inward and modules talk through `Contracts` only | `tests/Architecture.Tests` |
| Every code a client can receive is in `docs/api/errors.md`, and the page lists no code the source lacks | `tests/Contract.Tests/ErrorCatalogueTests.cs` |
| The OpenAPI document equals the committed `docs/api/openapi.v1.json`; a breaking change fails the pull request | `tests/Contract.Tests`, CI `api-contract` job |
| Line coverage of the Domain and Application assemblies is at least 85 % | `scripts/check-coverage.py` |

## Guidelines kept by review
- A type has at most about 400 lines, and a method takes at most 5 parameters; the build only refuses more than 7, because the factories and constructors of aggregates and value objects legitimately take more.
- Money, exchange rates and quantities are `decimal`, never `double` or `float`; rounding happens only at the presentation boundary (ADR-0006).
- Time is injected (`TimeProvider`); every I/O method takes a `CancellationToken`.
- Expected failures are `Result` values; exceptions are for the unexpected.
- Test names are `Method_Scenario_Expected`; assertions use AwesomeAssertions; no real clocks or sleeps in tests.
- Inline comments explain why, not what; no commented-out code.
