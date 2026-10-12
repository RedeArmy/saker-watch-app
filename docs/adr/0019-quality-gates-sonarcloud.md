# ADR-0019: Quality gates — SonarCloud plus compiler-enforced analyzers

- **Status:** accepted
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
Everything that can be checked automatically must be green before code reaches `main`. Formatting and basic analyzers already run in CI; we want deeper static analysis, coverage on new code, duplication and security hotspots with a pull-request gate.

## Decision
- **Shift left:** `SonarAnalyzer.CSharp` (same rules as SonarCloud's C# analysis) runs in every build as a global analyzer; with warnings as errors, local builds already reproduce most Sonar findings.
- **SonarCloud** runs in CI through SonarScanner for .NET (`sonarcloud` job): build, tests with coverlet coverage (OpenCover), upload, and `sonar.qualitygate.wait=true` so the job fails when the Quality Gate fails. Automatic Analysis is off.
- **Quality Gate:** Sonar way on new code (no new bugs or vulnerabilities, security hotspots reviewed, coverage >= 80 %, duplication <= 3 %); overall-code issues are fixed as found.
- **Exclusions are minimal and explicit:** `Program.cs` composition roots and `IAssemblyMarker` interfaces are excluded from coverage only; no rule is disabled globally.
- **Merge policy for `main`:** pull request required (no mandatory approvals, solo developer, A-015), all CI checks plus `SonarCloud Code Analysis` required, linear history, no force push. Setup steps: `docs/runbooks/sonarcloud-setup.md`.
- The job is skipped when `SONAR_TOKEN` is unavailable (forks) so that it never fails for infrastructure reasons; the required-check rule covers normal PRs.

## Consequences
Another external service and one more secret to manage. Private-repository analysis may require a paid SonarCloud plan; if that is unacceptable, the fallback is the analyzers-in-build setup alone (already in place).
