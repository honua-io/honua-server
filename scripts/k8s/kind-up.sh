#!/usr/bin/env bash
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-honua}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_PATH="${SCRIPT_DIR}/kind-config.yaml"

command -v kind >/dev/null 2>&1 || { echo "kind is required"; exit 1; }
command -v kubectl >/dev/null 2>&1 || { echo "kubectl is required"; exit 1; }

if kind get clusters | grep -q "^${CLUSTER_NAME}$"; then
  echo "kind cluster '${CLUSTER_NAME}' already exists"
else
  kind create cluster --name "${CLUSTER_NAME}" --config "${CONFIG_PATH}"
fi

kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/main/deploy/static/provider/kind/deploy.yaml
kubectl -n ingress-nginx rollout status deployment/ingress-nginx-controller --timeout=120s

echo "kind cluster '${CLUSTER_NAME}' ready"
echo "ingress-nginx is installed and listening on localhost:8080 (HTTP) and :8443 (HTTPS)"
