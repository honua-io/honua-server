#!/bin/bash
set -euo pipefail

# Rolling Deployment Script for Honua Server
# Standard rolling update deployment with health checks

IMAGE=${1:-""}
if [[ -z "$IMAGE" ]]; then
    echo "Error: Image tag required"
    echo "Usage: $0 <image-tag>"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NAMESPACE="${NAMESPACE:-honua}"
APP_NAME="${APP_NAME:-honua-server}"
HEALTH_CHECK_TIMEOUT="${HEALTH_CHECK_TIMEOUT:-300}"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-600}"
PORT_FORWARD_STARTUP_TIMEOUT="${PORT_FORWARD_STARTUP_TIMEOUT:-30}"
POST_DEPLOYMENT_VERIFICATION_SCRIPT="${POST_DEPLOYMENT_VERIFICATION_SCRIPT:-$SCRIPT_DIR/post-deployment-verification.sh}"
SKIP_POST_DEPLOYMENT_VERIFICATION="${SKIP_POST_DEPLOYMENT_VERIFICATION:-false}"
ENVIRONMENT="${ENVIRONMENT:-$NAMESPACE}"
ADMIN_API_KEY="${ADMIN_API_KEY:-}"
ADMIN_AUTH_HEADER="${ADMIN_AUTH_HEADER:-}"
PORT_FORWARD_PID=""
PORT_FORWARD_LOG=""
PORT_FORWARD_PORT=""

echo "🔄 Starting Rolling deployment for $APP_NAME"
echo "📦 Image: $IMAGE"
echo "🏷️  Namespace: $NAMESPACE"

check_dependencies() {
    if ! command -v kubectl &> /dev/null; then
        echo "❌ kubectl not found. Please install kubectl."
        exit 1
    fi

    if ! command -v python3 &> /dev/null; then
        echo "❌ python3 not found. Please install python3."
        exit 1
    fi

    if ! kubectl cluster-info &> /dev/null; then
        echo "❌ kubectl not configured or cluster unreachable."
        exit 1
    fi

    if [[ "$SKIP_POST_DEPLOYMENT_VERIFICATION" != "true" && ! -f "$POST_DEPLOYMENT_VERIFICATION_SCRIPT" ]]; then
        echo "❌ Post-deployment verification script not found: $POST_DEPLOYMENT_VERIFICATION_SCRIPT"
        exit 1
    fi
}

get_free_port() {
    python3 -c 'import socket; s=socket.socket(); s.bind(("", 0)); print(s.getsockname()[1]); s.close()'
}

stop_port_forward() {
    if [[ -n "$PORT_FORWARD_PID" ]]; then
        kill "$PORT_FORWARD_PID" 2>/dev/null || true
        wait "$PORT_FORWARD_PID" 2>/dev/null || true
        PORT_FORWARD_PID=""
    fi

    if [[ -n "$PORT_FORWARD_LOG" && -f "$PORT_FORWARD_LOG" ]]; then
        rm -f "$PORT_FORWARD_LOG"
        PORT_FORWARD_LOG=""
    fi

    PORT_FORWARD_PORT=""
}

start_port_forward() {
    stop_port_forward

    PORT_FORWARD_PORT=$(get_free_port)
    PORT_FORWARD_LOG=$(mktemp)

    echo "🔌 Establishing port-forward on localhost:$PORT_FORWARD_PORT..."
    kubectl port-forward deployment/$APP_NAME "$PORT_FORWARD_PORT:8080" \
        --namespace="$NAMESPACE" >"$PORT_FORWARD_LOG" 2>&1 &
    PORT_FORWARD_PID=$!

    local waited=0
    while [[ $waited -lt $PORT_FORWARD_STARTUP_TIMEOUT ]]; do
        if ! kill -0 "$PORT_FORWARD_PID" 2>/dev/null; then
            echo "❌ Port-forward exited unexpectedly"
            cat "$PORT_FORWARD_LOG"
            stop_port_forward
            return 1
        fi

        if curl -f -s --max-time 2 "http://localhost:$PORT_FORWARD_PORT/healthz/live" >/dev/null 2>&1; then
            return 0
        fi

        sleep 1
        waited=$((waited + 1))
    done

    echo "❌ Timed out establishing port-forward"
    cat "$PORT_FORWARD_LOG"
    stop_port_forward
    return 1
}

update_deployment() {
    echo "🚀 Updating deployment $APP_NAME with image $IMAGE..."

    kubectl set image deployment/$APP_NAME \
        $APP_NAME=$IMAGE \
        --namespace=$NAMESPACE

    echo "✅ Deployment image updated"
}

# Function to wait for rollout to complete
wait_for_rollout() {
    echo "⏳ Waiting for rollout to complete..."

    if ! kubectl rollout status deployment/$APP_NAME \
        --namespace=$NAMESPACE \
        --timeout=${ROLLOUT_TIMEOUT}s; then
        echo "❌ Rollout failed or timed out"
        return 1
    fi

    echo "✅ Rollout completed successfully"
    return 0
}

verify_deployment_health() {
    echo "🏥 Verifying deployment health..."

    # Get the number of ready replicas
    local ready_replicas=$(kubectl get deployment $APP_NAME \
        --namespace=$NAMESPACE \
        -o jsonpath='{.status.readyReplicas}')

    local desired_replicas=$(kubectl get deployment $APP_NAME \
        --namespace=$NAMESPACE \
        -o jsonpath='{.spec.replicas}')

    if [[ "$ready_replicas" != "$desired_replicas" ]]; then
        echo "❌ Not all replicas are ready: $ready_replicas/$desired_replicas"
        return 1
    fi

    if ! start_port_forward; then
        return 1
    fi

    local health_ok=true

    if ! curl -f -s --max-time 10 "http://localhost:$PORT_FORWARD_PORT/healthz/live" > /dev/null; then
        echo "❌ Liveness probe failed"
        health_ok=false
    else
        echo "✅ Liveness probe passed"
    fi

    if ! curl -f -s --max-time 10 "http://localhost:$PORT_FORWARD_PORT/healthz/ready" > /dev/null; then
        echo "❌ Readiness probe failed"
        health_ok=false
    else
        echo "✅ Readiness probe passed"
    fi

    stop_port_forward

    if [[ "$health_ok" == "false" ]]; then
        return 1
    fi

    echo "✅ Deployment health verification passed"
    return 0
}

run_post_deployment_verification() {
    local phase=${1:-post-deployment}

    if [[ "$SKIP_POST_DEPLOYMENT_VERIFICATION" == "true" ]]; then
        echo "ℹ️  Skipping $phase verification because SKIP_POST_DEPLOYMENT_VERIFICATION=true"
        return 0
    fi

    if ! start_port_forward; then
        return 1
    fi

    local -a verification_env=(
        "BASE_URL=http://localhost:$PORT_FORWARD_PORT"
        "ENVIRONMENT=$ENVIRONMENT"
        "VERIFICATION_TIMEOUT=$HEALTH_CHECK_TIMEOUT"
    )

    if [[ -n "$ADMIN_AUTH_HEADER" ]]; then
        verification_env+=("ADMIN_AUTH_HEADER=$ADMIN_AUTH_HEADER")
    elif [[ -n "$ADMIN_API_KEY" ]]; then
        verification_env+=("ADMIN_API_KEY=$ADMIN_API_KEY")
    fi

    echo "🔍 Running $phase verification..."
    if ! env "${verification_env[@]}" bash "$POST_DEPLOYMENT_VERIFICATION_SCRIPT"; then
        echo "❌ $phase verification failed"
        stop_port_forward
        return 1
    fi

    stop_port_forward
    echo "✅ $phase verification passed"
    return 0
}

get_deployment_history() {
    echo "📋 Deployment history:"
    kubectl rollout history deployment/$APP_NAME --namespace=$NAMESPACE
}

rollback_deployment() {
    echo "🔄 Rolling back deployment..."

    kubectl rollout undo deployment/$APP_NAME --namespace=$NAMESPACE

    echo "⏳ Waiting for rollback to complete..."
    if ! kubectl rollout status deployment/$APP_NAME \
        --namespace=$NAMESPACE \
        --timeout=${ROLLOUT_TIMEOUT}s; then
        echo "❌ Rollback failed"
        return 1
    fi

    if ! verify_deployment_health; then
        echo "❌ Rollback health verification failed"
        return 1
    fi

    if ! run_post_deployment_verification "post-rollback"; then
        echo "❌ Post-rollback verification failed"
        return 1
    fi

    echo "✅ Rollback completed"
    return 0
}

monitor_rollout() {
    echo "📊 Monitoring rollout progress..."

    local start_time=$(date +%s)
    local check_interval=10

    while kubectl rollout status deployment/$APP_NAME --namespace=$NAMESPACE --timeout=0s 2>&1 | grep -q "Waiting"; do
        local current_time=$(date +%s)
        local elapsed=$((current_time - start_time))

        if [[ $elapsed -ge $ROLLOUT_TIMEOUT ]]; then
            echo "❌ Rollout monitoring timeout after ${elapsed}s"
            return 1
        fi

        # Get current status
        local ready_replicas=$(kubectl get deployment $APP_NAME \
            --namespace=$NAMESPACE \
            -o jsonpath='{.status.readyReplicas}' 2>/dev/null || echo "0")

        local updated_replicas=$(kubectl get deployment $APP_NAME \
            --namespace=$NAMESPACE \
            -o jsonpath='{.status.updatedReplicas}' 2>/dev/null || echo "0")

        local desired_replicas=$(kubectl get deployment $APP_NAME \
            --namespace=$NAMESPACE \
            -o jsonpath='{.spec.replicas}')

        echo "⏳ Progress: $updated_replicas/$desired_replicas updated, $ready_replicas/$desired_replicas ready (${elapsed}s elapsed)"

        sleep $check_interval
    done

    echo "✅ Rollout monitoring completed"
    return 0
}

handle_failed_rollout() {
    local reason=$1

    echo "❌ $reason, attempting rollback..."
    if rollback_deployment; then
        echo "✅ Successfully rolled back to previous version"
    else
        echo "❌ Rollback failed - manual intervention required"
    fi

    exit 1
}

cleanup() {
    stop_port_forward
}

main() {
    check_dependencies

    echo "📋 Current deployment state:"
    kubectl get deployment $APP_NAME --namespace=$NAMESPACE || {
        echo "❌ Deployment $APP_NAME not found in namespace $NAMESPACE"
        exit 1
    }

    # Show deployment history
    get_deployment_history

    # Update deployment with new image
    update_deployment

    # Monitor rollout progress
    if ! monitor_rollout; then
        handle_failed_rollout "Rollout monitoring failed"
    fi

    # Wait for rollout to complete
    if ! wait_for_rollout; then
        handle_failed_rollout "Rollout failed"
    fi

    # Verify deployment health
    if ! verify_deployment_health; then
        handle_failed_rollout "Health verification failed"
    fi

    if ! run_post_deployment_verification; then
        handle_failed_rollout "Post-deployment verification failed"
    fi

    echo "🎉 Rolling deployment completed successfully!"
    echo "📦 Image: $IMAGE"
    echo "🔗 Namespace: $NAMESPACE"

    # Show final state
    echo "📋 Final deployment state:"
    kubectl get deployment $APP_NAME --namespace=$NAMESPACE
    kubectl get pods -l app=$APP_NAME --namespace=$NAMESPACE
}

trap cleanup EXIT

main "$@"
