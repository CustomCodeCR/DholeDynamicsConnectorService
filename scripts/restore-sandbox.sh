#!/usr/bin/env bash
set -euo pipefail
umask 077

# Only restores to a separately CREATED EMPTY sandbox database.
RESTORE_DB_NAME="${RESTORE_DB_NAME:?Use an empty connector restore sandbox DB}"
BACKUP_FILE="${BACKUP_FILE:?Provide an existing .dump.age archive}"
AGE_IDENTITY_FILE="${AGE_IDENTITY_FILE:?Provide age identity file outside Git}"
[[ "$RESTORE_DB_NAME" =~ ^dhole_dynamics_connector_restore_[a-z0-9_]+$ ]] ||
  { echo "Only dedicated restore sandboxes are allowed"; exit 2; }
[[ "$BACKUP_FILE" == *.dump.age && -f "$BACKUP_FILE" ]] || { echo "Invalid archive"; exit 2; }
[[ -r "$AGE_IDENTITY_FILE" ]] || { echo "Missing decryption identity"; exit 2; }
[[ "${ALLOW_SANDBOX_RESTORE:-}" == "YES" ]] || { echo "Explicit ALLOW_SANDBOX_RESTORE=YES required"; exit 2; }
: "${PGHOST:?Set PGHOST}"; : "${PGUSER:?Set PGUSER}"; : "${PGPASSWORD:?Set PGPASSWORD}"
command -v age >/dev/null || exit 2
command -v pg_restore >/dev/null || exit 2
command -v psql >/dev/null || exit 2

exists="$(psql -X -A -t -v ON_ERROR_STOP=1 -d "$RESTORE_DB_NAME"   -c "SELECT count(*) FROM pg_namespace WHERE nspname = 'dynamics'")"
[[ "$exists" == "0" ]] || { echo "Destination contains connector schema; refuse overwrite"; exit 2; }
age --decrypt --identity "$AGE_IDENTITY_FILE" "$BACKUP_FILE" |
  pg_restore --no-owner --no-acl --single-transaction --exit-on-error     --dbname="$RESTORE_DB_NAME"
psql -X -A -t -v ON_ERROR_STOP=1 -d "$RESTORE_DB_NAME"   -c 'SELECT count(*) FROM dynamics.schema_migrations' >/dev/null
echo "Encrypted archive restored into an empty sandbox; validate row counts and decryption with separate test keys."
