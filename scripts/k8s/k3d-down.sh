#!/usr/bin/env bash
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-honua-k3d}"

command -v k3d >/dev/null 2>&1 || { echo "k3d is required"; exit 1; }

k3d cluster delete "${CLUSTER_NAME}"
