# Runbook — the images and the stack on one host

- **Purpose:** how the application runs as containers on one host (the Oracle Always Free Ampere A1 VM: 2 OCPUs and 12 GB, A-009, RFC-0002), what the memory budget is and how to measure it, and what is still missing before this is a deployment (TD-001, TD-002, TD-015).
- **Owner:** Eder · **Status:** draft · **Target phase:** 5

## What exists
| Piece | File | Notes |
|---|---|---|
| Three images: `api`, `worker`, `migrator` | `deploy/Dockerfile` | Multi-stage, built from the repository root, framework-dependent on the ASP.NET Core base (`linux/amd64` and `linux/arm64`, pinned by the digest of the index; Dependabot proposes new digests). The warnings-as-errors build runs inside the build. They run as the `app` user (1654), without secrets inside |
| The application on the datastores | `deploy/docker-compose.app.yml` | Profile `app`. The migrator is a step that runs and ends; the API and the worker wait for it. The datastores stay on the internal `backend` network; only the API and the worker also join `app`, which has the outbound access the providers need. Read-only root file system, no capabilities, `no-new-privileges`, a process limit and memory and CPU limits on every service |
| Provider settings | `deploy/app.env.example` → `deploy/app.env` | Not in the repository. The providers per market, the Twelve Data key and the credit settings (ADR-0051) |
| A smoke test of all of it | `scripts/smoke-app-stack.sh` | Builds the images, runs the migrator, starts the API and the worker beside the datastores on a throwaway compose project, checks health, hardening and memory |
| A CI job | `images` in `.github/workflows/ci.yml` | Builds the three images and scans them with Trivy (HIGH and CRITICAL with a fix). Not a required check of the ruleset yet |

## Start it
```
# deploy/.env (mode 600): POSTGRES_PASSWORD, MONGO_PASSWORD, REDIS_PASSWORD, API_DB_PASSWORD, WORKER_DB_PASSWORD, MONGO_APP_PASSWORD,
#                         CLAVARIS_AUTHORITY, CLAVARIS_AUDIENCE, AUDIT_ACTOR_PEPPER
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml --profile app up -d postgres mongo redis
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml --profile app run --rm migrator
bash scripts/dev-create-logins.sh        # once: the runtime logins (the host's secret manager does this in production)
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml --profile app up -d api worker
```
On a CPU without AVX add `-f deploy/docker-compose.no-avx.yml` after the first file. The API is not published: the edge reaches it over the `app` network (Caddy still answers a fixed string, TD-002).

## The memory budget (12 GB host)
The limits of the compose file are a first budget; the measurement of the host replaces them.

| Service | Limit | Why | At rest, empty database (2026-10-24, x86-64 dev machine) |
|---|---|---|---|
| `postgres` | 2 GB, `shared_buffers=512MB`, `max_connections=100` | The pools of the API and the worker are 20 each (`ConnectionStrings:PostgresMaxPoolSize`) | 85 MiB |
| `mongo` | 1 GB, WiredTiger cache 0.5 GB | The default cache is half of the memory of the **host** minus 1 GB, not of the container | 74 MiB (4.4, the image without AVX) |
| `redis` | 256 MB, `maxmemory 160mb`, `noeviction` | Budgets and counters must not be evicted | 5 MiB |
| `api` | 768 MB | | 56 MiB, 65 MiB after 300 requests |
| `worker` | 768 MB | | 58 MiB |
| `migrator` | 256 MB | Runs and ends | not sampled (it has ended) |
| Sum of the limits | about 4.8 GB | A ceiling, not a use | about 0.28 GB at rest |

**What the measurement says and does not say.** At rest the whole application uses about 0.3 GB, far under the first estimate of 4 to 6 GB for the stack that RFC-0002 started from. That is an **empty database with no traffic**: PostgreSQL and MongoDB grow with the data and with their caches (250 stocks of ten years of prices is about 630,000 rows), and the observability stack (`docker-compose.obs.yml`: Prometheus, Grafana, Loki, Tempo, the collector) is not in the sample and is the largest consumer left out. The figure that matters for the model is the one taken **on the host, with the data loaded and the load of `scripts/run-k6.sh` running**; run `scripts/smoke-app-stack.sh` there first (day 1 of the spike) and put the numbers here. The worst case the limits allow, about 4.8 GB, leaves about 7 GB of the 12 for the observability stack, the operating system and the model.

## What is not here yet
- **The edge:** Caddy serves a fixed string; the proxy to the API, TLS, HSTS, CSP and `ForwardedHeaders` with the proxy network (A-023) are Phase 5.
- **Secrets as files:** the connection strings are environment variables. The hosts do not read Docker secrets (`/run/secrets`) yet; `deploy/.env` must be mode 600.
- **A registry, a signature and the arm64 build in CI:** the images are built for the machine's own architecture here and in CI; the `linux/arm64` build (cross-built with emulation or on an arm64 runner) has not been run. The Dockerfile selects the runtime identifier from `TARGETARCH` and the base image index includes `linux/arm64`.
- **The datastore images are pinned by tag, not by digest** (the integration tests read their tags from the compose file with a pattern that does not accept a digest); they are listed in TD-002.
- **Backups and the restore drill on this host**, alerts, the worker's own health signal (it serves no port; its liveness is the restart policy and the job metrics).
