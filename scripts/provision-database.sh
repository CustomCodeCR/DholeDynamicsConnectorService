#!/usr/bin/env bash
set -euo pipefail
umask 077

# Create a separate logical DB, never reuse the Dhole application database.
# Production operators should provision an independent role and grants out of band.
DB_NAME="${DB_NAME:-dhole_dynamics_connector}"
if [[ ! "$DB_NAME" =~ ^dhole_dynamics_connector(_[a-z0-9_]+)?$ ]]; then
  echo "Unsafe DB_NAME: use dhole_dynamics_connector[_suffix]" >&2; exit 2
fi
command -v psql >/dev/null || { echo "psql is required" >&2; exit 2; }
: "${PGHOST:?Set PGHOST}"
: "${PGUSER:?Set provisioning PGUSER}"
: "${PGPASSWORD:?Set password from secret manager}"
psql -X -v ON_ERROR_STOP=1 -d postgres -v db_name="$DB_NAME" <<'SQL'
SELECT format('CREATE DATABASE %I', :'db_name')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name')
\gexec
SQL
echo "Dedicated database exists: $DB_NAME; run connector-tools migrate using a migrator account."
