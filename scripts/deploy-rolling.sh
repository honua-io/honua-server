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

NAMESPACE="${NAMESPACE:-honua}"
APP_NAME="${APP_NAME:-honua-server}"
HEALTH_CHECK_TIMEOUT="${HEALTH_CHECK_TIMEOUT:-300}"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-600}"

echo "🔄 Starting Rolling deployment for $APP_NAME"
echo "📦 Image: $IMAGE"
echo "🏷️  Namespace: $NAMESPACE"

# Function to check if kubectl is available
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

# Function to update deployment image
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

# Function to verify deployment health
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

    # Port forward to test health endpoints
    local local_port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("", 0)); print(s.getsockname()[1]); s.close()')

    echo "🔍 Testing health endpoints via port-forward..."
    kubectl port-forward deployment/$APP_NAME $local_port:8080 \
        --namespace=$NAMESPACE &
    local pf_pid=$!

    # Wait for port-forward to establish
    sleep 5

    local health_ok=true

    # Test liveness probe
    if ! curl -f -s --max-time 10 "http://localhost:$local_port/healthz/live" > /dev/null; then
        echo "❌ Liveness probe failed"
        health_ok=false
    else
        echo "✅ Liveness probe passed"
    fi

    # Test readiness probe
    if ! curl -f -s --max-time 10 "http://localhost:$local_port/healthz/ready" > /dev/null; then
        echo "❌ Readiness probe failed"
        health_ok=false
    else
        echo "✅ Readiness probe passed"
    fi

    # Cleanup port-forward
    kill $pf_pid 2>/dev/null || true

    if [[ "$health_ok" == "false" ]]; then
        return 1
    fi

    echo "✅ Deployment health verification passed"
    return 0
}

# Function to get deployment history
get_deployment_history() {
    echo "📋 Deployment history:"
    kubectl rollout history deployment/$APP_NAME --namespace=$NAMESPACE
}

# Function to rollback deployment
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

    echo "✅ Rollback completed"
    return 0
}

# Function to monitor deployment during rollout
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

# Main deployment logic
main() {
    check_kubectl

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
        echo "❌ Rollout monitoring failed, attempting rollback..."
        if rollback_deployment; then
            echo "✅ Successfully rolled back to previous version"
        else
            echo "❌ Rollback failed - manual intervention required"
            exit 1
        fi
        exit 1
    fi

    # Wait for rollout to complete
    if ! wait_for_rollout; then
        echo "❌ Rollout failed, attempting rollback..."
        if rollback_deployment; then
            echo "✅ Successfully rolled back to previous version"
        else
            echo "❌ Rollback failed - manual intervention required"
            exit 1
        fi
        exit 1
    fi

    # Verify deployment health
    if ! verify_deployment_health; then
        echo "❌ Health verification failed, attempting rollback..."
        if rollback_deployment; then
            echo "✅ Successfully rolled back to previous version"
        else
            echo "❌ Rollback failed - manual intervention required"
            exit 1
        fi
        exit 1
    fi

    echo "🎉 Rolling deployment completed successfully!"
    echo "📦 Image: $IMAGE"
    echo "🔗 Namespace: $NAMESPACE"

    # Show final state
    echo "📋 Final deployment state:"
    kubectl get deployment $APP_NAME --namespace=$NAMESPACE
    kubectl get pods -l app=$APP_NAME --namespace=$NAMESPACE
}

# Trap to cleanup port-forwards on exit
trap 'jobs -p | xargs -r kill 2>/dev/null || true' EXIT

main "$@"