#!/usr/bin/env bash
# Load baseline of the Phase 2 read paths (milestone 2.8): the performance cache on, and then off, on one throwaway stack.
# It builds its own compose project (saker-load: other ports, random passwords, removed at the end), applies the migrations, seeds 500
# listings with six years of prices (scripts/load/seed.sql), starts the API with a stand-in identity provider (scripts/load/idp.py, a key
# made for this run only) and runs tests/load/baseline.js with k6 from a pinned image. The dev stack and your secrets are never touched.
#   bash scripts/run-k6.sh [output-dir]       (default: a new temporary directory; the k6 summaries and the logs stay there)
# Needs docker, python3, openssl and a built solution. On a CPU without AVX set LOAD_NOAVX=1. K6_ENFORCE=1 fails the run when the
# budgets of ADR-0033 are crossed. The numbers belong to the machine they were taken on: k6, the API and the databases share it.
set -u
cd "$(dirname "$0")/.."
out="${1:-$(mktemp -d)}"
mkdir -p "$out"
chmod 777 "$out"
k6_image="grafana/k6@sha256:3ddc8b1a33a2c3d8edc6e99b6a762ae36cba08788463458f5e6a7703e14eb77d"   # grafana/k6:1.3.0
authority="http://127.0.0.1:58081/o/load"
audience="saker-load-api"
api_url="http://127.0.0.1:58090"
api_pid=""
idp_pid=""

log() {
  echo "[$(date +%H:%M:%S)] $*" | tee -a "$out/steps.log"
  return 0
}

rand() {
  head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24
  return 0
}

export POSTGRES_PASSWORD="$(rand)" MONGO_PASSWORD="$(rand)" REDIS_PASSWORD="$(rand)"
export API_DB_PASSWORD="$(rand)" WORKER_DB_PASSWORD="$(rand)" MONGO_APP_PASSWORD="$(rand)"
export POSTGRES_PORT=55442 MONGO_PORT=57027 REDIS_PORT=56389
files=(-f deploy/docker-compose.yml)
if [[ "${LOAD_NOAVX:-0}" == "1" ]]; then
  export MONGO_NOAVX_IMAGE="${MONGO_NOAVX_IMAGE:-mongo:4.4}"
  files+=(-f deploy/docker-compose.no-avx.yml)
fi
files+=(-f deploy/docker-compose.dev-ports.yml)
compose=(docker compose -p saker-load "${files[@]}")

stop_api() {
  if [[ -n "$api_pid" ]]; then
    kill -TERM "$api_pid" 2>/dev/null
    wait "$api_pid" 2>/dev/null
    api_pid=""
  fi
  return 0
}

cleanup() {
  stop_api
  [[ -n "$idp_pid" ]] && kill -TERM "$idp_pid" 2>/dev/null
  log "tearing the load stack down (containers and its own volumes)"
  "${compose[@]}" down -v >>"$out/compose.log" 2>&1
  return 0
}
trap cleanup EXIT

log "starting postgres, mongo and redis"
"${compose[@]}" up -d postgres mongo redis >"$out/compose.log" 2>&1 || { log "compose up failed"; tail -5 "$out/compose.log"; exit 1; }
healthy=0
for _ in $(seq 1 60); do
  healthy=$("${compose[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | grep -c healthy)
  [[ "$healthy" -ge 3 ]] && break
  sleep 2
done
[[ "$healthy" -ge 3 ]] || { log "the services are not healthy"; exit 1; }

log "applying the migrations and creating the logins"
CONNECTIONSTRINGS__POSTGRESADMIN="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=saker;Username=saker_admin;Password=$POSTGRES_PASSWORD" \
CONNECTIONSTRINGS__MONGOADMIN="mongodb://saker_admin:$MONGO_PASSWORD@127.0.0.1:$MONGO_PORT/?authSource=admin" \
  dotnet run --no-build --project src/Hosts/Host.Migrator >"$out/migrator.log" 2>&1 || { log "the migrator failed"; exit 1; }
COMPOSE_PROJECT=saker-load COMPOSE_EXTRA_FILE="$([[ "${LOAD_NOAVX:-0}" == "1" ]] && echo deploy/docker-compose.no-avx.yml)" \
  bash scripts/dev-create-logins.sh >"$out/logins.log" 2>&1 || { log "creating the logins failed"; exit 1; }

log "seeding 500 listings with six years of prices"
"${compose[@]}" exec -T postgres psql -U saker_admin -d saker -q < scripts/load/seed.sql >"$out/seed.log" 2>&1 || { log "seeding failed"; tail -5 "$out/seed.log"; exit 1; }
grep -E '^ +[0-9]+ \|' "$out/seed.log" | tee -a "$out/steps.log"

log "building the API (Release)"
dotnet build -c Release --nologo -v q src/Hosts/Host.Api >"$out/build.log" 2>&1 || { log "the build failed"; tail -5 "$out/build.log"; exit 1; }

log "starting the stand-in identity provider"
python3 -I scripts/load/idp.py init "$out/idp" "$authority" "$audience" || { log "idp init failed"; exit 1; }
python3 -I scripts/load/idp.py serve "$out/idp" 58081 >"$out/idp.log" 2>&1 &
idp_pid=$!

run_mode() { # run_mode <label> <cache enabled: true|false> <first byte of the user ids>
  local label="$1" cache="$2" prefix="$3" tokens="" index sub
  log "== $label: starting the API (performance cache enabled: $cache)"
  ASPNETCORE_URLS="$api_url" ASPNETCORE_ENVIRONMENT=Production \
  CONNECTIONSTRINGS__POSTGRES="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=saker;Username=saker_api;Password=$API_DB_PASSWORD" \
  CONNECTIONSTRINGS__MONGO="mongodb://saker_app:$MONGO_APP_PASSWORD@127.0.0.1:$MONGO_PORT/saker" \
  CONNECTIONSTRINGS__REDIS="127.0.0.1:$REDIS_PORT,password=$REDIS_PASSWORD" \
  CLAVARIS__AUTHORITY="$authority" CLAVARIS__AUDIENCE="$audience" CLAVARIS__REQUIREHTTPSMETADATA=false \
  RATELIMITING__PERMITLIMIT=100000 RATELIMITING__EXPENSIVEPERMITLIMIT=100000 RATELIMITING__PREAUTHENTICATIONPERMITLIMIT=1000000 \
  AUDIT__ACTORPEPPER="$(head -c 32 /dev/urandom | base64)" MARKETDATA__CACHE__ENABLED="$cache" \
    dotnet src/Hosts/Host.Api/bin/Release/net10.0/SakerWatch.Hosts.Api.dll >"$out/api-$label.log" 2>&1 &
  api_pid=$!
  local ready=0
  for _ in $(seq 1 60); do
    [[ "$(curl -s -o /dev/null -w '%{http_code}' "$api_url/health/ready")" == "200" ]] && { ready=1; break; }
    sleep 1
  done
  [[ "$ready" -eq 1 ]] || { log "the API did not become ready (see api-$label.log)"; stop_api; return 1; }

  for index in 1 2 3 4 5; do
    sub="00000000-0000-0000-0000-000000000${prefix}${index}0"
    tokens="${tokens:+$tokens,}$(python3 -I scripts/load/idp.py token "$out/idp" "$sub")"
  done
  log "running k6 ($label)"
  docker run --rm --network host -v "$PWD/tests/load:/scripts:ro" -v "$out:/out" \
    -e K6_BASE_URL="$api_url" -e K6_TOKENS="$tokens" -e K6_ENFORCE="${K6_ENFORCE:-0}" -e K6_PHASE_SECONDS="${K6_PHASE_SECONDS:-30}" \
    "$k6_image" run --quiet --summary-export="/out/summary-$label.json" /scripts/baseline.js >"$out/k6-$label.log" 2>&1
  log "k6 ($label) exit code: $?"
  stop_api
  return 0
}

run_mode cache-on true a
run_mode cache-off false b

log "results"
K6_PHASE_SECONDS="${K6_PHASE_SECONDS:-30}" python3 -I scripts/load/summarize.py "performance cache on=$out/summary-cache-on.json" "performance cache off=$out/summary-cache-off.json" | tee -a "$out/results.md"
log "logs and summaries are in $out"
