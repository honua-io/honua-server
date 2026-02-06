#!/bin/bash
set -euo pipefail

APP_NAME="${APP_NAME:-honua-server}"
NAMESPACE="${NAMESPACE:-honua}"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-600}"
REVISION="${1:-}"

if ! command -v kubectl &> /dev/null; then
    echo "kubectl not found. Please install kubectl." >&2
    exit 1
fi

if ! kubectl cluster-info &> /dev/null; then
    echo "kubectl not configured or cluster unreachable." >&2
    exit 1
fi

if [[ -n "$REVISION" ]]; then
    echo "Rolling back deployment $APP_NAME in namespace $NAMESPACE to revision $REVISION..."
    kubectl rollout undo deployment/$APP_NAME --namespace=$NAMESPACE --to-revision="$REVISION"
else
    echo "Rolling back deployment $APP_NAME in namespace $NAMESPACE to previous revision..."
    kubectl rollout undo deployment/$APP_NAME --namespace=$NAMESPACE
fi

echo "Waiting for rollback to complete..."
if ! kubectl rollout status deployment/$APP_NAME --namespace=$NAMESPACE --timeout=${ROLLOUT_TIMEOUT}s; then
    echo "Rollback failed or timed out." >&2
    exit 1
fi

echo "Rollback completed successfully."
