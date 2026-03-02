#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NAMESPACE="${NAMESPACE:-honua}"
MANIFEST_PATH="${MANIFEST_PATH:-${SCRIPT_DIR}/postgis.yaml}"

command -v kubectl >/dev/null 2>&1 || { echo "kubectl is required"; exit 1; }

kubectl create namespace "${NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -
kubectl -n "${NAMESPACE}" apply -f "${MANIFEST_PATH}"
kubectl -n "${NAMESPACE}" rollout status deployment/honua-postgis --timeout=120s

wait_for_query_ready() {
  local max_attempts="${POSTGIS_READY_MAX_ATTEMPTS:-60}"
  local retry_seconds="${POSTGIS_READY_RETRY_SECONDS:-2}"
  local attempt=1

  while (( attempt <= max_attempts )); do
    if kubectl -n "${NAMESPACE}" exec deployment/honua-postgis -- sh -c "\
      export PGPASSWORD=honua; \
      psql -h 127.0.0.1 -U honua -d honua -tAc 'SELECT 1'" >/dev/null 2>&1; then
      return 0
    fi

    sleep "${retry_seconds}"
    ((attempt++))
  done

  echo "PostGIS did not become query-ready in namespace '${NAMESPACE}'" >&2
  kubectl -n "${NAMESPACE}" logs deployment/honua-postgis --tail=200 || true
  return 1
}

wait_for_query_ready

# Ensure both PostGIS extensions required by Terraform smoke checks exist.
kubectl -n "${NAMESPACE}" exec deployment/honua-postgis -- sh -c "\
  export PGPASSWORD=honua; \
  psql -h 127.0.0.1 -U honua -d honua -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION IF NOT EXISTS postgis;'; \
  psql -h 127.0.0.1 -U honua -d honua -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION IF NOT EXISTS postgis_raster;'" >/dev/null

echo "PostGIS is running in namespace '${NAMESPACE}'"
