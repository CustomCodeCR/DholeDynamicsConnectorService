#!/usr/bin/env bash
set -euo pipefail
umask 077

# Pipeline keeps pg_dump in memory and never writes an unencrypted dump to disk.
DB_NAME="${DB_NAME:-dhole_dynamics_connector}"
BACKUP_FILE="${BACKUP_FILE:?Set an absolute destination ending in .dump.age}"
: "${AGE_RECIPIENT:?Provide a verified age public recipient}"
: "${PGHOST:?Set PGHOST}"
: "${PGUSER:?Set PGUSER}"
: "${PGPASSWORD:?Provide PGPASSWORD via secure environment}"
[[ "$DB_NAME" =~ ^dhole_dynamics_connector(_[a-z0-9_]+)?$ ]] || { echo "Disallowed database"; exit 2; }
[[ "$BACKUP_FILE" == /* && "$BACKUP_FILE" == *.dump.age ]] || { echo "Invalid backup path"; exit 2; }
[[ ! -e "$BACKUP_FILE" ]] || { echo "Backup destination already exists"; exit 2; }
command -v pg_dump >/dev/null || { echo "pg_dump is required"; exit 2; }
command -v age >/dev/null || { echo "age is required"; exit 2; }

tmp="${BACKUP_FILE}.partial.$$"
trap 'rm -f -- "$tmp"' EXIT
pg_dump --format=custom --no-owner --no-acl --dbname="$DB_NAME" |
  age --recipient "$AGE_RECIPIENT" --output "$tmp"
chmod 600 "$tmp"
mv -n -- "$tmp" "$BACKUP_FILE"
if [[ -e "$tmp" ]]; then echo "Destination collision"; exit 1; fi
trap - EXIT
echo "Encrypted connector backup created (never store age identity beside archive)."
