#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCENARIO="${1:-${HONUA_MOBILE_OFFLINE_DEMO_SCENARIO:-baseline}}"

case "$SCENARIO" in
  baseline|conflict-after-download)
    ;;
  *)
    echo "Unsupported scenario: $SCENARIO" >&2
    echo "Usage: $0 [baseline|conflict-after-download]" >&2
    exit 1
    ;;
esac

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required." >&2
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "docker compose v2 is required." >&2
  exit 1
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required." >&2
  exit 1
fi

export COMPOSE_PROJECT_NAME="${HONUA_MOBILE_OFFLINE_DEMO_PROJECT:-honua-mobile-offline-demo}"
export HONUA_HTTP_PORT="${HONUA_MOBILE_OFFLINE_DEMO_HTTP_PORT:-18081}"
export POSTGRES_PORT="${HONUA_MOBILE_OFFLINE_DEMO_POSTGRES_PORT:-55433}"
export HONUA_CONNECTION_ENCRYPTION_MASTER_KEY="${HONUA_CONNECTION_ENCRYPTION_MASTER_KEY:-mobile-offline-demo-master-key-32c}"
export HONUA_CONNECTION_ENCRYPTION_SALT="${HONUA_CONNECTION_ENCRYPTION_SALT:-bW9iaWxlLW9mZmxpbmUtZGVtby1zYWx0LTAwMQ==}"

readonly BASE_URL="http://localhost:${HONUA_HTTP_PORT}"
readonly READY_URL="${BASE_URL}/healthz/ready"
readonly SERVICE_URL="${BASE_URL}/rest/services/mobile_offline_demo/FeatureServer?f=json"
readonly LAYER_URL="${BASE_URL}/rest/services/mobile_offline_demo/FeatureServer/68910?f=json"
readonly QUERY_URL="${BASE_URL}/rest/services/mobile_offline_demo/FeatureServer/68910/query?where=1%3D1&outFields=*&returnGeometry=true&f=json"
readonly BASE_SEED="${ROOT_DIR}/tests/seed/mobile-offline-demo-v1.sql"
readonly CONFLICT_SEED="${ROOT_DIR}/tests/seed/mobile-offline-demo-conflict-delta.sql"
readonly COMPOSE_FILE="${ROOT_DIR}/docker-compose.yml"
readonly DB_SERVICE="${HONUA_MOBILE_OFFLINE_DEMO_POSTGRES_SERVICE:-postgres}"
readonly DB_USER="${HONUA_MOBILE_OFFLINE_DEMO_DB_USER:-honua_user}"
readonly DB_PASSWORD="${HONUA_MOBILE_OFFLINE_DEMO_DB_PASSWORD:-honua_password}"
readonly DB_NAME="${HONUA_MOBILE_OFFLINE_DEMO_DB_NAME:-honua_dev}"

compose() {
  docker compose -f "${COMPOSE_FILE}" --project-directory "${ROOT_DIR}" "$@"
}

wait_for_ready() {
  local attempt
  for attempt in $(seq 1 90); do
    if [[ "$(curl -fsS "${READY_URL}" 2>/dev/null || true)" == "Ready" ]]; then
      return 0
    fi

    sleep 2
  done

  echo "Honua did not become ready at ${READY_URL}." >&2
  exit 1
}

apply_sql() {
  local sql_file="$1"
  compose exec -T \
    -e PGPASSWORD="${DB_PASSWORD}" \
    "${DB_SERVICE}" \
    psql -v ON_ERROR_STOP=1 -U "${DB_USER}" -d "${DB_NAME}" < "${sql_file}"
}

restart_honua() {
  compose restart honua >/dev/null
  wait_for_ready
}

smoke_fixture() {
  curl -fsS "${SERVICE_URL}" >/dev/null
  curl -fsS "${LAYER_URL}" >/dev/null
  curl -fsS "${QUERY_URL}" >/dev/null
}

echo "Starting isolated mobile offline demo stack (${COMPOSE_PROJECT_NAME}) on ${BASE_URL}."
compose down --remove-orphans --volumes >/dev/null 2>&1 || true
compose up -d --build postgres honua >/dev/null
wait_for_ready

echo "Applying baseline seed: ${BASE_SEED}"
apply_sql "${BASE_SEED}"

echo "Restarting Honua to clear in-memory output cache."
restart_honua

echo "Checking fixture metadata and feature query endpoints."
smoke_fixture

if [[ "${SCENARIO}" == "conflict-after-download" ]]; then
  echo "Apply the mobile package download now, then press Enter to advance the server conflict target."
  read -r
  echo "Applying conflict delta: ${CONFLICT_SEED}"
  apply_sql "${CONFLICT_SEED}"
  smoke_fixture
fi

cat <<EOF

Mobile offline demo fixture is ready.

Scenario: ${SCENARIO}
Service metadata: ${SERVICE_URL}
Editable layer metadata: ${LAYER_URL}
Editable layer query: ${QUERY_URL}
Create replica path: ${BASE_URL}/rest/services/mobile_offline_demo/FeatureServer/createReplica

Expected signals:
- baseline: service mobile_offline_demo has layers 68910 and 68920, field/form metadata, and sync_version = 1 seed records.
- conflict-after-download: objectid 6891002 advances to sync_version = 2 after the client captures the baseline package.

Cleanup:
COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME} HONUA_HTTP_PORT=${HONUA_HTTP_PORT} POSTGRES_PORT=${POSTGRES_PORT} docker compose -f "${COMPOSE_FILE}" --project-directory "${ROOT_DIR}" down --remove-orphans --volumes
EOF
