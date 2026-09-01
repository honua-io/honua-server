#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCENARIO="${1:-all}"
CANDIDATE_IMAGE="${HONUA_EXAMPLES_CANDIDATE_IMAGE:-${HONUA_SERVER_IMAGE:-}}"

if [[ -n "${CANDIDATE_IMAGE}" ]]; then
  case "${CANDIDATE_IMAGE}" in
    *@sha256:*) ;;
    *) echo "effective candidate image must be digest-pinned" >&2; exit 2 ;;
  esac
  export HONUA_SERVER_IMAGE="${CANDIDATE_IMAGE}"
fi

run_stac_ops() {
  trap 'COMPOSE_PROJECT_NAME=honua-examples-stac HONUA_HTTP_PORT=18080 HONUA_GRPC_PORT=18083 POSTGRES_PORT=55432 REDIS_PORT=56381 HONUA_STORAGE_VOLUME_NAME=honua_examples_stac_storage docker compose -f "${ROOT_DIR}/docker-compose.yml" --project-directory "${ROOT_DIR}" down --remove-orphans --volumes >/dev/null 2>&1 || true' RETURN
  HONUA_STAC_DEMO_PROJECT=honua-examples-stac \
    REDIS_PORT=56381 \
    HONUA_STORAGE_VOLUME_NAME=honua_examples_stac_storage \
    bash "${ROOT_DIR}/scripts/demos/run-stac-ops-demo.sh" baseline
  curl -fsS http://127.0.0.1:18080/stac | jq -e '.links | length > 0' >/dev/null
  curl -fsS http://127.0.0.1:18080/stac/collections | jq -e '.collections | length == 2' >/dev/null
  curl -fsS http://127.0.0.1:18080/samples/stac-ops/ | grep -q '<!DOCTYPE html>'
}

run_mobile_offline() {
  trap 'COMPOSE_PROJECT_NAME=honua-examples-mobile HONUA_HTTP_PORT=18081 HONUA_GRPC_PORT=18082 POSTGRES_PORT=55433 REDIS_PORT=56380 HONUA_STORAGE_VOLUME_NAME=honua_examples_mobile_storage docker compose -f "${ROOT_DIR}/docker-compose.yml" --project-directory "${ROOT_DIR}" down --remove-orphans --volumes >/dev/null 2>&1 || true' RETURN
  HONUA_MOBILE_OFFLINE_DEMO_PROJECT=honua-examples-mobile \
    REDIS_PORT=56380 \
    HONUA_STORAGE_VOLUME_NAME=honua_examples_mobile_storage \
    bash "${ROOT_DIR}/scripts/demos/run-mobile-offline-demo.sh" baseline
  curl -fsS 'http://127.0.0.1:18081/rest/services/mobile_offline_demo/FeatureServer?f=json' \
    | jq -e '.layers | length == 2' >/dev/null
  curl -fsS 'http://127.0.0.1:18081/rest/services/mobile_offline_demo/FeatureServer/68910/query?where=1%3D1&outFields=*&returnGeometry=true&f=json' \
    | jq -e '.features | length > 0' >/dev/null
}

run_gp_local_dev() {
  local project=honua-examples-gp
  compose() {
    COMPOSE_PROJECT_NAME="${project}" HONUA_HTTP_PORT=18084 HONUA_GRPC_PORT=18085 POSTGRES_PORT=55434 REDIS_PORT=56379 \
      docker compose -f "${ROOT_DIR}/docker-compose.gp-dev.yml" --project-directory "${ROOT_DIR}" "$@"
  }
  trap 'compose down --remove-orphans --volumes >/dev/null 2>&1 || true' RETURN
  if [[ -n "${CANDIDATE_IMAGE}" ]]; then
    compose up -d --no-build postgres redis honua
  else
    compose up -d --build postgres redis honua
  fi
  for _ in $(seq 1 90); do
    [[ "$(curl -fsS http://127.0.0.1:18084/healthz/ready 2>/dev/null || true)" == "Ready" ]] && break
    sleep 2
  done
  BASE=http://127.0.0.1:18084 bash "${ROOT_DIR}/samples/gp-local-dev/submit-buffer.sh" \
    | tee /tmp/honua-examples-gp-result.txt
  grep -q 'successful' /tmp/honua-examples-gp-result.txt
}

case "${SCENARIO}" in
  stac-ops) run_stac_ops ;;
  mobile-offline) run_mobile_offline ;;
  gp-local-dev) run_gp_local_dev ;;
  all)
    run_stac_ops
    run_mobile_offline
    run_gp_local_dev
    ;;
  *) echo "usage: $0 [all|stac-ops|mobile-offline|gp-local-dev]" >&2; exit 2 ;;
esac
