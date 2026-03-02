#!/usr/bin/env bash
set -euo pipefail

RELEASE_NAME="${RELEASE_NAME:-honua}"
NAMESPACE="${NAMESPACE:-honua}"

command -v helm >/dev/null 2>&1 || { echo "helm is required"; exit 1; }

helm test "${RELEASE_NAME}" --namespace "${NAMESPACE}"
