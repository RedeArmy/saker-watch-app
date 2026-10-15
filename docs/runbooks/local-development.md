# Local development: infrastructure, migrations and runtime logins

- **Purpose:** how to run the datastores, apply migrations and start the hosts on a development machine, and how the runtime logins relate to `api_role` and `worker_role`.
- **Owner:** Eder · **Status:** draft · **Target phase:** 1 (extended as hosts gain features)

## 1. Secrets (never committed)
`deploy/.env` supplies the bootstrap secrets read by `deploy/docker-compose.yml`: `POSTGRES_PASSWORD`, `MONGO_PASSWORD`, `REDIS_PASSWORD`. The observability stack also needs `GRAFANA_ADMIN_PASSWORD` (only when `docker-compose.obs.yml` is loaded). The runtime logins below take their passwords from the shell environment.

## 2. Start the infrastructure
- `make up` starts PostgreSQL + TimescaleDB, MongoDB, Redis, Caddy and the fake IdP. `make up-no-avx` replaces MongoDB with 4.4 on CPUs without AVX (override image with `MONGO_NOAVX_IMAGE`).
- To run the hosts from an IDE, also load `deploy/docker-compose.dev-ports.yml`, which publishes the datastores on loopback. Host ports are configurable (`POSTGRES_PORT`, `MONGO_PORT`, `REDIS_PORT`) when the defaults are taken.
- `make up-obs` adds Collector, Tempo, Loki, Prometheus and Grafana (see `docs/observability/README.md`).

## 3. Apply the migrations (separate deploy step)
The API and Worker never migrate on startup. Run the migrator with the **admin** connection strings:
```
ConnectionStrings__PostgresAdmin="Host=127.0.0.1;Port=5432;Database=saker;Username=saker_admin;Password=<POSTGRES_PASSWORD>" \
ConnectionStrings__MongoAdmin="mongodb://saker_admin:<MONGO_PASSWORD>@127.0.0.1:27017/?authSource=admin" \
dotnet run --project src/Hosts/Host.Migrator
```
It applies platform, then identity, then MongoDB, and exits non-zero on the first failure. Running it again applies nothing. Migrations are forward-only (ADR-0021).

## 4. Create the runtime logins
The platform migration creates two `NOLOGIN` group roles, `api_role` and `worker_role` (ADR-0010). The hosts connect with **logins** that belong to one of them, never with the admin login:
```
API_DB_PASSWORD=... WORKER_DB_PASSWORD=... MONGO_APP_PASSWORD=... bash scripts/dev-create-logins.sh
```
creates `saker_api` (member of `api_role`), `saker_worker` (member of `worker_role`) and the MongoDB user `saker_app` (`readWrite` on the application database), and sets per-login timeouts (role settings are not inherited through membership, so they live on the logins). It is idempotent. Production creates the same logins with its own secret manager.

## 5. Run the hosts
Both hosts also need a secret pepper for the audit log, generated once and kept out of the repository (`openssl rand -base64 32`):
```
Audit__ActorPepper=<base64, at least 32 bytes>
```
The API needs the Clavaris settings (`docs/clavaris-contract.md`): `Clavaris__Authority` (the Organization issuer URL), `Clavaris__Audience`, and `Clavaris__RequireHttpsMetadata=false` only against a local fake or a local Clavaris. `Clavaris__Audience` is the OAuth client id (A-021), and the issuer is `http://localhost:8080/o/{organizationId}` for a Clavaris on port 8080. On the first valid token of a new account the API creates the internal user and writes `user.provisioned` to the audit log.

Both hosts fail at startup when a connection string is missing:
```
ConnectionStrings__Postgres="Host=127.0.0.1;Database=saker;Username=saker_api;Password=<API_DB_PASSWORD>"      # worker: saker_worker
ConnectionStrings__Mongo="mongodb://saker_app:<MONGO_APP_PASSWORD>@127.0.0.1:27017/saker"
ConnectionStrings__Redis="127.0.0.1:6379,password=<REDIS_PASSWORD>"
dotnet run --project src/Hosts/Host.Api
```
`/health/ready` reports PostgreSQL, MongoDB and Redis (status only, no details) and returns 503 when any is down.

## 6. Integration tests
`dotnet test` starts its own containers (Docker required). The MongoDB image comes from the compose file, falling back to `mongo:4.4` on a CPU without AVX; `SAKER_TEST_MONGO_IMAGE` overrides it.

## API contract
`GET /openapi/v1.json` and `/swagger` are open in Development. To expose the document elsewhere set `OpenApi__Anonymous=true`. After changing an endpoint run `UPDATE_OPENAPI=1 dotnet test tests/Contract.Tests` and commit `docs/api/openapi.v1.json` (CI fails on a stale file).

## Behind a reverse proxy
The API trusts `X-Forwarded-*` only from `ForwardedHeaders__KnownProxies__0` (an address) or `ForwardedHeaders__KnownNetworks__0` (a CIDR such as `172.18.0.0/16`, the compose network of Caddy). Without it the client IP of every anonymous request is the proxy's. CORS stays closed unless `Cors__AllowedOrigins__0=https://app.example.com` is set (A-023).

## Retention (worker)
The worker enqueues one retention job per UTC day and runs it through the job queue. It starts in **dry-run mode**: it logs (event 9701) and counts (`sakerwatch_retention_rows_total{mode="dry_run"}`) what it would remove and deletes nothing. After reading that for an environment, set `Retention__DryRun=false`. Ages and limits: `Retention__OutboxProcessedDays`, `Retention__InboxProcessedDays`, `Retention__JobsSucceededDays`, `Retention__JobsDeadDays`, `Retention__BatchSize`, `Retention__MaxBatchesPerTarget`; `Retention__Enabled=false` stops the daily schedule. A delete run that reports `more remaining` is finished by the next run, and running twice is harmless.
