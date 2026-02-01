#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NAMESPACE="${NAMESPACE:-honua}"
MANIFEST_PATH="${MANIFEST_PATH:-${SCRIPT_DIR}/postgis.yaml}"

command -v kubectl >/dev/null 2>&1 || { echo "kubectl is required"; exit 1; }

kubectl create namespace "${NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -
kubectl -n "${NAMESPACE}" apply -f "${MANIFEST_PATH}"
kubectl -n "${NAMESPACE}" rollout status deployment/honua-postgis --timeout=120s

echo "PostGIS is running in namespace '${NAMESPACE}'"
