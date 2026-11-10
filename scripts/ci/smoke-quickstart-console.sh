#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${ROOT_DIR}/docker-compose.yml"
PROJECT_NAME="${HONUA_QUICKSTART_SMOKE_PROJECT:-honua-quickstart-console-smoke-$$}"
OWNED_SERVER="${ROOT_DIR}/scripts/ci/owned-server.py"
SMOKE_OVERRIDE_FILE=""
SMOKE_ENV_FILE=""
HONUA_HTTP_PORT="$(python3 "${OWNED_SERVER}" port --variable HONUA_QUICKSTART_SMOKE_HTTP_PORT --ci-default 18080)"
HONUA_GRPC_PORT="$(python3 "${OWNED_SERVER}" port --variable HONUA_QUICKSTART_SMOKE_GRPC_PORT --ci-default 18081)"
HONUA_CONSOLE_PORT="$(python3 "${OWNED_SERVER}" port --variable HONUA_QUICKSTART_SMOKE_CONSOLE_PORT --ci-default 15174)"
POSTGRES_PORT="$(python3 "${OWNED_SERVER}" port --variable HONUA_QUICKSTART_SMOKE_POSTGRES_PORT --ci-default 15432)"
REDIS_PORT="$(python3 "${OWNED_SERVER}" port --variable HONUA_QUICKSTART_SMOKE_REDIS_PORT --ci-default 16379)"
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
    docker compose --env-file "${SMOKE_ENV_FILE}" --profile console "${compose_files[@]}" --project-directory "${ROOT_DIR}" "$@"
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
  if [[ -n "${SMOKE_ENV_FILE}" ]]; then
    rm -f "${SMOKE_ENV_FILE}" >/dev/null 2>&1 || true
  fi
  return "${status}"
}
trap cleanup EXIT

compose_down() {
  compose down --remove-orphans --volumes >/dev/null 2>&1 || true
}

wait_for_url() {
  local url="$1" service="$2" attempts="${3:-60}" container
  local response_options=()
  if [[ -n "${4:-}" ]]; then response_options+=(--redirect-path "$4"); fi
  container="$(compose ps -q "$service")"
  python3 "${OWNED_SERVER}" wait --container "$container" --container-port 8080 \
    --url "$url" --timeout "$((attempts * 2))" "${response_options[@]}"
}

assert_all_health_checks() {
  local attempts="${1:-60}"
  for _ in $(seq 1 "${attempts}"); do
    if curl -fsS -H "X-API-Key: ${ADMIN_PASSWORD}" \
      "http://127.0.0.1:${HONUA_HTTP_PORT}/monitoring/health/comprehensive" \
      | jq -e --arg verified_after "${AUDIT_VERIFIED_AFTER}" '.status == "Healthy" and (.entries | type == "object")
          and (.entries | has("audit-chain-integrity"))
          and (.entries["audit-chain-integrity"].data.last_verified_at | type == "string" and length > 0)
          and (.entries["audit-chain-integrity"].data.last_verified_at > $verified_after)
          and (.entries | all(.status == "Healthy"))' >/dev/null; then
      echo "all registered health checks: Healthy (audit chain verified)"
      return 0
    fi
    sleep 2
  done
  echo "health checks: timed out waiting for Healthy and verified audit chain" >&2
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

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required for proposal smoke assertions." >&2
  exit 2
fi

SMOKE_ENV_FILE="$(mktemp)"
python3 "${ROOT_DIR}/scripts/docker/quickstart.py" --env-file "${SMOKE_ENV_FILE}" --init-only
SMOKE_OVERRIDE_FILE="$(mktemp)"
cat >"${SMOKE_OVERRIDE_FILE}" <<'YAML'
services:
  honua:
    environment:
      AuditLog__ChainVerification__InitialDelay: "00:00:02"
      AuditLog__ChainVerification__Interval: "00:00:02"
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

wait_for_url "http://127.0.0.1:${HONUA_HTTP_PORT}/healthz/ready" "honua" 90
assert_proposal_roundtrip
# Require a verification in a later second than the completed audit-producing request.
AUDIT_VERIFIED_AFTER="$(date -u +%Y-%m-%dT%H:%M:%S).9999999+00:00"
assert_all_health_checks
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/version.json" "console" 60
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/operate" "console" 60 "/auth/login"
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/operate/health" "console" 60 "/auth/login"
wait_for_url "http://127.0.0.1:${HONUA_CONSOLE_PORT}/operate/copilot" "console" 60 "/auth/login"

compose ps
