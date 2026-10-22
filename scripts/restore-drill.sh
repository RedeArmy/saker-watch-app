#!/usr/bin/env bash
# A restore drill on a throwaway stack (compose project saker-drill: other ports, random passwords, removed at the end): migrate, put a little
# data in PostgreSQL and MongoDB, back it up with scripts/dev-backup.sh, restore it into new databases with scripts/dev-restore.sh, and check that the
# copy is the same: the same rows, the same grants and policies, the hypertable, the functions, and a migrator that finds nothing left to apply.
# Nothing is dropped or overwritten, and the dev stack (saker-watch), its volumes and your secrets are never touched.
#   bash scripts/restore-drill.sh [output-dir]      (default: a new temporary directory; the backup and the logs stay there)
# Needs docker and a built solution (dotnet build). On a CPU without AVX set DRILL_NOAVX=1.
set -u
cd "$(dirname "$0")/.."
out="${1:-$(mktemp -d)}"
mkdir -p "$out"
chmod 700 "$out"
failures=0
log() {
  echo "[$(date +%H:%M:%S)] $*" | tee -a "$out/steps.log"
  return 0
}
check() { # check <what> <expected> <actual>
  local what="$1" expected="$2" actual="$3"
  if [[ "$expected" == "$actual" ]]; then
    log "ok   $what"
  else
    log "FAIL $what: expected '$expected', got '$actual'"
    failures=$((failures + 1))
  fi
  return 0
}

rand() {
  head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24
  return 0
}
export POSTGRES_PASSWORD="$(rand)" MONGO_PASSWORD="$(rand)" REDIS_PASSWORD="$(rand)"
export POSTGRES_PORT=55452 MONGO_PORT=57037 REDIS_PORT=56399
files=(-f deploy/docker-compose.yml)
extra=""
if [[ "${DRILL_NOAVX:-0}" == "1" ]]; then
  export MONGO_NOAVX_IMAGE="${MONGO_NOAVX_IMAGE:-mongo:4.4}"
  extra="deploy/docker-compose.no-avx.yml"
  files+=(-f "$extra")
fi
files+=(-f deploy/docker-compose.dev-ports.yml)
compose=(docker compose -p saker-drill "${files[@]}")
export COMPOSE_PROJECT=saker-drill COMPOSE_EXTRA_FILE="$extra"
target=saker_restored

cleanup() {
  log "tearing the drill stack down (containers and its own volumes)"
  "${compose[@]}" down -v >>"$out/compose.log" 2>&1
  return 0
}
trap cleanup EXIT

psql_in() { # psql_in <database> <statement>: one value
  local database="$1" statement="$2"
  "${compose[@]}" exec -T postgres psql -U saker_admin -d "$database" -v ON_ERROR_STOP=1 -q -At -c "$statement" 2>>"$out/psql.log"
  return $?
}
mongo_eval() { # mongo_eval <javascript>: one value
  local script="$1"
  export MONGO_PASSWORD
  "${compose[@]}" exec -T -e MONGO_PASSWORD -e SCRIPT="$script" mongo sh -c \
    'cmd=$(command -v mongosh || command -v mongo); "$cmd" --quiet --username saker_admin --password "$MONGO_PASSWORD" --authenticationDatabase admin --eval "$SCRIPT"' 2>>"$out/mongo.log"
  return $?
}

# One value that changes if any row of the tables the product lives on changes.
fingerprint_sql="SELECT md5(string_agg(x, ',' ORDER BY x)) FROM (
  SELECT 's' || id::text || ticker AS x FROM marketdata.stocks
  UNION ALL SELECT 'p' || stock_id::text || trade_date::text || close::text || adjusted_close::text FROM marketdata.price_daily
  UNION ALL SELECT 'f' || base || quote || as_of_date::text || rate::text FROM marketdata.fx_rates
  UNION ALL SELECT 'w' || id::text || user_id::text || stock_id::text FROM watchlist.watchlist_items
  UNION ALL SELECT 'o' || id::text || user_id::text || name FROM watchlist.portfolios
) t"

log "starting postgres, mongo and redis"
"${compose[@]}" up -d postgres mongo redis >"$out/compose.log" 2>&1 || { log "compose up failed"; tail -5 "$out/compose.log"; exit 1; }
healthy=0
for _ in $(seq 1 60); do
  healthy=$("${compose[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$2 == "healthy" { n++ } END { print n + 0 }')
  [[ "$healthy" -ge 3 ]] && break
  sleep 2
done
check "the three services are healthy" 3 "$healthy"
[[ "$healthy" -ge 3 ]] || exit 1

# The image reports healthy while its init server is still up, and then restarts: wait until the real one answers three times in a row.
steady=0
for _ in $(seq 1 60); do
  if "${compose[@]}" exec -T postgres psql -U saker_admin -d saker -At -c 'SELECT 1' >/dev/null 2>&1; then steady=$((steady + 1)); else steady=0; fi
  [[ "$steady" -ge 3 ]] && break
  sleep 1
done

migrate() { # migrate <postgres database>
  CONNECTIONSTRINGS__POSTGRESADMIN="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=$1;Username=saker_admin;Password=$POSTGRES_PASSWORD" \
  CONNECTIONSTRINGS__MONGOADMIN="mongodb://saker_admin:$MONGO_PASSWORD@127.0.0.1:$MONGO_PORT/?authSource=admin" \
    dotnet run --no-build --project src/Hosts/Host.Migrator >>"$out/migrator.log" 2>&1
  return $?
}
migrate saker
check "the migrator applies the schema" 0 "$?"

log "putting data in both stores"
psql_in saker "
  INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at)
  SELECT gen_random_uuid(), 'XNAS', 'D' || n, 'Drill ' || n, 'USD', 'America/New_York', 'equity', true, now() FROM generate_series(1, 5) n;
  INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
  SELECT s.id, d::date, 10 + n, 12 + n, 9 + n, 11 + n, 11 + n, 1000, 'twelvedata', true, now()
  FROM marketdata.stocks s CROSS JOIN generate_series(1, 120) n CROSS JOIN LATERAL (SELECT (date '2026-01-01' + n) AS d) g;
  INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at) SELECT 'USD', 'GTQ', date '2026-10-01' + n, 'banguat', 7.6 + n / 1000.0, now() FROM generate_series(0, 9) n;
  INSERT INTO marketdata.fx_rates_rejected (base, quote, as_of_date, source, rate, reason, seen_at) VALUES ('USD', 'GTQ', date '2026-10-05', 'banguat', 76.4, 'out_of_band', now());
  INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at)
  SELECT gen_random_uuid(), '00000000-0000-0000-0000-0000000000d1', id, now(), now() FROM marketdata.stocks;
  INSERT INTO watchlist.portfolios (id, user_id, name, position, created_at, updated_at) VALUES (gen_random_uuid(), '00000000-0000-0000-0000-0000000000d1', 'Drill', 0, now(), now());
" >/dev/null
inserted="$(mongo_eval 'var r = db.getSiblingDB("saker").provider_payloads.insertOne({_id: "drill:payload", schemaVersion: NumberInt(1), createdAt: new Date(), provider: "drill"}); r.acknowledged ? "inserted" : "refused"')"
check "a document is in MongoDB before the backup" inserted "$inserted"
before_postgres="$(psql_in saker "$fingerprint_sql")"
before_history="$(psql_in saker "SELECT (SELECT count(*) FROM marketdata.__ef_migrations_history) + (SELECT count(*) FROM watchlist.__ef_migrations_history) + (SELECT count(*) FROM platform.__ef_migrations_history) + (SELECT count(*) FROM identity.__ef_migrations_history)")"
before_mongo="$(mongo_eval 'var d = db.getSiblingDB("saker"); d.getCollectionNames().sort().join(",") + "|" + d.provider_payloads.countDocuments({}) + "|" + d.getCollection("_migrations").countDocuments({})')"
log "before: postgres $before_postgres, migrations $before_history, mongo $before_mongo"

log "backup"
bash scripts/dev-backup.sh "$out/backup" >>"$out/backup.log" 2>&1
check "the backup is written" 0 "$?"
(cd "$out/backup" && sha256sum --check --quiet SHA256SUMS)
check "the checksums of the backup hold" 0 "$?"

log "restore into new databases ($target)"
bash scripts/dev-restore.sh "$out/backup" "$target" >>"$out/restore.log" 2>&1
check "the restore ends" 0 "$?"
bash scripts/dev-restore.sh "$out/backup" "$target" >>"$out/restore.log" 2>&1
check "a second restore into the same name is refused, so nothing is overwritten" 1 "$?"
bash scripts/dev-restore.sh "$out/backup" saker >>"$out/restore.log" 2>&1
check "a restore over the live database is refused" 1 "$?"
mongo_eval 'db.getSiblingDB("saker_leftover").leftovers.insertOne({a: 1}).acknowledged' >/dev/null
bash scripts/dev-restore.sh "$out/backup" saker_leftover >>"$out/restore.log" 2>&1
check "a restore into a MongoDB database that already has collections is refused" 1 "$?"
check "and it created nothing in PostgreSQL on the way" 0 "$(psql_in postgres "SELECT count(*) FROM pg_database WHERE datname = 'saker_leftover'")"

check "the restored rows are the same" "$before_postgres" "$(psql_in "$target" "$fingerprint_sql")"
check "the live database still has them" "$before_postgres" "$(psql_in saker "$fingerprint_sql")"
check "the history of migrations is the same" "$before_history" "$(psql_in "$target" "SELECT (SELECT count(*) FROM marketdata.__ef_migrations_history) + (SELECT count(*) FROM watchlist.__ef_migrations_history) + (SELECT count(*) FROM platform.__ef_migrations_history) + (SELECT count(*) FROM identity.__ef_migrations_history)")"
check "the price table is a hypertable again" 1 "$(psql_in "$target" "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'price_daily'")"
check "the API role reads the catalog" t "$(psql_in "$target" "SELECT has_table_privilege('api_role', 'marketdata.stocks', 'SELECT')")"
check "the API role still cannot read the rejected rates" f "$(psql_in "$target" "SELECT has_table_privilege('api_role', 'marketdata.fx_rates_rejected', 'SELECT')")"
check "the row-level security of the watchlist is in place" "3|true" "$(psql_in "$target" "SELECT (SELECT count(*) FROM pg_policies WHERE schemaname = 'watchlist') || '|' || (SELECT relrowsecurity FROM pg_class WHERE oid = 'watchlist.watchlist_items'::regclass)")"
check "the security-definer functions are there" 2 "$(psql_in "$target" "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE p.prosecdef AND ((n.nspname = 'marketdata' AND p.proname = 'register_stock') OR (n.nspname = 'watchlist' AND p.proname = 'tracked_stock_ids'))")"
check "MongoDB has the same collections and documents" "$before_mongo" "$(mongo_eval "var d = db.getSiblingDB('$target'); d.getCollectionNames().sort().join(',') + '|' + d.provider_payloads.countDocuments({}) + '|' + d.getCollection('_migrations').countDocuments({})")"

migrate "$target"
check "the migrator finds nothing to do on the restored copy" 0 "$?"
check "and the history is still the same" "$before_history" "$(psql_in "$target" "SELECT (SELECT count(*) FROM marketdata.__ef_migrations_history) + (SELECT count(*) FROM watchlist.__ef_migrations_history) + (SELECT count(*) FROM platform.__ef_migrations_history) + (SELECT count(*) FROM identity.__ef_migrations_history)")"

if [[ "$failures" -eq 0 ]]; then log "ALL OK (logs and the backup in $out)"; else log "$failures check(s) FAILED (logs and the backup in $out)"; fi
[[ "$failures" -eq 0 ]]
