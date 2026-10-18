#!/usr/bin/env bash
set -euo pipefail
umask 077
# Environment-specific deployment, run on its dedicated host AFTER environment approval.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${1:?Usage: bash scripts/release-deploy.sh /path/to/secured.env}"
[[ "$ENV_FILE" == /* && -r "$ENV_FILE" ]] || { echo "Absolute readable release env file required"; exit 2; }
[[ "$(stat -c %a "$ENV_FILE")" == "600" || "$(stat -c %a "$ENV_FILE")" == "400" ]] ||
  { echo "Release env file must be chmod 600 or 400"; exit 2; }
set -a; source "$ENV_FILE"; set +a
[[ "$DYNAMICS_ENVIRONMENT" == "Staging" || "$DYNAMICS_ENVIRONMENT" == "Production" ]] ||
  { echo "Staging or Production environment required"; exit 2; }
[[ "$DYNAMICS_RELEASE_TAG" =~ ^sha-[0-9a-f]{40}$ ]] ||
  { echo "Release images must use the exact commit SHA tag"; exit 2; }
[[ "$DYNAMICS_STACK_NAME" =~ ^dhole-dynamics-(staging|production)$ ]] ||
  { echo "Dedicated project name required"; exit 2; }
if [[ "$DYNAMICS_ENVIRONMENT" == "Production" ]]; then
  [[ "${DYNAMICS_GO_LIVE_APPROVED:-NO}" == "YES" &&
      "${DYNAMICS_GO_LIVE_TICKET:-}" =~ ^[A-Za-z0-9][A-Za-z0-9_./-]{11,119}$ ]] ||
      { echo "Production deployment requires independent explicit change approval"; exit 2; }
fi
cd "$ROOT"
export COMPOSE_PROJECT_NAME="$DYNAMICS_STACK_NAME"
compose=(docker compose --env-file "$ENV_FILE" -f compose.release.yaml -p "$DYNAMICS_STACK_NAME")
"${compose[@]}" config -q
"${compose[@]}" pull
# A failure of the migration never touches the previous healthy running API deployment.
"${compose[@]}" up -d --no-deps postgres
"${compose[@]}" run --rm migrate
# Capture previously running image for controlled rollback. No destructive volume operation.
old_image="$("${compose[@]}" images -q api 2>/dev/null | head -1 || true)"
"${compose[@]}" up -d --no-deps api worker admin https
if ! bash scripts/release-smoke.sh "https://$DYNAMICS_PUBLIC_FQDN"; then
  echo "Smoke failed: disable the new stack or restore a previous approved image; DB migrations are forward-only."
  echo "Previous API image ID: ${old_image:-unknown}. Never run compose down -v."
  exit 1
fi
echo "Release image health verified; verify OIDC/M2M staging checklist before go-live."
