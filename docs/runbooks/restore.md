# Runbook — backup and restore

- **Purpose:** how to back up the datastores before a change, restore them into new databases, put the copy in service, and prove that it works (a drill). Written for the compose stack; the deployed host, the schedule, off-host storage and the recovery targets are completed in Phase 5.
- **Owner:** Eder · **Status:** approved · **Target phase:** 5 (the procedure and its drill exist since Phase 2)

## What is backed up
| Store | How | Why |
|---|---|---|
| PostgreSQL (`saker`) | `pg_dump` in custom format: all schemas, the data, the grants, the row-level security policies, the functions and the hypertable definition | the source of truth |
| MongoDB (`saker`) | `mongodump` as one gzip archive | raw provider answers and, later, the analysis documents |
| Redis | not backed up | it holds caches and call budgets only; after a restore it is simply empty |

The backup directory (`backups/<UTC timestamp>/`, mode 700, ignored by git) holds `postgres.dump`, `mongo.archive.gz`, `manifest.json` (time, commit, versions) and `SHA256SUMS`. **It holds all the data of the product: treat it like the databases.**

## Principles (ADR-0004, ADR-0021)
- Migrations are forward-only: the ways back from a bad deploy are a corrective forward migration, or a restore. There is no `Down`.
- **Take a backup before the migrator runs in a deployment**, and keep it until the new version has run for a full day. The migrator is its own step; the API and the worker never migrate.
- The previous image must keep working against the new schema. Every migration of Phase 2 only adds (tables, columns, functions, checks) or widens (`fx_rates.rate`), so the previous API and worker run on it; a migration that drops, renames or adds a NOT NULL without a default follows expand and contract over two deploys.
- If the migrator fails half way, the failed migration did not apply (each EF migration runs in its own transaction, and the Mongo runner is idempotent and takes a lock): fix the cause and run it again; it applies only what is missing.

## 1. Take a backup
```
POSTGRES_PASSWORD=... MONGO_PASSWORD=... bash scripts/dev-backup.sh            # or: ... bash scripts/dev-backup.sh <directory>
```
It checks nothing about the state of the stack: stop the writers (API and worker) first if the copy must be consistent between the two stores. `COMPOSE_PROJECT` and `COMPOSE_EXTRA_FILE` select another stack.

## 2. Restore into new databases
```
POSTGRES_PASSWORD=... MONGO_PASSWORD=... bash scripts/dev-restore.sh <backup-directory> [target-name]     # default target: saker_restored
```
- It verifies the checksums first and **never drops, truncates or overwrites anything**: it refuses to run when the PostgreSQL database already exists or the MongoDB database of that name already has collections (before it creates anything), and it refuses the names `saker`, `postgres` and `admin`.
- PostgreSQL goes to the new database `<target-name>` (TimescaleDB in its restore mode, `api_role` and `worker_role` created if the server lacks them); MongoDB goes to a database of the same name. The login roles (`saker_api`, `saker_worker`, `saker_app`) are cluster objects and are not in the dump: on a fresh server run `scripts/dev-create-logins.sh` afterwards.

## 3. Check the copy, then put it in service
1. Look at it before trusting it: row counts of the tables you care about, `SELECT max(trade_date) FROM marketdata.price_daily`, the migration history (`SELECT * FROM marketdata.__ef_migrations_history`), the grants (`\dp marketdata.*`) and the policies (`SELECT * FROM pg_policies WHERE schemaname = 'watchlist'`).
2. Run the migrator against the copy (`ConnectionStrings__PostgresAdmin` with `Database=<target-name>`): on a good copy it applies nothing.
3. Stop the API and the worker. Then point them at the copy: `ConnectionStrings__Postgres` with `Database=<target-name>` and `Mongo:Database=<target-name>`. For PostgreSQL the alternative, once the operator has decided to give up the old database, is to rename by hand (`ALTER DATABASE saker RENAME TO saker_old; ALTER DATABASE saker_restored RENAME TO saker;`, which needs no open sessions, so the hosts and your own connections must be closed); MongoDB cannot rename a database, so it is always the setting. Keep the old databases until the new ones have run for a day. This step is deliberately not a script.
4. Start the worker, then the API; `/health/ready` must be 200. The performance cache fills itself.

## 4. The drill
`DRILL_NOAVX=1 bash scripts/restore-drill.sh` (leave the variable out on a CPU with AVX) builds a throwaway stack, migrates it, puts data in both stores, backs it up, restores it into a new database and checks that the copy has the same rows (a fingerprint over the catalog, prices, rates and the watchlist), the same migration history, the hypertable, the grants (the API role reads the catalog and cannot read the rejected rates), the row-level security policies, the security-definer functions and the MongoDB collections and documents; that a second restore into the same name, a restore over the live database and a restore into a MongoDB database that already has collections are refused (and the last one creates nothing in PostgreSQL); and that the migrator finds nothing to apply on the copy. It prints `ALL OK`, or one `FAIL` line per check, and removes everything it made except its output directory. Run it after a change to the migrations, to the scripts or to the images of the datastores.

Runs: 2026-10-22, `ALL OK` (the third time with the MongoDB leftover case). Python is not needed; docker and a built solution are.

## Not covered yet (Phase 5)
The deployed host (Oracle arm64): where the backups go and how they are encrypted and kept off the host, the schedule, the retention of backups, the recovery point and time objectives, point-in-time recovery, and a drill on the real host. Until then a backup is taken by hand before every deployment that migrates.
