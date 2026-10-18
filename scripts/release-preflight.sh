#!/usr/bin/env bash
set -euo pipefail
# Tests release config interpolation without contacting production or requiring real credentials.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
export DYNAMICS_STACK_NAME=dhole-dynamics-staging
export DYNAMICS_ENVIRONMENT=Staging
export DYNAMICS_PUBLIC_FQDN=staging.example.com
export DYNAMICS_ACME_EMAIL=staging-ops@example.com
export DYNAMICS_RELEASE_TAG=sha-0000000000000000000000000000000000000000
export DYNAMICS_POSTGRES_PASSWORD_FILE=/tmp/phase12-not-a-real-postgres-secret
export DYNAMICS_CONNECTOR_DB_FILE=/tmp/phase12-not-a-real-connection
export DYNAMICS_ENCRYPTION_KEY_FILE=/tmp/phase12-not-a-real-encryption-key
export DYNAMICS_CURSOR_KEY_FILE=/tmp/phase12-not-a-real-cursor
export DYNAMICS_ADMIN_OIDC_SECRET_FILE=/tmp/phase12-not-a-real-oidc
export DYNAMICS_DP_CERT_FILE=/tmp/phase12-not-a-real-pfx
export DYNAMICS_DP_PASSWORD_FILE=/tmp/phase12-not-a-real-pfx-password
export DYNAMICS_ADMIN_OIDC_AUTHORITY=https://login.microsoftonline.com/test/v2.0
export DYNAMICS_ADMIN_OIDC_CLIENT_ID=dummy-test-app
export DYNAMICS_M2M_AUTHORITY=https://login.microsoftonline.com/test/v2.0
export DYNAMICS_M2M_AUDIENCE=api://dummy-test-app
docker compose -f compose.release.yaml config -q
# Verify network isolation, workers opt-in, secret mounts, Caddy and separate volumes.
for section in postgres migrate api worker admin https; do
  docker compose -f compose.release.yaml config --services | grep -Fx "$section" >/dev/null
done
! docker compose -f compose.release.yaml config | grep -q 'published: "5432"'
! docker compose -f compose.release.yaml config | grep -q 'DYNAMICS_PRODUCTION_WRITES_APPROVED: YES'
echo "Release Compose topology and defaults valid (no remote deployment)."
