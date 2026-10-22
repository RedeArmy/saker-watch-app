# Runbook — restore

- **Purpose:** operational procedure for restore.
- **Owner:** Eder · **Status:** stub · **Target phase:** 5

## What is already decided (the skeleton Phase 5 completes)
- Migrations are forward-only (ADR-0004, ADR-0021): the ways back from a bad deploy are a corrective forward migration, or a restore of a backup. There is no `Down`.
- **Before the migrator runs in a deployment, take a backup** of PostgreSQL (and note the MongoDB state), and keep it until the new version has run for a full day. The migrator is its own deploy step; the API and the worker never migrate.
- **The previous image must keep working against the new schema.** Every migration of Phase 2 only adds (tables, columns with defaults, functions, indexes, checks), so the API and the worker of the previous version run on it; a migration that is not additive (a drop, a rename, a new NOT NULL without a default) follows expand and contract over two deploys.
- If the migrator fails half way, nothing is applied by the failed step (each EF migration runs in its own transaction, and the Mongo runner is idempotent and takes a lock); fix the cause and run it again, it applies only what is missing.
- Redis holds no state that matters (caches and budgets): after a restore it is simply emptied.
- The detailed steps, the drill and its evidence are the Phase 5 gate (`docs/roadmap.md`).

