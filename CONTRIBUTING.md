# Contributing

- Trunk-based; short-lived branches; Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`), one logical change per commit.
- Every change must build with warnings-as-errors, pass `dotnet format --verify-no-changes`, tests, and `make check-docs`.
- Significant decisions need an ADR (copy `docs/templates/adr-template.md` to the next free number). Update `docs/DOCS-INDEX.md` for any doc you touch.
- Never commit secrets; gitleaks runs as pre-commit and in CI.
