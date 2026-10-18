#!/usr/bin/env bash
set -euo pipefail
# Rollback is an explicit, reviewed image-only recovery. It NEVER downgrades PostgreSQL.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${1:?Usage: release-rollback.sh /absolute/release.env sha-previous40hex}"
OLD_TAG="${2:?Provide previous known-good SHA tag}"
[[ "$OLD_TAG" =~ ^sha-[0-9a-f]{40}$ && "$ENV_FILE" == /* && -r "$ENV_FILE" ]] ||
  { echo "A known-good immutable tag and release env file are required"; exit 2; }
[[ "$(stat -c %a "$ENV_FILE")" == "600" || "$(stat -c %a "$ENV_FILE")" == "400" ]] ||
  { echo "Release file permissions are insecure"; exit 2; }
[[ "${DYNAMICS_ROLLBACK_APPROVED:-NO}" == "YES" ]] ||
  { echo "Rollback requires explicit DYNAMICS_ROLLBACK_APPROVED=YES"; exit 2; }
set -a; source "$ENV_FILE"; set +a
[[ "$DYNAMICS_ENVIRONMENT" == Staging || "$DYNAMICS_ENVIRONMENT" == Production ]] || exit 2
cd "$ROOT"
export DYNAMICS_RELEASE_TAG="$OLD_TAG"
docker compose --env-file "$ENV_FILE" -f compose.release.yaml -p "$DYNAMICS_STACK_NAME" \
  up -d --no-deps api worker admin
bash scripts/release-smoke.sh "https://$DYNAMICS_PUBLIC_FQDN"
echo "Image rolled back without modifying or dropping the PostgreSQL schema."
