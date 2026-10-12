# Runbook — SonarCloud and merge-to-main quality gates

- **Purpose:** one-time setup of SonarCloud and the branch rules that keep `main` clean (ADR-0019).
- **Owner:** Eder · **Status:** draft · **Target phase:** 1

## A. SonarCloud (web, one time)
1. Sign in at https://sonarcloud.io with the GitHub account, **Import an organization** from GitHub (`RedeArmy`) and authorize the SonarCloud GitHub app on the repository `saker-watch-app`.
2. Choose a plan. Public repositories are free; for a **private** repository check the current free-tier limits (lines of code) before importing, because private analysis may need a paid plan.
3. **Analyze new project** → select `saker-watch-app` → set the new-code definition to **Previous version** (or **Reference branch: main**) → **With GitHub Actions**. Note the **organization key** and **project key** shown on that page.
4. **Administration → Analysis Method:** turn **Automatic Analysis OFF** (CI analysis is used because it provides coverage; both at once makes the scan fail).
5. **My Account → Security:** generate a token (name it `saker-watch-ci`); copy it once.
6. **Quality Gate:** keep the built-in *Sonar way* (new code: no new bugs/vulnerabilities, hotspots reviewed, coverage >= 80 %, duplication <= 3 %) or create a stricter copy and assign it to the project.

## B. GitHub repository settings
1. **Settings → Secrets and variables → Actions → Secrets:** add `SONAR_TOKEN` (the token from A.5).
2. **Variables tab:** add `SONAR_ORGANIZATION` and `SONAR_PROJECT_KEY` (from A.3).
3. **Settings → Code security:** enable Dependabot alerts and security updates. Enable *Code scanning* only if the repository is public or has Advanced Security (otherwise CodeQL keeps its SARIF as an artifact, assumption A-018). Enable *Secret scanning* and *Push protection* (free on public repositories).
4. **Settings → Branches (or Rulesets) → protect `main`:**
   - Require a pull request before merging (approvals: 0, because of the solo developer, A-015).
   - Require status checks to pass and require the branch to be up to date. Select these checks: `docs-and-config`, `secrets`, `dotnet`, `sonarcloud`, `codeql`, `trivy`, and the SonarCloud app status `SonarCloud Code Analysis`.
   - Require linear history; block force pushes and deletions; require conversation resolution.
   - Optionally require signed commits.

## C. First run checklist
- Open the first PR: the `sonarcloud` job should finish and SonarCloud decorates the PR with the Quality Gate result.
- If `sonarcloud` shows "skipped", the `SONAR_TOKEN` secret is missing or the PR comes from a fork.
- If the scanner reports "automatic analysis is enabled", repeat A.4.
- Findings appear under the project in SonarCloud; fix them or mark false positives with a justification (never disable a rule globally without an ADR note).

## What SonarCloud validates here
C# bugs, vulnerabilities, code smells and complexity; security hotspots; duplication; coverage on new code (OpenCover from coverlet); secrets; IaC and GitHub Actions workflow files. Formatting stays enforced locally and in CI by `dotnet format --verify-no-changes` and the analyzers in the build (warnings are errors), so the Sonar analyzers also run inside every local build.
