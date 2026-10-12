## What / why

## Checklist
- [ ] Builds with warnings-as-errors; `dotnet format --verify-no-changes` clean
- [ ] Tests added/updated (unit, integration, architecture as relevant)
- [ ] **Security:** authz default-deny, input validation, no secrets/PII in logs, SSRF allow-list respected
- [ ] **Idempotency:** retried paths/jobs/handlers are re-runnable; deterministic identity
- [ ] **Migration:** forward-only, tested from empty and previous version, RLS for new user-owned tables
- [ ] Observability (logs/metrics/traces) added
- [ ] ADR added/updated if a decision changed; `docs/DOCS-INDEX.md` statuses advanced
