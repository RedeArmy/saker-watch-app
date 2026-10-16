# ADR-0025: Audit log retention and the `Down` of applied migrations

- **Status:** proposed
- **Date:** 2026-10-16
- **Deciders:** Eder

## Context
`platform.audit_log` is append-only and hash-chained (`append_audit`, `verify_audit_chain`). No retention path exists: the table grows for ever, and the retention module deliberately does not touch it. Deleting rows breaks the chain unless a checkpoint records the last purged hash.

Separately, the `Down` of two applied migrations (`CreatePlatformTables`, `CreateUsers`) drops tables, while newer migrations throw `NotSupportedException` (forward-only, ADR-0021). An applied migration must not be edited without the owner's consent.

## Options considered
1. **Keep for ever.** Simplest, chain stays verifiable; volume is small for one user (a few rows per mutation).
2. **Hypertable on `created_at` with `drop_chunks`.** Needs a composite key (`id, created_at`), a rewrite of a live table and a chain checkpoint row so verification can start after the purge.
3. **Archive to cold storage, then purge.** Off-database anchor (also a Phase 5 backlog item); largest effort.

For `Down`: (a) leave as is; (b) change both to throw plus a reflection test that every `Down` throws (requires editing applied migration files, content only, no schema effect).

## Decision
Proposed, pending the owner: option 1 until the table measurably matters, revisit with the off-database anchor in Phase 5 (option 3). For `Down`, option (b), with the test. Nothing is implemented by this ADR.

## Consequences
- Audit storage grows linearly; add a size gauge to the Phase 5 dashboards.
- If option 2 or 3 is chosen later, a new ADR supersedes this one and the migration is forward-only with a verified checkpoint.
- Editing the two `Down` methods changes migration file checksums only in the repository, not in applied databases (EF stores the id, not a hash); the owner decides.
