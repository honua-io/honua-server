#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${ROOT_DIR}/docker-compose.yml"
PROJECT_NAME="${HONUA_QUICKSTART_SMOKE_PROJECT:-honua-quickstart-console-smoke}"
SMOKE_OVERRIDE_FILE=""
HONUA_HTTP_PORT="${HONUA_QUICKSTART_SMOKE_HTTP_PORT:-18080}"
HONUA_GRPC_PORT="${HONUA_QUICKSTART_SMOKE_GRPC_PORT:-18081}"
HONUA_CONSOLE_PORT="${HONUA_QUICKSTART_SMOKE_CONSOLE_PORT:-15174}"
POSTGRES_PORT="${HONUA_QUICKSTART_SMOKE_POSTGRES_PORT:-15432}"
REDIS_PORT="${HONUA_QUICKSTART_SMOKE_REDIS_PORT:-16379}"
ADMIN_PASSWORD="${HONUA_ADMIN_PASSWORD:-quickstart-admin-password}"

compose() {
  local compose_files=(-f "${COMPOSE_FILE}")
  if [[ -n "${SMOKE_OVERRIDE_FILE}" ]]; then
    compose_files+=(-f "${SMOKE_OVERRIDE_FILE}")
  fi

  COMPOSE_PROJECT_NAME="${PROJECT_NAME}" \
  HONUA_HTTP_PORT="${HONUA_HTTP_PORT}" \
  HONUA_GRPC_PORT="${HONUA_GRPC_PORT}" \
  HONUA_CONSOLE_PORT="${HONUA_CONSOLE_PORT}" \
  POSTGRES_PORT="${POSTGRES_PORT}" \
  REDIS_PORT="${REDIS_PORT}" \
  HONUA_ADMIN_PASSWORD="${ADMIN_PASSWORD}" \
  HONUA_DEV_GRANT_EDITION="${HONUA_DEV_GRANT_EDITION:-Enterprise}" \
  HONUA_ENABLE_OBSERVABILITY_TEST_SEED="${HONUA_ENABLE_OBSERVABILITY_TEST_SEED:-true}" \
  HONUA_CONSOLE_IMAGE="${HONUA_CONSOLE_IMAGE}" \
    docker compose --profile console "${compose_files[@]}" --project-directory "${ROOT_DIR}" "$@"
}

cleanup() {
  local status=$?
  if [[ "${status}" != "0" ]]; then
    compose ps || true
    compose logs --no-color --tail=200 || true
  fi
  compose_down
  if [[ -n "${SMOKE_OVERRIDE_FILE}" ]]; then
    rm -f "${SMOKE_OVERRIDE_FILE}" >/dev/null 2>&1 || true
  fi
  return "${status}"
}
trap cleanup EXIT

compose_down() {
  compose down --remove-orphans --volumes >/dev/null 2>&1 || true
}

wait_for_url() {
  local url="$1"
  local label="$2"
  local attempts="${3:-60}"

  for _ in $(seq 1 "${attempts}"); do
    if curl -fsS "${url}" >/dev/null 2>&1; then
      echo "${label}: ok"
      return 0
    fi

    sleep 2
  done

  echo "${label}: timed out waiting for ${url}" >&2
  return 1
}

assert_proposal_roundtrip() {
  local base_url="http://127.0.0.1:${HONUA_HTTP_PORT}"
  local response proposal_id proposals

  response="$(
    curl -fsS \
      -X POST \
      -H "X-API-Key: ${ADMIN_PASSWORD}" \
      -H "Content-Type: application/json" \
      -d '{"reason":"Quickstart Console smoke proposal","correlationId":"quickstart-console-smoke"}' \
      "${base_url}/api/v1/admin/platform-release/converge"
  )"

  proposal_id="$(
    printf '%s' "${response}" \
      | jq -r '.targets[]? | select(.proposalId != null) | .proposalId' \
      | head -n 1
  )"

  if [[ -z "${proposal_id}" || "${proposal_id}" == "null" ]]; then
    echo "proposal roundtrip: converge did not return a proposalId" >&2
    printf '%s\n' "${response}" >&2
    return 1
  fi

  proposals="$(
    curl -fsS \
      -H "X-API-Key: ${ADMIN_PASSWORD}" \
      "${base_url}/api/v1/admin/proposals"
  )"

  printf '%s' "${proposals}" \
    | jq -e --arg id "${proposal_id}" \
      '.proposals[]? | select(.proposalId == $id and .status == "AwaitingApproval")' \
    >/dev/null

  echo "proposal roundtrip: ok (${proposal_id})"
}

if [[ -z "${HONUA_CONSOLE_IMAGE:-}" ]]; then
  echo "HONUA_CONSOLE_IMAGE must point to a compatible published honua-console image." >&2
  exit 2
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required for proposal smoke assertions." >&2
  exit 2
fi

SMOKE_OVERRIDE_FILE="$(mktemp)"
cat >"${SMOKE_OVERRIDE_FILE}" <<'YAML'
services:
  honua:
    environment:
      ControlPlane__PlatformRelease__Version: "2026.07.0-smoke"
      ControlPlane__PlatformRelease__ServingArtifactReference: "ghcr.io/honua-io/honua-server:quickstart-smoke"
      ControlPlane__PlatformRelease__Workers__0__ArtifactReference: "ghcr.io/honua-io/honua-worker:quickstart-smoke"
      ControlPlane__DeployTargets__0__TargetId: "quickstart-smoke-target"
      ControlPlane__DeployTargets__0__TargetKind: "Kubernetes"
      ControlPlane__DeployTargets__0__Backend: "honua-gitops-kubernetes"
      ControlPlane__DeployTargets__0__Environment: "quickstart-smoke"
      ControlPlane__DeployTargets__0__TargetName: "honua-server"
YAML

compose_down
compose up -d --build postgres redis honua console

wait_for_url "http://127.0.0.1:${HONUA_HTTP_PORT}/healthz/ready" "honua ready" 90
assert_proposal_roundtrip
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/version.json" "console version" 60
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/operate" "console operate" 60
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/operate/health" "console operate health" 60
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/operate/copilot" "console operate copilot" 60

compose ps
