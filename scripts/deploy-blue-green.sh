#!/bin/bash
set -euo pipefail

# Blue-Green Deployment Script for Honua Server
# Provides zero-downtime deployments with instant rollback capability

IMAGE=${1:-""}
if [[ -z "$IMAGE" ]]; then
    echo "Error: Image tag required"
    echo "Usage: $0 <image-tag>"
    exit 1
fi

NAMESPACE="${NAMESPACE:-honua}"
APP_NAME="${APP_NAME:-honua-server}"
HEALTH_CHECK_TIMEOUT="${HEALTH_CHECK_TIMEOUT:-300}"
HEALTH_CHECK_INTERVAL="${HEALTH_CHECK_INTERVAL:-5}"

echo "🔵 Starting Blue-Green deployment for $APP_NAME"
echo "📦 Image: $IMAGE"
echo "🏷️  Namespace: $NAMESPACE"

# Function to check if kubectl is available and configured
check_kubectl() {
    if ! command -v kubectl &> /dev/null; then
        echo "❌ kubectl not found. Please install kubectl."
        exit 1
    fi

    if ! kubectl cluster-info &> /dev/null; then
        echo "❌ kubectl not configured or cluster unreachable."
        exit 1
    fi
}

# Function to wait for deployment to be ready
wait_for_deployment() {
    local deployment_name=$1
    local timeout=$2

    echo "⏳ Waiting for deployment $deployment_name to be ready..."

    if ! kubectl wait --for=condition=available deployment/"$deployment_name" \
        --namespace="$NAMESPACE" --timeout="${timeout}s"; then
        echo "❌ Deployment $deployment_name failed to become ready within ${timeout}s"
        return 1
    fi

    echo "✅ Deployment $deployment_name is ready"
    return 0
}

# Function to perform health checks
health_check() {
    local service_name=$1
    local port=$2
    local max_attempts=$((HEALTH_CHECK_TIMEOUT / HEALTH_CHECK_INTERVAL))
    local attempt=1

    echo "🏥 Starting health checks for $service_name..."

    while [[ $attempt -le $max_attempts ]]; do
        echo "🔍 Health check attempt $attempt/$max_attempts"

        # Port forward to the service for health checking
        kubectl port-forward service/"$service_name" "$port:80" \
            --namespace="$NAMESPACE" &
        local pf_pid=$!

        # Wait a moment for port-forward to establish
        sleep 2

        # Perform health checks
        if curl -f -s "http://localhost:$port/healthz/live" > /dev/null && \
           curl -f -s "http://localhost:$port/healthz/ready" > /dev/null; then
            echo "✅ Health checks passed for $service_name"
            kill $pf_pid 2>/dev/null || true
            return 0
        fi

        kill $pf_pid 2>/dev/null || true
        echo "⚠️  Health check failed, retrying in ${HEALTH_CHECK_INTERVAL}s..."
        sleep $HEALTH_CHECK_INTERVAL
        ((attempt++))
    done

    echo "❌ Health checks failed for $service_name after $max_attempts attempts"
    return 1
}

# Function to switch traffic between blue and green
switch_traffic() {
    local from_env=$1
    local to_env=$2

    echo "🔄 Switching traffic from $from_env to $to_env..."

    # Update the main service selector to point to the new environment
    kubectl patch service "$APP_NAME" \
        --namespace="$NAMESPACE" \
        --type='merge' \
        -p="{\"spec\":{\"selector\":{\"version\":\"$to_env\"}}}"

    echo "✅ Traffic switched to $to_env"
}

# Function to cleanup old deployment
cleanup_old_deployment() {
    local old_env=$1

    echo "🧹 Cleaning up old deployment ($old_env)..."

    # Scale down the old deployment
    kubectl scale deployment "$APP_NAME-$old_env" \
        --namespace="$NAMESPACE" \
        --replicas=0

    echo "✅ Old deployment scaled down"
}

# Main deployment logic
main() {
    check_kubectl

    # Determine current active environment (blue or green)
    current_env=$(kubectl get service "$APP_NAME" \
        --namespace="$NAMESPACE" \
        -o jsonpath='{.spec.selector.version}' 2>/dev/null || echo "")

    if [[ "$current_env" == "blue" ]]; then
        target_env="green"
        old_env="blue"
    else
        target_env="blue"
        old_env="green"
    fi

    echo "🎯 Current environment: ${current_env:-none}"
    echo "🎯 Target environment: $target_env"

    # Update deployment with new image
    deployment_name="$APP_NAME-$target_env"

    echo "📦 Updating deployment $deployment_name with image $IMAGE..."
    kubectl set image deployment/"$deployment_name" \
        "$APP_NAME=$IMAGE" \
        --namespace="$NAMESPACE"

    # Wait for deployment to be ready
    if ! wait_for_deployment "$deployment_name" "$HEALTH_CHECK_TIMEOUT"; then
        echo "❌ Deployment failed. Aborting blue-green switch."
        exit 1
    fi

    # Perform health checks on the new deployment
    service_name="$APP_NAME-$target_env"
    local_port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("", 0)); print(s.getsockname()[1]); s.close()')

    if ! health_check "$service_name" "$local_port"; then
        echo "❌ Health checks failed. Aborting blue-green switch."
        exit 1
    fi

    # Switch traffic to the new environment
    switch_traffic "$old_env" "$target_env"

    # Wait a bit to ensure traffic is flowing correctly
    echo "⏳ Waiting 30s for traffic to stabilize..."
    sleep 30

    # Perform final health check on the main service
    main_port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("", 0)); print(s.getsockname()[1]); s.close()')
    if ! health_check "$APP_NAME" "$main_port"; then
        echo "❌ Final health check failed. Rolling back..."
        switch_traffic "$target_env" "$old_env"
        exit 1
    fi

    # Cleanup old deployment
    if [[ -n "$old_env" ]]; then
        cleanup_old_deployment "$old_env"
    fi

    echo "🎉 Blue-Green deployment completed successfully!"
    echo "✅ Active environment: $target_env"
    echo "📊 Image: $IMAGE"
}

# Trap to cleanup port-forwards on exit
trap 'jobs -p | xargs -r kill 2>/dev/null || true' EXIT

main "$@"