#!/usr/bin/env bash
set -euo pipefail
BASE="${1:?Usage: release-smoke.sh https://your-real-connector-host}"
[[ "$BASE" =~ ^https://[a-zA-Z0-9.-]+$ ]] ||
  { echo "HTTPS host without credentials/path required"; exit 2; }
# TLS certificate chain and hostname verification MUST remain enabled.
for i in $(seq 1 30); do
  if curl --fail --silent --show-error --max-time 5 "$BASE/health/ready" >/dev/null 2>&1; then break; fi
  sleep 3
done
curl --fail --silent --show-error --max-time 10 "$BASE/health/live" >/dev/null
curl --fail --silent --show-error --max-time 10 "$BASE/health/ready" >/dev/null
code="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' --max-time 10 "$BASE/api/admin/v1/connections")"
[[ "$code" == "401" ]] || { echo "Anonymous admin access was not denied"; exit 1; }
code="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' --max-time 10 "$BASE/api/v1/quotes/00000000-0000-0000-0000-000000000001?connectionCode=unknown")"
[[ "$code" == "401" ]] || { echo "Anonymous machine access was not denied"; exit 1; }
echo "HTTPS, readiness and anonymous auth denial verified. This is not a full Entra/Dataverse E2E test."
