#!/usr/bin/env bash
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-honua}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HTTP_PORT="${KIND_HTTP_PORT:-8080}"
HTTPS_PORT="${KIND_HTTPS_PORT:-8443}"
CONFIG_PATH="${KIND_CONFIG:-}"

command -v kind >/dev/null 2>&1 || { echo "kind is required"; exit 1; }
command -v kubectl >/dev/null 2>&1 || { echo "kubectl is required"; exit 1; }

if [ -z "${CONFIG_PATH}" ]; then
  CONFIG_PATH="$(mktemp)"
  cat <<EOF > "${CONFIG_PATH}"
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    extraPortMappings:
      - containerPort: 80
        hostPort: ${HTTP_PORT}
        protocol: TCP
      - containerPort: 443
        hostPort: ${HTTPS_PORT}
        protocol: TCP
EOF
  trap 'rm -f "${CONFIG_PATH}"' EXIT
fi

if kind get clusters | grep -q "^${CLUSTER_NAME}$"; then
  echo "kind cluster '${CLUSTER_NAME}' already exists"
else
  kind create cluster --name "${CLUSTER_NAME}" --config "${CONFIG_PATH}"
fi

kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/main/deploy/static/provider/kind/deploy.yaml
kubectl -n ingress-nginx rollout status deployment/ingress-nginx-controller --timeout=120s

echo "kind cluster '${CLUSTER_NAME}' ready"
echo "ingress-nginx is installed and listening on localhost:${HTTP_PORT} (HTTP) and :${HTTPS_PORT} (HTTPS)"
