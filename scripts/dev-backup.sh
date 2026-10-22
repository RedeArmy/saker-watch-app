#!/usr/bin/env bash
# Backup of a compose stack: PostgreSQL (custom format, the whole `saker` database with its grants, policies and functions) and MongoDB (one gzip
# archive of the `saker` database), with a checksum file and a manifest. Redis is not backed up: it holds caches and call budgets only.
#   bash scripts/dev-backup.sh [directory]       (default: backups/<UTC timestamp>, mode 700, ignored by git)
# Needs POSTGRES_PASSWORD and MONGO_PASSWORD in the environment (compose needs them to render its file; the dumps use the containers' own admin
# logins, and the Mongo password reaches the container by name, never in an argument). COMPOSE_PROJECT and COMPOSE_EXTRA_FILE select another stack.
# Take it BEFORE the migrator runs in a deployment (docs/runbooks/restore.md).
set -euo pipefail
cd "$(dirname "$0")/.."

compose=(docker compose -p "${COMPOSE_PROJECT:-saker-watch}" -f deploy/docker-compose.yml ${COMPOSE_EXTRA_FILE:+-f "$COMPOSE_EXTRA_FILE"})
dir="${1:-backups/$(date -u +%Y%m%dT%H%M%SZ)}"
database="${MONGO_DATABASE:-saker}"

for name in POSTGRES_PASSWORD MONGO_PASSWORD; do
  if [[ -z "${!name:-}" ]]; then
    echo "ERROR: $name is not set" >&2
    exit 1
  fi
done

psql_admin() {
  "${compose[@]}" exec -T postgres psql -U saker_admin -d saker -At -c "$1"
  return $?
}

umask 077
mkdir -p "$dir"
chmod 700 "$dir"

echo "PostgreSQL ..."
"${compose[@]}" exec -T postgres pg_dump -U saker_admin -d saker -Fc >"$dir/postgres.dump"

echo "MongoDB ..."
export MONGO_PASSWORD
"${compose[@]}" exec -T -e MONGO_PASSWORD -e MONGO_DATABASE="$database" mongo sh -c \
  'mongodump --quiet --archive --gzip --username saker_admin --password "$MONGO_PASSWORD" --authenticationDatabase admin --db "$MONGO_DATABASE"' \
  >"$dir/mongo.archive.gz"

{
  echo "{"
  echo "  \"takenAtUtc\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\","
  echo "  \"gitCommit\": \"$(git rev-parse HEAD 2>/dev/null || echo unknown)\","
  echo "  \"postgres\": \"$(psql_admin 'SHOW server_version')\","
  echo "  \"timescaledb\": \"$(psql_admin "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'")\""
  echo "}"
} >"$dir/manifest.json"

(cd "$dir" && sha256sum postgres.dump mongo.archive.gz manifest.json >SHA256SUMS)
echo "backup written to $dir"
