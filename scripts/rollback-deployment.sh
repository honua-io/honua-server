#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_NAME="${APP_NAME:-honua-server}"
NAMESPACE="${NAMESPACE:-honua}"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-600}"
PORT_FORWARD_STARTUP_TIMEOUT="${PORT_FORWARD_STARTUP_TIMEOUT:-30}"
POST_DEPLOYMENT_VERIFICATION_SCRIPT="${POST_DEPLOYMENT_VERIFICATION_SCRIPT:-$SCRIPT_DIR/post-deployment-verification.sh}"
SKIP_POST_ROLLBACK_VERIFICATION="${SKIP_POST_ROLLBACK_VERIFICATION:-false}"
ENVIRONMENT="${ENVIRONMENT:-$NAMESPACE}"
ADMIN_API_KEY="${ADMIN_API_KEY:-}"
ADMIN_AUTH_HEADER="${ADMIN_AUTH_HEADER:-}"
REVISION="${1:-}"
PORT_FORWARD_PID=""
PORT_FORWARD_LOG=""
PORT_FORWARD_PORT=""

if ! command -v kubectl &> /dev/null; then
    echo "kubectl not found. Please install kubectl." >&2
    exit 1
fi

if ! command -v python3 &> /dev/null; then
    echo "python3 not found. Please install python3." >&2
    exit 1
fi

if ! kubectl cluster-info &> /dev/null; then
    echo "kubectl not configured or cluster unreachable." >&2
    exit 1
fi

if [[ "$SKIP_POST_ROLLBACK_VERIFICATION" != "true" && ! -f "$POST_DEPLOYMENT_VERIFICATION_SCRIPT" ]]; then
    echo "Post-deployment verification script not found: $POST_DEPLOYMENT_VERIFICATION_SCRIPT" >&2
    exit 1
fi

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

    kubectl port-forward deployment/$APP_NAME "$PORT_FORWARD_PORT:8080" \
        --namespace="$NAMESPACE" >"$PORT_FORWARD_LOG" 2>&1 &
    PORT_FORWARD_PID=$!

    local waited=0
    while [[ $waited -lt $PORT_FORWARD_STARTUP_TIMEOUT ]]; do
        if ! kill -0 "$PORT_FORWARD_PID" 2>/dev/null; then
            echo "Port-forward exited unexpectedly." >&2
            cat "$PORT_FORWARD_LOG" >&2
            stop_port_forward
            return 1
        fi

        if curl -f -s --max-time 2 "http://localhost:$PORT_FORWARD_PORT/healthz/live" >/dev/null 2>&1; then
            return 0
        fi

        sleep 1
        waited=$((waited + 1))
    done

    echo "Timed out establishing port-forward." >&2
    cat "$PORT_FORWARD_LOG" >&2
    stop_port_forward
    return 1
}

verify_deployment_health() {
    echo "Verifying rollback health..."

    if ! start_port_forward; then
        return 1
    fi

    local healthy=true

    if ! curl -f -s --max-time 10 "http://localhost:$PORT_FORWARD_PORT/healthz/live" >/dev/null; then
        echo "Liveness probe failed after rollback." >&2
        healthy=false
    fi

    if ! curl -f -s --max-time 10 "http://localhost:$PORT_FORWARD_PORT/healthz/ready" >/dev/null; then
        echo "Readiness probe failed after rollback." >&2
        healthy=false
    fi

    stop_port_forward

    if [[ "$healthy" != "true" ]]; then
        return 1
    fi

    echo "Rollback health verification passed."
    return 0
}

run_post_rollback_verification() {
    if [[ "$SKIP_POST_ROLLBACK_VERIFICATION" == "true" ]]; then
        echo "Skipping post-rollback verification because SKIP_POST_ROLLBACK_VERIFICATION=true."
        return 0
    fi

    if ! start_port_forward; then
        return 1
    fi

    local -a verification_env=(
        "BASE_URL=http://localhost:$PORT_FORWARD_PORT"
        "ENVIRONMENT=$ENVIRONMENT"
        "VERIFICATION_TIMEOUT=$ROLLOUT_TIMEOUT"
    )

    if [[ -n "$ADMIN_AUTH_HEADER" ]]; then
        verification_env+=("ADMIN_AUTH_HEADER=$ADMIN_AUTH_HEADER")
    elif [[ -n "$ADMIN_API_KEY" ]]; then
        verification_env+=("ADMIN_API_KEY=$ADMIN_API_KEY")
    fi

    echo "Running post-rollback verification..."
    if ! env "${verification_env[@]}" bash "$POST_DEPLOYMENT_VERIFICATION_SCRIPT"; then
        echo "Post-rollback verification failed." >&2
        stop_port_forward
        return 1
    fi

    stop_port_forward
    echo "Post-rollback verification passed."
    return 0
}

cleanup() {
    stop_port_forward
}

trap cleanup EXIT

main() {
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

    if ! verify_deployment_health; then
        echo "Rollback completed but health verification failed." >&2
        exit 1
    fi

    if ! run_post_rollback_verification; then
        echo "Rollback completed but post-rollback verification failed." >&2
        exit 1
    fi

    echo "Rollback completed successfully."
}

main "$@"
