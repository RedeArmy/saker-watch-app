#!/usr/bin/env bash
# Restores a backup made by scripts/dev-backup.sh into NEW databases of a compose stack. It never drops, truncates or overwrites anything: it
# refuses to run when the target names already exist. Putting the restored copy in service (renaming it, or pointing the hosts at it) is a step
# the operator does by hand after looking at it (docs/runbooks/restore.md).
#   bash scripts/dev-restore.sh <backup-directory> [target-name]       (default target name: saker_restored)
# The checksums of the backup are verified before anything is created. TimescaleDB needs its restore mode around pg_restore; the roles `api_role`
# and `worker_role` are created if the server does not have them (a fresh server). The login roles are cluster objects and are not in the dump:
# scripts/dev-create-logins.sh makes them again. Same variables as dev-backup.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

compose=(docker compose -p "${COMPOSE_PROJECT:-saker-watch}" -f deploy/docker-compose.yml ${COMPOSE_EXTRA_FILE:+-f "$COMPOSE_EXTRA_FILE"})
dir="${1:?usage: dev-restore.sh <backup-directory> [target-name]}"
target="${2:-saker_restored}"
source_database="${MONGO_DATABASE:-saker}"

if [[ ! "$target" =~ ^[a-z][a-z0-9_]{0,40}$ || "$target" == "saker" || "$target" == "postgres" || "$target" == "admin" ]]; then
  echo "ERROR: the target name must be lower-case letters, digits and underscores, and not the name of a live database" >&2
  exit 1
fi

for name in POSTGRES_PASSWORD MONGO_PASSWORD; do
  if [[ -z "${!name:-}" ]]; then
    echo "ERROR: $name is not set" >&2
    exit 1
  fi
done

(cd "$dir" && sha256sum --check --quiet SHA256SUMS)

psql_admin() { # psql_admin <database> <statement>
  local database="$1" statement="$2"
  "${compose[@]}" exec -T postgres psql -U saker_admin -d "$database" -v ON_ERROR_STOP=1 -q -At -c "$statement"
  return $?
}

if [[ "$(psql_admin postgres "SELECT count(*) FROM pg_database WHERE datname = '$target'")" != "0" ]]; then
  echo "ERROR: the PostgreSQL database $target already exists; choose another name" >&2
  exit 1
fi

mongo_collections() { # mongo_collections <database>: how many collections it has (0 when it does not exist)
  local database="$1"
  export MONGO_PASSWORD
  "${compose[@]}" exec -T -e MONGO_PASSWORD -e DATABASE="$database" mongo sh -c \
    'cmd=$(command -v mongosh || command -v mongo); "$cmd" --quiet --username saker_admin --password "$MONGO_PASSWORD" --authenticationDatabase admin --eval "db.getSiblingDB(\"$DATABASE\").getCollectionNames().length" | tail -1'
  return $?
}

if [[ "$(mongo_collections "$target")" != "0" ]]; then
  echo "ERROR: the MongoDB database $target already has collections; choose another name" >&2
  exit 1
fi

echo "PostgreSQL -> $target ..."
psql_admin postgres "CREATE DATABASE $target OWNER saker_admin"
psql_admin "$target" "CREATE EXTENSION IF NOT EXISTS timescaledb"
for role in api_role worker_role; do
  psql_admin postgres "DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') THEN CREATE ROLE $role NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; END IF; END \$\$"
done
psql_admin "$target" "SELECT timescaledb_pre_restore()" >/dev/null
# The extensions are created above, so their entries are left out of the list; everything else, grants and policies included, is restored.
"${compose[@]}" exec -T -e TARGET="$target" postgres sh -c 'cat >/tmp/restore.dump && pg_restore -l /tmp/restore.dump | grep -v -E " EXTENSION - (timescaledb|plpgsql)| COMMENT - EXTENSION " >/tmp/restore.list; pg_restore -U saker_admin -d "$TARGET" --no-owner -L /tmp/restore.list /tmp/restore.dump; status=$?; rm -f /tmp/restore.dump /tmp/restore.list; exit $status' <"$dir/postgres.dump"
psql_admin "$target" "SELECT timescaledb_post_restore()" >/dev/null

echo "MongoDB -> $target ..."
export MONGO_PASSWORD
"${compose[@]}" exec -T -e MONGO_PASSWORD -e SOURCE="$source_database" -e TARGET="$target" mongo sh -c \
  'mongorestore --quiet --archive --gzip --username saker_admin --password "$MONGO_PASSWORD" --authenticationDatabase admin --nsFrom "$SOURCE.*" --nsTo "$TARGET.*"' \
  <"$dir/mongo.archive.gz"

echo "restored from $dir into $target; nothing existing was touched"
