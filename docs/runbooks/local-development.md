# Local development: infrastructure, migrations and runtime logins

- **Purpose:** how to run the datastores, apply migrations and start the hosts on a development machine, and how the runtime logins relate to `api_role` and `worker_role`.
- **Owner:** Eder · **Status:** approved · **Target phase:** 1 (extended as hosts gain features)

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
It applies platform, then identity, then marketdata, then watchlist, then MongoDB, and exits non-zero on the first failure. Running it again applies nothing. Migrations are forward-only (ADR-0021).

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

Market data (milestone 2.2). Prices come from Twelve Data on its free plan (ADR-0027). Route the markets you want to it and give the key. The key is the one secret that must never appear in a file of the repository, in a URL or in a log; keep it as a .NET user secret, shared by both hosts (they use the same secrets id):
```
dotnet user-secrets set "TwelveData:ApiKey" "<key>" --project src/Hosts/Host.Worker
dotnet user-secrets set "MarketData:Providers:Markets:XNAS" "twelvedata" --project src/Hosts/Host.Worker
dotnet user-secrets set "MarketData:Providers:Markets:XNYS" "twelvedata" --project src/Hosts/Host.Worker
```
User secrets are read only in the Development environment, so run the hosts with `DOTNET_ENVIRONMENT=Development` (the Worker) or `ASPNETCORE_ENVIRONMENT=Development` (the API); without it the key is not found and a host that routes a market to Twelve Data refuses to start. In a container the same settings arrive as `TwelveData__ApiKey` from a Docker secret or the environment. The secrets file lives in your home directory (`~/.microsoft/usersecrets/`), outside the repository. `MarketData:Providers:Markets` lists the canonical market codes (`XNAS`, `XNYS`, `ARCX`, ...) that are enabled; a market that is not listed is disabled. The free plan serves US listings only. Every call spends one credit of a budget shared through Redis (`TwelveData:CreditsPerMinute`, default 8, and `TwelveData:CreditsPerDay`, default 800, counted per UTC day); a call without budget is not sent and the caller is told how long to wait, so a host that routes a market to Twelve Data needs Redis as well. If Redis is down the call is allowed and Twelve Data enforces its own limit. The raw body of every successful response is kept in MongoDB (`provider_payloads`, 90 days by default) so a parsing change can be replayed; set `MarketData:Payloads:Enabled=false` to keep nothing, and the host then does not need MongoDB for this. Ingestion settings live under `MarketData:Ingestion` (`Enabled`, `CheckInterval` ten minutes, `SettleDelay` three hours, `BackfillYears` ten, `RefreshOverlapDays` ten, `RewriteTolerance`); the worker queues the daily refresh of every enabled market on its own. The API uses the same settings to search stocks and to register one the catalog does not have yet when a user adds it to the watchlist (ADR-0032): it needs the same routing table, the key and Redis (shared user secrets cover this locally); it only searches, which costs no credit, and never fetches prices. `MarketData:Lookup` sets the searches shared by all users (`SearchesPerMinute` 30, `SearchesPerDay` 2000), how long an answer is cached (`CacheMinutes` 10) and `MaxResults` 20; `Watchlist:Limits` lowers the stocks a user may keep (`MaxItems` up to 500). To check the plan and the API against the real service use `scripts/verify-twelvedata.sh`. To check the adapter itself against the real service (about forty-five credits, several minutes, a report in `/tmp/saker-live-twelvedata/report.md`) use `bash scripts/run-live-twelvedata.sh`; it is never part of `dotnet test` or CI.

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

## Scripts that start a throwaway stack
Each uses its own compose project, other ports and random passwords that live only in the script's shell, and removes everything it made; none touches the dev stack (`saker-watch`), its volumes or your secrets. They need docker and a built solution (`dotnet build`); on a CPU without AVX set the variable named in each.
- `bash scripts/smoke-worker.sh` (`SMOKE_NOAVX=1`): applies the migrations, runs the worker for 25 seconds and tries the operator command `marketdata delist|relist|rename` (`docs/runbooks/listing-lifecycle.md`). Prints `ALL OK` or one `FAIL` line per check.
- `bash scripts/run-k6.sh` (`LOAD_NOAVX=1`, `K6_ENFORCE=1`, `K6_PHASE_SECONDS`): seeds 500 stocks with six years of prices, starts the API with a stand-in identity provider and measures the read paths with k6 with the performance cache on and off (`docs/reports/phase-2-load-baseline.md`). `K6_ENFORCE=1` (at least 20 s per scenario) fails the run when a budget is crossed with the cache on, twice in a row; the cache-off run never enforces; k6 failing or a missing summary always fails it (how it is judged: `docs/reports/phase-2-load-baseline.md`). After the two runs it measures the first read after a load with and without the warming job (`K6_FIRST_READ_CYCLES`, default 5, 0 skips it; `K6_ONLY_FIRST_READ=1` runs only that part), starting a worker for it. `K6_P95_BUDGET_MS` (default 300, ADR-0033) is for a slower machine only: the run writes the value it used at the top of `results.md` and flags an override, and whoever quotes that run writes the value and the reason next to the numbers. Needs python3 (3.14 or later, whose `http.server` serves the stand-in identity provider over TLS) and openssl.
- `bash scripts/dev-backup.sh`, `scripts/dev-restore.sh` and `bash scripts/restore-drill.sh` (`DRILL_NOAVX=1`): back up the databases of a stack, restore a backup into new databases (never over existing ones) and prove on a throwaway stack that the copy is the same (`docs/runbooks/restore.md`).
- `bash scripts/run-live-edgar.sh` and `bash scripts/run-live-twelvedata.sh` are opt-in checks against the real services; they read the identity or key from your environment and are never run by `dotnet test`.

## Gates of the repository
- `bash scripts/check-todos.sh` checks every `TODO`, `HACK` and `FIXME` marker of the tracked sources: `TODO(owner: name[, ref]): text`, and `HACK` and `FIXME` with the ref (a line with a valid marker and a bare one fails). `bash tests/hooks/run.sh` tests it.
- `python3 -I scripts/check-coverage.py <results-directory> 85` fails below 85 % in a `*.Domain` or `*.Application` assembly, and when such an assembly of `src/` appears in no report; `scripts/coverage-exempt.txt` lists the few that hold no executable code yet.

## Integration tests
The PostgreSQL container migrates one template database per test class fixture and every test gets a copy (`CREATE DATABASE ... TEMPLATE`, about a quarter of a second), instead of running every migration again. The API tests point Redis at a closed port with a short `connectTimeout`; the host connects at start and a Redis command fails at once, so a test no longer waits seconds for a cache that is not there.
