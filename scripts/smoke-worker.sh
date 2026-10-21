#!/usr/bin/env bash
# Smoke start of Host.Worker against a throwaway stack: it must start, run its jobs, stop on SIGTERM, and the operator
# command `marketdata` must answer with the documented exit codes and leave the documented rows (docs/runbooks/listing-lifecycle.md).
# The stack is its own compose project (saker-smoke), on other ports, with random passwords that live only in this shell; the
# dev stack (saker-watch), its volumes and your secrets are never touched. The worker reaches the Banco de Guatemala for the
# exchange rate (public, no key); no market is routed to a provider, so nobody else is called.
#   bash scripts/smoke-worker.sh [output-dir]      (default: a new temporary directory; logs stay there)
# Needs docker and a built solution (dotnet build). On a CPU without AVX set SMOKE_NOAVX=1.
set -u
cd "$(dirname "$0")/.."
root="$PWD"
out="${1:-$(mktemp -d)}"
mkdir -p "$out"
failures=0
log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$out/steps.log"; }
check() { # check <what> <expected> <actual>
  if [[ "$2" == "$3" ]]; then log "ok   $1"; else log "FAIL $1: expected '$2', got '$3'"; failures=$((failures + 1)); fi
}

rand() { head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24; }
export POSTGRES_PASSWORD="$(rand)" MONGO_PASSWORD="$(rand)" REDIS_PASSWORD="$(rand)"
export API_DB_PASSWORD="$(rand)" WORKER_DB_PASSWORD="$(rand)" MONGO_APP_PASSWORD="$(rand)"
export POSTGRES_PORT=55432 MONGO_PORT=57017 REDIS_PORT=56379
files=(-f deploy/docker-compose.yml)
if [[ "${SMOKE_NOAVX:-0}" == "1" ]]; then
  export MONGO_NOAVX_IMAGE="${MONGO_NOAVX_IMAGE:-mongo:4.4}"
  files+=(-f deploy/docker-compose.no-avx.yml)
fi
files+=(-f deploy/docker-compose.dev-ports.yml)
compose=(docker compose -p saker-smoke "${files[@]}")

cleanup() { log "tearing the smoke stack down (containers and its own volumes)"; "${compose[@]}" down -v >>"$out/compose.log" 2>&1; }
trap cleanup EXIT

log "starting postgres, mongo and redis"
"${compose[@]}" up -d postgres mongo redis >"$out/compose.log" 2>&1 || { log "compose up failed"; tail -5 "$out/compose.log"; exit 1; }
healthy=0
for _ in $(seq 1 60); do
  healthy=$("${compose[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | grep -c healthy)
  [[ "$healthy" -ge 3 ]] && break
  sleep 2
done
check "the three services are healthy" 3 "$healthy"
[[ "$healthy" -ge 3 ]] || exit 1

ConnectionStrings__PostgresAdmin="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=saker;Username=saker_admin;Password=$POSTGRES_PASSWORD" \
ConnectionStrings__MongoAdmin="mongodb://saker_admin:$MONGO_PASSWORD@127.0.0.1:$MONGO_PORT/?authSource=admin" \
  dotnet run --no-build --project src/Hosts/Host.Migrator >"$out/migrator.log" 2>&1
check "the migrator exits 0" 0 "$?"

COMPOSE_PROJECT=saker-smoke COMPOSE_EXTRA_FILE="$([[ "${SMOKE_NOAVX:-0}" == "1" ]] && echo deploy/docker-compose.no-avx.yml)" bash scripts/dev-create-logins.sh >"$out/logins.log" 2>&1
check "the runtime logins are created" 0 "$?"

export ConnectionStrings__Postgres="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=saker;Username=saker_worker;Password=$WORKER_DB_PASSWORD"
export ConnectionStrings__Mongo="mongodb://saker_app:$MONGO_APP_PASSWORD@127.0.0.1:$MONGO_PORT/saker"
export ConnectionStrings__Redis="127.0.0.1:$REDIS_PORT,password=$REDIS_PASSWORD"
export Audit__ActorPepper="$(head -c 32 /dev/urandom | base64)"
export DOTNET_ENVIRONMENT=Production

log "running the worker for 25 seconds"
timeout -s TERM 25 dotnet run --no-build --project src/Hosts/Host.Worker >"$out/worker.log" 2>&1
check "the worker was still running when it was stopped (124)" 124 "$?"
has() { grep -q "$1" "$out/worker.log" && echo yes || echo no; }
check "the worker logged its start (9201)" yes "$(has '"Name":"WorkerStarted"')"
check "the worker logged a graceful stop (9202)" yes "$(has '"Name":"WorkerStopping"')"
check "no warning or error in the worker log (but the dry-run notice of Production)" 0 "$(grep -E '"@l":"(Warning|Error|Fatal)"' "$out/worker.log" | grep -vc DryRunOutsideDevelopment)"

psql_admin() { "${compose[@]}" exec -T postgres psql -U saker_admin -d saker -v ON_ERROR_STOP=1 -At "$@"; }
psql_admin -c "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (gen_random_uuid(), 'XNAS', 'OLDT', 'Old Ticker Inc.', 'USD', 'America/New_York', 'equity', true, now())" >>"$out/steps.log" 2>&1
psql_admin -c "INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) SELECT 'twelvedata', 'XNAS', 'OLDT', id, now() FROM marketdata.stocks WHERE ticker = 'OLDT'" >>"$out/steps.log" 2>&1

cli() { dotnet run --no-build --project src/Hosts/Host.Worker -- marketdata "$@" >>"$out/cli.log" 2>&1; echo $?; }
check "an unreadable command exits 2" 2 "$(cli explode)"
check "an unknown stock exits 1" 1 "$(cli delist XNAS NOPE)"
check "rename exits 0" 0 "$(cli rename XNAS OLDT NEWT --on 2026-10-01)"
check "delist exits 0" 0 "$(cli delist XNAS NEWT --on 2026-10-02)"
check "delist again exits 0 (already so)" 0 "$(cli delist XNAS NEWT)"
check "relist exits 0" 0 "$(cli relist XNAS NEWT)"
check "rename to the ticker it has exits 1" 1 "$(cli rename XNAS NEWT NEWT)"

check "the stock has its new ticker, trading" "NEWT|t|f" "$(psql_admin -c "SELECT ticker, is_active, delisted_at IS NOT NULL FROM marketdata.stocks")"
check "the old ticker is in the history" "XNAS|OLDT" "$(psql_admin -c "SELECT market_mic, ticker FROM marketdata.ticker_history")"
check "both corporate actions are recorded by the operator" "delisting|operator
symbol_change|operator" "$(psql_admin -c "SELECT kind, source FROM marketdata.corporate_actions ORDER BY kind")"
check "the provider symbol follows the ticker" "NEWT" "$(psql_admin -c "SELECT provider_symbol FROM marketdata.symbol_aliases")"

if [[ "$failures" -eq 0 ]]; then log "ALL OK (logs in $out)"; else log "$failures check(s) FAILED (logs in $out)"; fi
[[ "$failures" -eq 0 ]]
