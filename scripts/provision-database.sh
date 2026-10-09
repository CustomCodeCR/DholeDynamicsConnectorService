#!/usr/bin/env bash
set -euo pipefail

# Usage: DB_NAME=dhole_dynamics PGHOST=... PGUSER=... PGPASSWORD=... bash scripts/provision-database.sh
# Use the existing Dhole PostgreSQL cluster (no new unbacked postgres volume).
DB_NAME="${DB_NAME:-dhole_dynamics}"
if [[ ! "$DB_NAME" =~ ^[a-z][a-z0-9_]{2,62}$ ]]; then
  echo "Invalid DB_NAME" >&2
  exit 1
fi
command -v psql >/dev/null || { echo "psql is required" >&2; exit 1; }
: "${PGHOST:?PGHOST is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGPASSWORD:?PGPASSWORD must be provided securely}"

psql -X -v ON_ERROR_STOP=1 -d postgres -v db_name="$DB_NAME" <<'SQL'
SELECT format('CREATE DATABASE %I', :'db_name')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name')
\gexec
SQL
psql -X -v ON_ERROR_STOP=1 -d "$DB_NAME" -f sql/001_init.sql
echo "Schema ready in database $DB_NAME"
