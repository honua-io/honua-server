#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OWNED_SERVER="${ROOT_DIR}/scripts/ci/owned-server.py"
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
  local project="honua-examples-stac-$$" http grpc postgres redis
  http="$(python3 "$OWNED_SERVER" port --variable HONUA_STAC_DEMO_HTTP_PORT --ci-default 18080)"
  grpc="$(python3 "$OWNED_SERVER" port --variable HONUA_STAC_DEMO_GRPC_PORT --ci-default 18083)"
  postgres="$(python3 "$OWNED_SERVER" port --variable HONUA_STAC_DEMO_POSTGRES_PORT --ci-default 55432)"
  redis="$(python3 "$OWNED_SERVER" port --variable REDIS_PORT --ci-default 56381)"
  trap 'COMPOSE_PROJECT_NAME="$project" HONUA_HTTP_PORT="$http" HONUA_GRPC_PORT="$grpc" POSTGRES_PORT="$postgres" REDIS_PORT="$redis" HONUA_STORAGE_VOLUME_NAME="${project}_storage" docker compose -f "${ROOT_DIR}/docker-compose.yml" --project-directory "${ROOT_DIR}" down --remove-orphans --volumes >/dev/null 2>&1 || true' RETURN
  HONUA_STAC_DEMO_PROJECT="$project" HONUA_STAC_DEMO_HTTP_PORT="$http" \
    HONUA_STAC_DEMO_GRPC_PORT="$grpc" HONUA_STAC_DEMO_POSTGRES_PORT="$postgres" REDIS_PORT="$redis" \
    HONUA_STORAGE_VOLUME_NAME="${project}_storage" \
    bash "${ROOT_DIR}/scripts/demos/run-stac-ops-demo.sh" baseline
  python3 "$OWNED_SERVER" wait --container "$(docker compose -p "$project" -f "$ROOT_DIR/docker-compose.yml" ps -q honua)" \
    --container-port 8080 --url "http://127.0.0.1:$http/healthz/ready" --body Ready
  curl -fsS "http://127.0.0.1:$http/stac" | jq -e '.links | length > 0' >/dev/null
  curl -fsS "http://127.0.0.1:$http/stac/collections" | jq -e '.collections | length == 2' >/dev/null
}

run_mobile_offline() {
  local project="honua-examples-mobile-$$" http grpc postgres redis
  http="$(python3 "$OWNED_SERVER" port --variable HONUA_MOBILE_OFFLINE_DEMO_HTTP_PORT --ci-default 18081)"
  grpc="$(python3 "$OWNED_SERVER" port --variable HONUA_MOBILE_OFFLINE_DEMO_GRPC_PORT --ci-default 18082)"
  postgres="$(python3 "$OWNED_SERVER" port --variable HONUA_MOBILE_OFFLINE_DEMO_POSTGRES_PORT --ci-default 55433)"
  redis="$(python3 "$OWNED_SERVER" port --variable REDIS_PORT --ci-default 56380)"
  trap 'COMPOSE_PROJECT_NAME="$project" HONUA_HTTP_PORT="$http" HONUA_GRPC_PORT="$grpc" POSTGRES_PORT="$postgres" REDIS_PORT="$redis" HONUA_STORAGE_VOLUME_NAME="${project}_storage" docker compose -f "${ROOT_DIR}/docker-compose.yml" --project-directory "${ROOT_DIR}" down --remove-orphans --volumes >/dev/null 2>&1 || true' RETURN
  HONUA_MOBILE_OFFLINE_DEMO_PROJECT="$project" HONUA_MOBILE_OFFLINE_DEMO_HTTP_PORT="$http" \
    HONUA_MOBILE_OFFLINE_DEMO_GRPC_PORT="$grpc" HONUA_MOBILE_OFFLINE_DEMO_POSTGRES_PORT="$postgres" REDIS_PORT="$redis" \
    HONUA_STORAGE_VOLUME_NAME="${project}_storage" \
    bash "${ROOT_DIR}/scripts/demos/run-mobile-offline-demo.sh" baseline
  python3 "$OWNED_SERVER" wait --container "$(docker compose -p "$project" -f "$ROOT_DIR/docker-compose.yml" ps -q honua)" \
    --container-port 8080 --url "http://127.0.0.1:$http/healthz/ready" --body Ready
  curl -fsS "http://127.0.0.1:$http/rest/services/mobile_offline_demo/FeatureServer?f=json" \
    | jq -e '.layers | length == 2' >/dev/null
  curl -fsS "http://127.0.0.1:$http/rest/services/mobile_offline_demo/FeatureServer/68910/query?where=1%3D1&outFields=*&returnGeometry=true&f=json" \
    | jq -e '.features | length > 0' >/dev/null
}

run_gp_local_dev() {
  local sample_root="${HONUA_SAMPLES_ROOT:-${ROOT_DIR}/../honua-samples}"
  local output="${ROOT_DIR}/artifacts/validated-examples/gp-result.txt"
  if [[ ! -f "${sample_root}/samples/gp-local-dev/submit-buffer.sh" ]]; then
    echo "Set HONUA_SAMPLES_ROOT to a honua-samples checkout." >&2
    return 2
  fi
  local project="honua-examples-gp-$$" http grpc postgres redis
  http="$(python3 "$OWNED_SERVER" port --variable HONUA_EXAMPLES_GP_HTTP_PORT --ci-default 18084)"
  grpc="$(python3 "$OWNED_SERVER" port --variable HONUA_EXAMPLES_GP_GRPC_PORT --ci-default 18085)"
  postgres="$(python3 "$OWNED_SERVER" port --variable HONUA_EXAMPLES_GP_POSTGRES_PORT --ci-default 55434)"
  redis="$(python3 "$OWNED_SERVER" port --variable HONUA_EXAMPLES_GP_REDIS_PORT --ci-default 56379)"
  compose() {
    COMPOSE_PROJECT_NAME="${project}" HONUA_HTTP_PORT="$http" HONUA_GRPC_PORT="$grpc" POSTGRES_PORT="$postgres" REDIS_PORT="$redis" \
      docker compose -f "${ROOT_DIR}/docker/compose/docker-compose.gp-dev.yml" --project-directory "${ROOT_DIR}" "$@"
  }
  trap 'compose down --remove-orphans --volumes >/dev/null 2>&1 || true' RETURN
  if [[ -n "${CANDIDATE_IMAGE}" ]]; then
    compose up -d --no-build postgres redis honua
  else
    compose up -d --build postgres redis honua
  fi
  python3 "$OWNED_SERVER" wait --container "$(compose ps -q honua)" --container-port 8080 \
    --url "http://127.0.0.1:$http/healthz/ready" --body Ready --timeout 180
  mkdir -p "$(dirname "${output}")"
  BASE="http://127.0.0.1:$http" bash "${sample_root}/samples/gp-local-dev/submit-buffer.sh" \
    | tee "${output}"
  grep -q 'successful' "${output}"
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
