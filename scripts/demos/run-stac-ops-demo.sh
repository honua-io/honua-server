#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCENARIO="${1:-${HONUA_STAC_DEMO_SCENARIO:-baseline}}"

case "$SCENARIO" in
  baseline|stale-cache)
    ;;
  *)
    echo "Unsupported scenario: $SCENARIO" >&2
    echo "Usage: $0 [baseline|stale-cache]" >&2
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

export COMPOSE_PROJECT_NAME="${HONUA_STAC_DEMO_PROJECT:-honua-stac-demo}"
export HONUA_HTTP_PORT="${HONUA_STAC_DEMO_HTTP_PORT:-18080}"
export POSTGRES_PORT="${HONUA_STAC_DEMO_POSTGRES_PORT:-55432}"
export HONUA_CONNECTION_ENCRYPTION_MASTER_KEY="${HONUA_CONNECTION_ENCRYPTION_MASTER_KEY:-stac-ops-demo-master-key-with-32-chars}"
export HONUA_CONNECTION_ENCRYPTION_SALT="${HONUA_CONNECTION_ENCRYPTION_SALT:-c3RhYy1vcHMtZGVtby1zYWx0LXN0YWJsZS0wMDE=}"

readonly BASE_URL="http://localhost:${HONUA_HTTP_PORT}"
readonly SAMPLE_URL="${BASE_URL}/samples/stac-ops/"
readonly READY_URL="${BASE_URL}/healthz/ready"
readonly BASE_SEED="${ROOT_DIR}/tests/seed/stac-ops-demo-v1.sql"
readonly DELTA_SEED="${ROOT_DIR}/tests/seed/stac-ops-demo-cache-delta.sql"
readonly COMPOSE_FILE="${ROOT_DIR}/docker-compose.yml"
readonly DB_SERVICE="${HONUA_STAC_DEMO_POSTGRES_SERVICE:-postgres}"
readonly DB_USER="${HONUA_STAC_DEMO_DB_USER:-honua_user}"
readonly DB_PASSWORD="${HONUA_STAC_DEMO_DB_PASSWORD:-honua_password}"
readonly DB_NAME="${HONUA_STAC_DEMO_DB_NAME:-honua_dev}"

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

warm_metadata_cache() {
  curl -fsS "${BASE_URL}/stac" >/dev/null
  curl -fsS "${BASE_URL}/stac/collections" >/dev/null
}

restart_honua() {
  compose restart honua >/dev/null
  wait_for_ready
}

echo "Starting isolated STAC ops demo stack (${COMPOSE_PROJECT_NAME}) on ${BASE_URL}."
compose down --remove-orphans --volumes >/dev/null 2>&1 || true
compose up -d --build postgres honua >/dev/null
wait_for_ready

echo "Applying baseline seed: ${BASE_SEED}"
apply_sql "${BASE_SEED}"

echo "Restarting Honua to clear in-memory output cache."
restart_honua

if [[ "${SCENARIO}" == "stale-cache" ]]; then
  echo "Warming STAC metadata cache before applying delta."
  warm_metadata_cache

  echo "Applying stale-cache delta: ${DELTA_SEED}"
  apply_sql "${DELTA_SEED}"
fi

cat <<EOF

STAC ops demo is ready.

Scenario: ${SCENARIO}
Sample URL: ${SAMPLE_URL}

Expected signals:
- baseline: one healthy collection, one warning collection, passing search/paging probes.
- stale-cache: baseline signals plus discovery freshness and temporal-drift warnings for cached collection metadata versus live item timestamps.

Cleanup:
COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME} HONUA_HTTP_PORT=${HONUA_HTTP_PORT} POSTGRES_PORT=${POSTGRES_PORT} docker compose -f "${COMPOSE_FILE}" --project-directory "${ROOT_DIR}" down --remove-orphans --volumes
EOF
