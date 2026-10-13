#!/usr/bin/env bash
# Dev only: creates the least-privilege runtime logins after the migrator has run (ADR-0010, ADR-0021).
#   PostgreSQL: saker_api (member of api_role) and saker_worker (member of worker_role)
#   MongoDB:    saker_app (readWrite on the application database)
# Passwords come from the environment, never from the repository:
#   POSTGRES_PASSWORD MONGO_PASSWORD (bootstrap admins, as in deploy/.env)
#   API_DB_PASSWORD WORKER_DB_PASSWORD MONGO_APP_PASSWORD (the new logins)
# Production creates the same logins with its own secret manager; this script only serves the local compose stack.
set -euo pipefail
cd "$(dirname "$0")/.."

compose=(docker compose -p saker-watch -f deploy/docker-compose.yml)
database="${MONGO_DATABASE:-saker}"

for name in POSTGRES_PASSWORD MONGO_PASSWORD API_DB_PASSWORD WORKER_DB_PASSWORD MONGO_APP_PASSWORD; do
  if [[ -z "${!name:-}" ]]; then
    echo "ERROR: $name is not set" >&2
    exit 1
  fi
done

# POSTGRES_PASSWORD is only needed by compose to render the file; the exec below uses the container's own admin login.
"${compose[@]}" exec -T postgres psql -U saker_admin -d saker -v ON_ERROR_STOP=1 \
  -v api_pw="$API_DB_PASSWORD" -v worker_pw="$WORKER_DB_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L IN ROLE api_role', 'saker_api', :'api_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'saker_api') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L IN ROLE worker_role', 'saker_worker', :'worker_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'saker_worker') \gexec
SELECT format('ALTER ROLE saker_api PASSWORD %L', :'api_pw') \gexec
SELECT format('ALTER ROLE saker_worker PASSWORD %L', :'worker_pw') \gexec
-- Role settings are not inherited through membership, so limits are set on the logins themselves.
ALTER ROLE saker_api SET statement_timeout = '15s';
ALTER ROLE saker_api SET lock_timeout = '5s';
ALTER ROLE saker_api SET idle_in_transaction_session_timeout = '30s';
ALTER ROLE saker_worker SET statement_timeout = '10min';
ALTER ROLE saker_worker SET lock_timeout = '10s';
ALTER ROLE saker_worker SET idle_in_transaction_session_timeout = '2min';
SQL

# MongoDB 5+ ships mongosh; 4.4 (used on CPUs without AVX) ships the legacy mongo shell, which has no `process.env`,
# so the values are expanded by the container shell. Dev passwords must therefore be alphanumeric.
"${compose[@]}" exec -T \
  -e ADMIN_PW="$MONGO_PASSWORD" -e APP_PW="$MONGO_APP_PASSWORD" -e APP_DB="$database" \
  mongo sh -c '
    shell=$(command -v mongosh || command -v mongo)
    "$shell" --quiet -u saker_admin -p "$ADMIN_PW" --authenticationDatabase admin --eval "
      var target = db.getSiblingDB(\"$APP_DB\");
      var roles = [{ role: \"readWrite\", db: \"$APP_DB\" }];
      if (target.getUser(\"saker_app\")) {
        target.updateUser(\"saker_app\", { pwd: \"$APP_PW\", roles: roles });
      } else {
        target.createUser({ user: \"saker_app\", pwd: \"$APP_PW\", roles: roles });
      }
    " >/dev/null
  '

echo "Logins ready: saker_api, saker_worker (PostgreSQL) and saker_app (MongoDB)."
