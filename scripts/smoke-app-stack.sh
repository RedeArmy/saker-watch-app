#!/usr/bin/env bash
# Smoke test of the application images on a throwaway stack: it builds the three images, runs the migrator, starts the API and the worker beside the datastores
# with the memory limits of deploy/docker-compose.app.yml, checks that they are healthy and hardened, and reports how much memory each container uses at rest and
# after a little traffic. The numbers belong to the machine they were taken on: for the budget of the Oracle host (RFC-0002) run it there.
# The stack is its own compose project (saker-appsmoke), on other ports, with random passwords that live only in this shell; the dev stack, its volumes and your
# secrets are never touched. No market is routed to a provider and the identity provider address is a placeholder: nobody outside is called except the Banco de
# Guatemala for the exchange rate.
#   bash scripts/smoke-app-stack.sh [output-dir]      (default: a new temporary directory; logs stay there)
# Needs docker and curl. On a CPU without AVX set SMOKE_NOAVX=1. SMOKE_REQUESTS (default 300) is the number of requests of the traffic sample.
set -u
cd "$(dirname "$0")/.."
out="${1:-$(mktemp -d)}"
mkdir -p "$out"
failures=0
log() {
  echo "[$(date +%H:%M:%S)] $*" | tee -a "$out/steps.log"
  return 0
}
check() { # check <what> <expected> <actual>
  if [[ "$2" == "$3" ]]; then
    log "ok   $1"
  else
    log "FAIL $1: expected '$2', got '$3'"
    failures=$((failures + 1))
  fi
  return 0
}
rand() {
  head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24
  return 0
}

export POSTGRES_PASSWORD="$(rand)" MONGO_PASSWORD="$(rand)" REDIS_PASSWORD="$(rand)"
export API_DB_PASSWORD="$(rand)" WORKER_DB_PASSWORD="$(rand)" MONGO_APP_PASSWORD="$(rand)"
export AUDIT_ACTOR_PEPPER="$(head -c 32 /dev/urandom | base64)"
export CLAVARIS_AUTHORITY="https://idp.invalid/o/smoke" CLAVARIS_AUDIENCE="smoke-api"
api_port=58180
cat >"$out/ports.yml" <<YAML
services:
  api: { ports: ["127.0.0.1:$api_port:8080"] }
YAML
files=(-f deploy/docker-compose.yml)
if [[ "${SMOKE_NOAVX:-0}" == "1" ]]; then
  export MONGO_NOAVX_IMAGE="${MONGO_NOAVX_IMAGE:-mongo:4.4}"
  files+=(-f deploy/docker-compose.no-avx.yml)
fi
files+=(-f deploy/docker-compose.app.yml -f "$out/ports.yml")
compose=(docker compose -p saker-appsmoke "${files[@]}" --profile app)

cleanup() {
  log "tearing the smoke stack down (containers and its own volumes)"
  "${compose[@]}" down -v >>"$out/compose.log" 2>&1
  return 0
}
trap cleanup EXIT

log "building the images (the first build downloads the SDK and the packages)"
"${compose[@]}" build >"$out/build.log" 2>&1
check "the three images build" 0 "$?"
[[ "$failures" -eq 0 ]] || { tail -5 "$out/build.log"; exit 1; }

log "starting postgres, mongo and redis"
"${compose[@]}" up -d postgres mongo redis >"$out/compose.log" 2>&1 || { log "compose up failed"; tail -5 "$out/compose.log"; exit 1; }
healthy=0
for _ in $(seq 1 60); do
  healthy=$("${compose[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$2 == "healthy" { n++ } END { print n + 0 }')
  [[ "$healthy" -ge 3 ]] && break
  sleep 2
done
check "the three datastores are healthy" 3 "$healthy"
[[ "$healthy" -ge 3 ]] || exit 1
steady=0
for _ in $(seq 1 60); do
  if "${compose[@]}" exec -T postgres psql -U saker_admin -d saker -At -c 'SELECT 1' >/dev/null 2>&1; then steady=$((steady + 1)); else steady=0; fi
  [[ "$steady" -ge 3 ]] && break
  sleep 1
done

log "running the migrator image"
"${compose[@]}" run --rm migrator >"$out/migrator.log" 2>&1
check "the migrator exits 0" 0 "$?"

COMPOSE_PROJECT=saker-appsmoke COMPOSE_EXTRA_FILE="$([[ "${SMOKE_NOAVX:-0}" == "1" ]] && echo deploy/docker-compose.no-avx.yml)" bash scripts/dev-create-logins.sh >"$out/logins.log" 2>&1
check "the runtime logins are created" 0 "$?"

log "starting the API and the worker"
"${compose[@]}" up -d api worker >>"$out/compose.log" 2>&1
check "compose starts the API and the worker" 0 "$?"
state=""
for _ in $(seq 1 60); do
  state=$("${compose[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1 == "api" { print $2 }')
  [[ "$state" == "healthy" ]] && break
  sleep 2
done
check "the API container is healthy" healthy "$state"

http() { # http <path> -> status code
  curl -s -o /dev/null -m 10 -w '%{http_code}' "http://127.0.0.1:$api_port$1"
  return 0
}
check "/health/live answers 200" 200 "$(http /health/live)"
check "/health/ready answers 200 (PostgreSQL, MongoDB and Redis are reached)" 200 "$(http /health/ready)"
check "a protected route answers 401 without a token" 401 "$(http /v1/watchlist)"
sleep 15
check "the worker is still running after 15 seconds" running "$("${compose[@]}" ps --format '{{.Service}} {{.State}}' | awk '$1 == "worker" { print $2 }')"
check "the worker never restarted" 0 "$(docker inspect -f '{{.RestartCount}}' "$("${compose[@]}" ps -q worker)")"
check "no warning or error from the API except the identity provider placeholder" 0 "$("${compose[@]}" logs api 2>/dev/null | grep -E '"@l":"(Error|Fatal)"' | grep -vc idp.invalid)"
check "no error from the worker" 0 "$("${compose[@]}" logs worker 2>/dev/null | grep -cE '"@l":"(Error|Fatal)"')"

# What the images are allowed to be.
for service in api worker migrator; do
  check "$service runs as the non-root user 1654" 1654 "$(docker image inspect "saker-watch/$service:${IMAGE_TAG:-local}" -f '{{.Config.User}}')"
done
for service in api worker; do
  container=$("${compose[@]}" ps -q "$service")
  check "$service has a read-only root file system" true "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$container")"
  check "$service drops every capability" "[ALL]" "$(docker inspect -f '{{.HostConfig.CapDrop}}' "$container")"
  check "$service is not privileged" false "$(docker inspect -f '{{.HostConfig.Privileged}}' "$container")"
done
check "the datastores are not on the network that has outbound access" 0 "$("${compose[@]}" ps -q postgres mongo redis | xargs docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}' | grep -c 'saker-appsmoke_app')"

# Memory: at rest, and after some traffic (the health route does the three datastore round trips).
sample() { # sample <label>
  log "memory $1 (container, used / limit):"
  docker stats --no-stream --format '{{.Name}} {{.MemUsage}}' $("${compose[@]}" ps -q) | sed 's/saker-appsmoke-//' | tee -a "$out/memory.log" | sed 's/^/       /'
  return 0
}
sample "at rest"
requests="${SMOKE_REQUESTS:-300}"
for _ in $(seq 1 "$requests"); do curl -s -o /dev/null -m 10 "http://127.0.0.1:$api_port/health/ready" & if (( $(jobs -r | wc -l) >= 8 )); then wait -n; fi; done
wait
sample "after $requests requests to /health/ready, 8 at a time"
oom=$(docker inspect -f '{{.State.OOMKilled}}' $("${compose[@]}" ps -q api worker postgres mongo redis) | grep -c true)
check "no container was killed for memory" 0 "$oom"
if [[ -r /proc/meminfo ]]; then log "host: $(awk '/MemTotal/ { t=$2 } /MemAvailable/ { a=$2 } END { printf "%.1f GB available of %.1f GB", a/1048576, t/1048576 }' /proc/meminfo)"; fi

if [[ "$failures" -eq 0 ]]; then log "ALL OK (logs in $out)"; else log "$failures check(s) FAILED (logs in $out)"; fi
[[ "$failures" -eq 0 ]]
