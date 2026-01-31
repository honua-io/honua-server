#!/usr/bin/env bash
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-honua}"

command -v kind >/dev/null 2>&1 || { echo "kind is required"; exit 1; }

kind delete cluster --name "${CLUSTER_NAME}"
