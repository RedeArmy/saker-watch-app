# Runbook — SonarCloud and merge-to-main quality gates

- **Purpose:** one-time setup of SonarCloud and the branch rules that keep `main` clean (ADR-0019).
- **Owner:** Eder · **Status:** approved · **Target phase:** 1

## A. SonarCloud (web, one time)
1. Sign in at https://sonarcloud.io with the GitHub account, **Import an organization** from GitHub (`RedeArmy`) and authorize the SonarCloud GitHub app on the repository `saker-watch-app`.
2. Choose a plan. Public repositories are free; for a **private** repository check the current free-tier limits (lines of code) before importing, because private analysis may need a paid plan.
3. **Analyze new project** → select `saker-watch-app` → set the new-code definition to **Previous version** (or **Reference branch: main**) → **With GitHub Actions**. Note the **organization key** and **project key** shown on that page.
4. **Administration → Analysis Method:** turn **Automatic Analysis OFF** (CI analysis is used because it provides coverage; both at once makes the scan fail).
5. **My Account → Security:** generate a token (name it `saker-watch-ci`); copy it once.
6. **Duplications:** *Administration → General Settings → Analysis Scope → Duplications Exclusions*: add `**/Migrations/**`. EF migrations are generated and each one repeats the shape of the others (a `Designer` file repeats the model snapshot), so Sonar reports them as duplicated code that no one can reasonably refactor. The same exclusion is also passed by the CI scanner (`/d:sonar.cpd.exclusions` in the `sonarcloud` job), so a scan does not depend on this setting having been made; the web setting keeps the project page consistent.
7. **Quality Gate:** keep the built-in *Sonar way* (new code: no new bugs/vulnerabilities, hotspots reviewed, coverage >= 80 %, duplication <= 3 %) or create a stricter copy and assign it to the project.

## B. GitHub repository settings
1. **Settings → Secrets and variables → Actions → Secrets:** add `SONAR_TOKEN` (the token from A.5). **Add the same secret under the *Dependabot* tab** too: Dependabot-triggered runs cannot read the Actions secret store, so without it every Dependabot PR fails the `sonarcloud` job (by design, see below).
2. **Variables tab:** add `SONAR_ORGANIZATION` and `SONAR_PROJECT_KEY` (from A.3).
3. **Settings → Code security:** enable Dependabot alerts and security updates. Enable *Code scanning* only if the repository is public or has Advanced Security (otherwise CodeQL keeps its SARIF as an artifact, assumption A-018). Enable *Secret scanning* and *Push protection* (free on public repositories).
4. **Protect `main` with a Ruleset** (Settings → Rules → Rulesets); see section D for the exact steps and the importable file `docs/runbooks/github-ruleset-main.json`.

## C. First run checklist
- Open the first PR: the `sonarcloud` job should finish and SonarCloud decorates the PR with the Quality Gate result.
- If `sonarcloud` fails at "Require SONAR_TOKEN", the secret is missing from the Actions or the Dependabot store. It is only skipped for PRs from forks.
- If the scanner reports "automatic analysis is enabled", repeat A.4.
- Findings appear under the project in SonarCloud; fix them or mark false positives with a justification (never disable a rule globally without an ADR note).

## What the scanner is told about the Python and SQL files
Two warnings of the scanner are answered in the `sonarcloud` job of the workflow. The Python tests of the spike harness (`scripts/**/test_*.py`) are declared with `sonar.test.inclusions`, so that the rules for production code are not run on them. The PL/SQL analyzer is switched off (`sonar.plsql.file.suffixes=-`): the only `.sql` files are the seed of the load test and none of them is code that ships, and the analyzer needs a data dictionary the project does not have; the SQL of the migrations lives in C# strings and was never analyzed by it.

## What SonarCloud validates here
C# bugs, vulnerabilities, code smells and complexity; security hotspots; duplication; coverage on new code (OpenCover from coverlet); secrets; IaC and GitHub Actions workflow files. Formatting stays enforced locally and in CI by `dotnet format --verify-no-changes` and the analyzers in the build (warnings are errors), so the Sonar analyzers also run inside every local build.

## D. Ruleset for `main` (GitHub)
File: `docs/runbooks/github-ruleset-main.json` (name `protect-main`, target: default branch).

**Order matters.** A ruleset that requires checks blocks every merge until those checks have run and passed.
1. Get the first pull request green (sections A–C), merge it.
2. Then import the ruleset: **Settings → Rules → Rulesets → New ruleset → Import a ruleset**, pick the JSON file, review it, **Create**. (Alternatively create it by hand with the same rules.)
3. Open the next small pull request so the SonarCloud app reports its status; then **edit the ruleset** and add the required check `SonarCloud Code Analysis` (it only appears in the picker after it has reported once).

What the ruleset enforces on the default branch:
| Rule | Effect |
|---|---|
| Restrict deletions / block force pushes | `main` cannot be deleted or rewritten |
| Require linear history | no merge commits; use squash or rebase |
| Require a pull request | all changes arrive through a PR; 0 approvals (solo developer, A-015); stale approvals dismissed on push; conversations must be resolved; merge methods: squash or rebase |
| Require status checks | `docs-and-config`, `api-contract`, `secrets`, `dotnet`, `sonarcloud`, `codeql`, `trivy`; branch must be up to date (strict) |
| Bypass list | empty on purpose: nobody, including the owner, skips the rules |

Notes:
- A *skipped* required job reports as passed, so `sonarcloud` fails loudly when `SONAR_TOKEN` is missing instead of skipping (fork PRs are the only exception). Dependabot PRs are covered by the Dependabot copy of the secret (B.1).
- Emergency exit: temporarily set the ruleset to *Disabled* (Settings → Rules), fix, and re-enable; or add yourself under **Bypass list** with mode *Pull request only* if you want a documented escape hatch.
- Optional extras (not enabled): *Require signed commits*; a second ruleset on tags `v*` (block deletion and force update) once releases start; *Require deployments to succeed* once a deploy environment exists. Code-scanning results as a merge rule need a public repository or Advanced Security (A-018).
- Each required check is pinned to the GitHub Actions app (`integration_id` 15368), so no other app or API call can post a fake green status under the same name.
- `docs/runbooks/github-ruleset-tags.json` (`protect-release-tags`) makes `v*` tags immutable; import it the same way before the first release.
- Required check names are the workflow job ids in `.github/workflows/ci.yml`; renaming a job requires updating the ruleset. `scripts/check-ruleset.py` (a step of the `docs-and-config` job) fails when the ruleset requires a check that no job reports, so the file cannot drift from the workflow unseen. It was checked on 2026-10-23: the seven required checks (`docs-and-config`, `api-contract`, `secrets`, `dotnet`, `sonarcloud`, `codeql`, `trivy`) are the seven jobs. Importing the file into GitHub is still done by hand (section D, step 2), and so is adding `SONAR_TOKEN` to the Dependabot secret store (B.1).

