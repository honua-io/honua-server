#!/usr/bin/env bash
set -euo pipefail

CHART_PATH="${CHART_PATH:-infrastructure/helm/honua}"
RELEASE_NAME="${RELEASE_NAME:-honua}"
NAMESPACE="${NAMESPACE:-honua}"
INGRESS_HOSTNAME="${INGRESS_HOSTNAME:-honua.local}"
LOCAL_HTTP_PORT="${LOCAL_HTTP_PORT:-8080}"
ADMIN_PASSWORD="${HONUA_ADMIN_PASSWORD:-change-me}"
MASTER_KEY="${SECURITY_MASTER_KEY:-dev-master-key-32chars-minimum-1234}"
INGRESS_CLASS="${INGRESS_CLASS:-nginx}"
INGRESS_PATH="${INGRESS_PATH:-/}"
INGRESS_PATH_TYPE="${INGRESS_PATH_TYPE:-Prefix}"
POSTGRES_IMAGE_TAG="${POSTGRES_IMAGE_TAG:-latest}"
POSTGRESQL_ENABLED="${POSTGRESQL_ENABLED:-true}"
DEFAULT_CONNECTION_STRING="${DEFAULT_CONNECTION_STRING:-}"
HONUA_SKIP_MIGRATIONS="${HONUA_SKIP_MIGRATIONS:-}"

HONUA_IMAGE_REPOSITORY="${HONUA_IMAGE_REPOSITORY:-}"
HONUA_IMAGE_TAG="${HONUA_IMAGE_TAG:-}"
HONUA_IMAGE_PULL_POLICY="${HONUA_IMAGE_PULL_POLICY:-}"
POSTGRES_IMAGE_REPOSITORY="${POSTGRES_IMAGE_REPOSITORY:-}"
POSTGRES_IMAGE_DIGEST="${POSTGRES_IMAGE_DIGEST:-}"

command -v helm >/dev/null 2>&1 || { echo "helm is required"; exit 1; }
command -v kubectl >/dev/null 2>&1 || { echo "kubectl is required"; exit 1; }

kubectl create namespace "${NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -

helm dependency update "${CHART_PATH}"

HELM_ARGS=(
  --namespace "${NAMESPACE}"
  --set ingress.enabled=true
  --set ingress.className="${INGRESS_CLASS}"
  --set ingress.hosts[0].host="${INGRESS_HOSTNAME}"
  --set ingress.hosts[0].paths[0].path="${INGRESS_PATH}"
  --set ingress.hosts[0].paths[0].pathType="${INGRESS_PATH_TYPE}"
  --set secret.env.HONUA_ADMIN_PASSWORD="${ADMIN_PASSWORD}"
  --set-string secret.env.Security__ConnectionEncryption__MasterKey="${MASTER_KEY}"
  --set config.env.HONUA_ADMIN_UI="true"
  --set config.env.HostValidation__Enabled="false"
  --set-string config.env.PUBLIC_BASE_URL="http://${INGRESS_HOSTNAME}"
)

if [ -n "${HONUA_IMAGE_REPOSITORY}" ]; then
  HELM_ARGS+=(--set image.repository="${HONUA_IMAGE_REPOSITORY}")
fi
if [ -n "${HONUA_IMAGE_TAG}" ]; then
  HELM_ARGS+=(--set image.tag="${HONUA_IMAGE_TAG}")
fi
if [ -n "${HONUA_IMAGE_PULL_POLICY}" ]; then
  HELM_ARGS+=(--set image.pullPolicy="${HONUA_IMAGE_PULL_POLICY}")
fi
if [ -n "${HONUA_SKIP_MIGRATIONS}" ]; then
  HELM_ARGS+=(--set config.env.HONUA_SKIP_MIGRATIONS="${HONUA_SKIP_MIGRATIONS}")
fi

if [ "${POSTGRESQL_ENABLED}" = "true" ]; then
  HELM_ARGS+=(--set postgresql.enabled=true)
  HELM_ARGS+=(--set postgresql.image.tag="${POSTGRES_IMAGE_TAG}")

  if [ -n "${POSTGRES_IMAGE_REPOSITORY}" ]; then
    HELM_ARGS+=(--set postgresql.image.repository="${POSTGRES_IMAGE_REPOSITORY}")
  fi
  if [ -n "${POSTGRES_IMAGE_DIGEST}" ]; then
    HELM_ARGS+=(--set postgresql.image.digest="${POSTGRES_IMAGE_DIGEST}")
  fi
else
  HELM_ARGS+=(--set postgresql.enabled=false)
fi

if [ -n "${DEFAULT_CONNECTION_STRING}" ]; then
  HELM_ARGS+=(--set-string secret.env.ConnectionStrings__DefaultConnection="${DEFAULT_CONNECTION_STRING}")
fi

helm upgrade --install "${RELEASE_NAME}" "${CHART_PATH}" "${HELM_ARGS[@]}"

echo "Release '${RELEASE_NAME}' installed in namespace '${NAMESPACE}'"
echo "Test: curl -H \"Host: ${INGRESS_HOSTNAME}\" http://localhost:${LOCAL_HTTP_PORT}/healthz/ready"
