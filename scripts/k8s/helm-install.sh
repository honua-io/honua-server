#!/usr/bin/env bash
set -euo pipefail

CHART_PATH="${CHART_PATH:-infrastructure/helm/honua}"
RELEASE_NAME="${RELEASE_NAME:-honua}"
NAMESPACE="${NAMESPACE:-honua}"
HOSTNAME="${HOSTNAME:-honua.local}"
ADMIN_PASSWORD="${HONUA_ADMIN_PASSWORD:-change-me}"
MASTER_KEY="${SECURITY_MASTER_KEY:-dev-master-key-32chars-minimum-1234}"

command -v helm >/dev/null 2>&1 || { echo "helm is required"; exit 1; }
command -v kubectl >/dev/null 2>&1 || { echo "kubectl is required"; exit 1; }

kubectl create namespace "${NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -

helm dependency update "${CHART_PATH}"

helm upgrade --install "${RELEASE_NAME}" "${CHART_PATH}" \
  --namespace "${NAMESPACE}" \
  --set ingress.enabled=true \
  --set ingress.className=nginx \
  --set ingress.hosts[0].host="${HOSTNAME}" \
  --set postgresql.enabled=true \
  --set secret.env.HONUA_ADMIN_PASSWORD="${ADMIN_PASSWORD}" \
  --set-string secret.env.Security__ConnectionEncryption__MasterKey="${MASTER_KEY}" \
  --set config.env.HONUA_ADMIN_UI="true"

echo "Release '${RELEASE_NAME}' installed in namespace '${NAMESPACE}'"
echo "Test: curl -H \"Host: ${HOSTNAME}\" http://localhost:8080/healthz/ready"
