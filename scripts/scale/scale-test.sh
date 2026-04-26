#!/bin/bash

# Scale-out testing script for Honua Server
# Tests distributed caching, load balancing, and multi-instance scenarios

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
COMPOSE_FILE="$PROJECT_DIR/docker/scale-test/compose.yml"
BASE_URL="${BASE_URL:-http://localhost:${HONUA_SCALE_TEST_HTTP_PORT:-8080}}"
ADMIN_API_KEY="${HONUA_ADMIN_PASSWORD:-scale-test-admin-password}"
POST_DEPLOYMENT_VERIFICATION_SCRIPT="${POST_DEPLOYMENT_VERIFICATION_SCRIPT:-$PROJECT_DIR/scripts/cloud/post-deployment-verification.sh}"
CANARY_ROUTE_HEADER="${HONUA_SCALE_TEST_CANARY_ROUTE_HEADER:-X-Honua-Canary: always}"
CANARY_WEIGHT="${HONUA_SCALE_TEST_CANARY_WEIGHT:-10}"
CANARY_SAMPLE_REQUESTS="${HONUA_SCALE_TEST_CANARY_SAMPLE_REQUESTS:-40}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Logging functions
log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

compose_cmd() {
    docker compose -f "$COMPOSE_FILE" "$@"
}

compose_canary_cmd() {
    docker compose --profile canary -f "$COMPOSE_FILE" "$@"
}

# Function to wait for services to be healthy
wait_for_services() {
    log_info "Waiting for services to be healthy..."
    wait_for_http_status "${BASE_URL}/healthz/live" "200" 30 10 "services"
}

# Function to test basic connectivity
test_basic_connectivity() {
    log_info "Testing basic connectivity..."

    response=$(curl -s -w "%{http_code}" "${BASE_URL}/healthz/live" -o /dev/null)
    if [ "$response" = "200" ]; then
        log_info "✓ Health check endpoint accessible"
    else
        log_error "✗ Health check failed with status: $response"
        return 1
    fi
}

wait_for_http_status() {
    local url=$1
    local expected_status=$2
    local max_attempts=${3:-30}
    local sleep_seconds=${4:-10}
    local description=${5:-endpoint}

    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        local status
        status=$(curl -s -o /dev/null -w "%{http_code}" "$url" || echo "000")
        if [ "$status" = "$expected_status" ]; then
            log_info "Expected status $expected_status observed for $description"
            return 0
        fi

        attempt=$((attempt + 1))
        log_info "Attempt $attempt/$max_attempts waiting for $description (got $status, want $expected_status)"
        sleep "$sleep_seconds"
    done

    log_error "Timed out waiting for $description to return $expected_status"
    return 1
}

wait_for_any_http_status() {
    local url=$1
    local expected_statuses=$2
    local max_attempts=${3:-30}
    local sleep_seconds=${4:-10}
    local description=${5:-endpoint}

    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        local status
        status=$(curl -s -o /dev/null -w "%{http_code}" "$url" || echo "000")

        for expected_status in $expected_statuses; do
            if [ "$status" = "$expected_status" ]; then
                log_info "Observed failure status $status for $description"
                return 0
            fi
        done

        attempt=$((attempt + 1))
        log_info "Attempt $attempt/$max_attempts waiting for $description to return one of [$expected_statuses] (got $status)"
        sleep "$sleep_seconds"
    done

    log_error "Timed out waiting for $description to return one of [$expected_statuses]"
    return 1
}

wait_for_http_status_with_header() {
    local url=$1
    local header=$2
    local expected_status=$3
    local max_attempts=${4:-30}
    local sleep_seconds=${5:-10}
    local description=${6:-endpoint}

    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        local status
        status=$(curl -s -o /dev/null -w "%{http_code}" -H "$header" "$url" || echo "000")
        if [ "$status" = "$expected_status" ]; then
            log_info "Expected status $expected_status observed for $description using routed header"
            return 0
        fi

        attempt=$((attempt + 1))
        log_info "Attempt $attempt/$max_attempts waiting for $description with routed header (got $status, want $expected_status)"
        sleep "$sleep_seconds"
    done

    log_error "Timed out waiting for $description with routed header to return $expected_status"
    return 1
}

wait_for_any_http_status_with_header() {
    local url=$1
    local header=$2
    local expected_statuses=$3
    local max_attempts=${4:-30}
    local sleep_seconds=${5:-10}
    local description=${6:-endpoint}

    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        local status
        status=$(curl -s -o /dev/null -w "%{http_code}" -H "$header" "$url" || echo "000")

        for expected_status in $expected_statuses; do
            if [ "$status" = "$expected_status" ]; then
                log_info "Observed status $status for $description using routed header"
                return 0
            fi
        done

        attempt=$((attempt + 1))
        log_info "Attempt $attempt/$max_attempts waiting for $description with routed header to return one of [$expected_statuses] (got $status)"
        sleep "$sleep_seconds"
    done

    log_error "Timed out waiting for $description with routed header to return one of [$expected_statuses]"
    return 1
}

get_preflight_flag() {
    local response
    response=$(curl -s -H "X-API-Key: ${ADMIN_API_KEY}" "${BASE_URL}/api/v1/admin/deploy/preflight" || true)

    if [ -z "$response" ]; then
        echo "unavailable"
        return 0
    fi

    echo "$response" | grep -o '"readyForCoordinatedDeploy":[^,}]*' | cut -d':' -f2 | tr -d '[:space:]'
}

wait_for_preflight_state() {
    local expected_state=$1
    local max_attempts=${2:-30}
    local sleep_seconds=${3:-10}
    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        local state
        state=$(get_preflight_flag)
        if [ "$state" = "$expected_state" ]; then
            log_info "Deploy preflight reports readyForCoordinatedDeploy=$expected_state"
            return 0
        fi

        attempt=$((attempt + 1))
        log_info "Attempt $attempt/$max_attempts waiting for deploy preflight state $expected_state (got ${state:-unknown})"
        sleep "$sleep_seconds"
    done

    log_error "Timed out waiting for deploy preflight state $expected_state"
    return 1
}

# Function to test load balancing
test_load_balancing() {
    log_info "Testing load balancing across instances..."

    local instances=()
    for i in {1..20}; do
        instance=$(curl -s "${BASE_URL}/healthz/live" -H "X-Test-Request: $i" | grep -o '"instance":"[^"]*"' | cut -d'"' -f4 || echo "unknown")
        instances+=("$instance")
        sleep 0.1
    done

    # Count unique instances
    unique_instances=$(printf '%s\n' "${instances[@]}" | sort -u | wc -l)
    log_info "Detected $unique_instances unique instances serving requests"

    if [ "$unique_instances" -gt 1 ]; then
        log_info "✓ Load balancing is working (multiple instances serving)"
    else
        log_warn "⚠ Only 1 instance detected - load balancing may not be working properly"
    fi
}

# Function to test distributed caching
test_distributed_caching() {
    log_info "Testing distributed caching behavior..."

    # Test cache warming across instances
    local test_url="${BASE_URL}/rest/services/1/FeatureServer"

    log_info "Making initial request to warm cache..."
    time1=$(curl -s -w "%{time_total}" "${test_url}" -o /dev/null)

    log_info "Making second request (should be faster due to caching)..."
    time2=$(curl -s -w "%{time_total}" "${test_url}" -o /dev/null)

    log_info "First request: ${time1}s, Second request: ${time2}s"

    # Test cache consistency across instances
    log_info "Testing cache consistency across instances..."
    for i in {1..10}; do
        etag=$(curl -s -I "${test_url}" | grep -i etag | cut -d' ' -f2 | tr -d '\r\n' || echo "none")
        if [ "$etag" != "none" ] && [ -n "$etag" ]; then
            log_info "Request $i: ETag = $etag"
        else
            log_warn "Request $i: No ETag received"
        fi
        sleep 0.2
    done
}

# Function to test cache stampede protection
test_cache_stampede_protection() {
    log_info "Testing cache stampede protection..."

    local test_url="${BASE_URL}/rest/services/1/FeatureServer"

    log_info "Clearing cache by restarting Redis..."
    compose_cmd restart redis
    sleep 5

    log_info "Sending concurrent requests to trigger potential stampede..."

    # Send 20 concurrent requests
    for i in {1..20}; do
        curl -s "${test_url}" -H "X-Test-Request: concurrent-$i" > /dev/null &
    done

    wait
    log_info "✓ Concurrent requests completed (stampede protection should prevent duplicate work)"
}

# Function to test Redis failover
test_redis_failover() {
    log_info "Testing Redis failover behavior..."

    local test_url="${BASE_URL}/rest/services/1/FeatureServer"

    log_info "Making request with Redis available..."
    response1=$(curl -s -w "%{http_code}" "${test_url}" -o /dev/null)

    log_info "Stopping Redis..."
    compose_cmd stop redis
    sleep 2

    log_info "Making request with Redis unavailable (should fallback)..."
    response2=$(curl -s -w "%{http_code}" "${test_url}" -o /dev/null)

    log_info "Restarting Redis..."
    compose_cmd start redis
    sleep 5

    log_info "Making request with Redis restored..."
    response3=$(curl -s -w "%{http_code}" "${test_url}" -o /dev/null)

    if [ "$response1" = "200" ] && [ "$response2" = "200" ] && [ "$response3" = "200" ]; then
        log_info "✓ Redis failover working correctly"
    else
        log_warn "⚠ Redis failover test results: $response1 → $response2 → $response3"
    fi
}

test_deploy_rollback() {
    log_info "Testing rollback behavior against the scale-test environment..."

    local override_file
    override_file=$(mktemp)

    cat > "$override_file" <<EOF
services:
  honua:
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua_scale_test_broken;Username=honua_user;Password=honua_password"
EOF

    cleanup_override() {
        rm -f "$override_file"
    }

    trap cleanup_override RETURN

    log_info "Verifying baseline readiness before rollout..."
    wait_for_http_status "${BASE_URL}/healthz/ready" "200" 30 10 "baseline readiness"
    wait_for_preflight_state "true" 18 10

    log_info "Applying an intentionally broken rollout configuration..."
    compose_cmd -f "$override_file" up -d --force-recreate --scale honua=3 honua nginx

    wait_for_any_http_status "${BASE_URL}/healthz/ready" "502 503" 24 10 "failed rollout readiness gate"

    log_info "Rolling back to the baseline configuration..."
    compose_cmd up -d --force-recreate --scale honua=3 honua nginx

    wait_for_http_status "${BASE_URL}/healthz/ready" "200" 30 10 "post-rollback readiness"
    wait_for_preflight_state "true" 18 10

    log_info "Rollback rehearsal succeeded"
}

configure_canary_routing() {
    local enabled=$1
    local weight=$2

    log_info "Configuring Nginx canary routing (enabled=$enabled, weight=${weight}%)..."
    env \
        HONUA_SCALE_TEST_CANARY_ENABLED="$enabled" \
        HONUA_SCALE_TEST_CANARY_WEIGHT="$weight" \
        docker compose -f "$COMPOSE_FILE" up -d --force-recreate --no-deps nginx

    wait_for_http_status "${BASE_URL}/healthz/live" "200" 18 5 "nginx after canary reconfiguration"
}

start_canary_service() {
    local override_file=${1:-}

    log_info "Starting canary service..."
    if [ -n "$override_file" ]; then
        compose_canary_cmd -f "$override_file" up -d --force-recreate --scale honua_canary=1 honua_canary
    else
        compose_canary_cmd up -d --force-recreate --scale honua_canary=1 honua_canary
    fi
}

stop_canary_service() {
    log_info "Stopping canary service..."
    compose_canary_cmd stop honua_canary >/dev/null 2>&1 || true
    compose_canary_cmd rm -f -s honua_canary >/dev/null 2>&1 || true
}

sample_canary_lane_hits() {
    local url=$1
    local request_count=$2
    local canary_hits=0

    for i in $(seq 1 "$request_count"); do
        local lane
        lane=$(curl -s -D - -o /dev/null -H "X-Scale-Test-Sample: $i" "$url" \
            | tr -d '\r' \
            | awk -F': ' 'tolower($1)=="x-honua-deployment-lane"{print $2}' \
            | tail -n1)

        if [ "$lane" = "canary" ]; then
            canary_hits=$((canary_hits + 1))
        fi
    done

    echo "$canary_hits"
}

run_canary_verification() {
    if [ ! -f "$POST_DEPLOYMENT_VERIFICATION_SCRIPT" ]; then
        log_error "Post-deployment verification script not found: $POST_DEPLOYMENT_VERIFICATION_SCRIPT"
        return 1
    fi

    log_info "Running routed verification against the canary lane..."
    env \
        BASE_URL="$BASE_URL" \
        ENVIRONMENT="scale-test-canary" \
        ADMIN_API_KEY="$ADMIN_API_KEY" \
        VERIFICATION_TIMEOUT=120 \
        EXTRA_CURL_HEADER="$CANARY_ROUTE_HEADER" \
        bash "$POST_DEPLOYMENT_VERIFICATION_SCRIPT"
}

rollback_canary_route() {
    configure_canary_routing false 0
    stop_canary_service
    wait_for_http_status "${BASE_URL}/healthz/ready" "200" 24 5 "stable readiness after canary rollback"
    wait_for_preflight_state "true" 18 5
}

test_canary_rollout() {
    log_info "Testing weighted Nginx canary rollout and automatic rollback..."

    local broken_override
    broken_override=$(mktemp)
    local cleanup_done=0

    cat > "$broken_override" <<EOF
services:
  honua_canary:
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua_scale_test_broken;Username=honua_user;Password=honua_password"
EOF

    cleanup_canary() {
        if [ "${cleanup_done:-1}" -eq 1 ]; then
            trap - RETURN
            return 0
        fi

        cleanup_done=1
        trap - RETURN
        configure_canary_routing false 0 || true
        stop_canary_service || true
        rm -f "${broken_override:-}"
    }

    trap cleanup_canary RETURN

    wait_for_http_status "${BASE_URL}/healthz/ready" "200" 30 10 "baseline readiness"
    wait_for_preflight_state "true" 18 10
    configure_canary_routing false 0

    log_info "Starting healthy canary rehearsal..."
    start_canary_service
    wait_for_http_status_with_header "${BASE_URL}/healthz/ready" "$CANARY_ROUTE_HEADER" "200" 24 5 "healthy canary readiness gate"
    configure_canary_routing true "$CANARY_WEIGHT"
    run_canary_verification

    local healthy_canary_hits
    healthy_canary_hits=$(sample_canary_lane_hits "${BASE_URL}/openapi.json" "$CANARY_SAMPLE_REQUESTS")
    if [ "$healthy_canary_hits" -le 0 ]; then
        log_error "Weighted routing never selected the canary lane during the healthy rehearsal"
        return 1
    fi
    log_info "Observed $healthy_canary_hits canary responses out of $CANARY_SAMPLE_REQUESTS weighted requests"

    log_info "Rolling back the healthy rehearsal to restore a stable-only baseline..."
    rollback_canary_route

    log_info "Starting negative canary rehearsal with a broken canary configuration..."
    start_canary_service "$broken_override"
    configure_canary_routing true "$CANARY_WEIGHT"

    if ! wait_for_any_http_status_with_header "${BASE_URL}/healthz/ready" "$CANARY_ROUTE_HEADER" "502 503" 24 5 "broken canary readiness gate"; then
        log_error "Broken canary did not surface through the routed readiness gate"
        return 1
    fi

    log_info "Health degradation detected in canary lane; triggering automatic rollback..."
    rollback_canary_route

    local post_rollback_canary_hits
    post_rollback_canary_hits=$(sample_canary_lane_hits "${BASE_URL}/openapi.json" 20)
    if [ "$post_rollback_canary_hits" -ne 0 ]; then
        log_error "Canary lane still received traffic after rollback"
        return 1
    fi

    wait_for_http_status_with_header "${BASE_URL}/healthz/ready" "$CANARY_ROUTE_HEADER" "200" 24 5 "routed header after canary rollback"
    log_info "Canary rehearsal succeeded"
}

# Function to show monitoring URLs
show_monitoring_urls() {
    log_info "Monitoring URLs (if --monitoring flag was used):"
    echo "  Redis Insight:  http://localhost:8001"
    echo "  Prometheus:     http://localhost:9090"
    echo "  Grafana:        http://localhost:3000 (admin/admin)"
    echo ""
    echo "Load Balancer:    http://localhost:8080"
    echo "Direct DB:        postgresql://honua_user:honua_password@localhost:5434/honua_scale_test"
    echo "Direct Redis:     redis://localhost:6379"
}

print_stop_command() {
    if [ -n "${COMPOSE_PROJECT_NAME:-}" ]; then
        log_info "To stop the environment: docker compose -p ${COMPOSE_PROJECT_NAME} -f $COMPOSE_FILE --profile canary down"
        return
    fi

    log_info "To stop the environment: docker compose -f $COMPOSE_FILE --profile canary down"
}

# Main function
main() {
    local monitoring_flag=""
    local test_type="all"

    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case $1 in
            --monitoring)
                monitoring_flag="--profile monitoring"
                shift
                ;;
            --test)
                test_type="$2"
                shift 2
                ;;
            --help)
                echo "Usage: $0 [--monitoring] [--test <type>]"
                echo ""
                echo "Options:"
                echo "  --monitoring    Start monitoring stack (Redis Insight, Prometheus, Grafana)"
                echo "  --test <type>   Run specific test type: basic|load|cache|stampede|failover|rollback|canary|all"
                echo "  --help          Show this help message"
                echo ""
                echo "Examples:"
                echo "  $0                           # Start 3 instances and run all tests"
                echo "  $0 --monitoring              # Start with monitoring stack"
                echo "  $0 --test cache              # Run only cache tests"
                exit 0
                ;;
            *)
                log_error "Unknown option: $1"
                exit 1
                ;;
        esac
    done

    log_info "Starting scale-out testing environment..."

    # Start services
    log_info "Starting services with 3 Honua instances..."
    compose_cmd up -d --scale honua=3 $monitoring_flag

    # Wait for services
    if ! wait_for_services; then
        log_error "Failed to start services"
        exit 1
    fi

    # Run tests based on type
    case "$test_type" in
        basic)
            test_basic_connectivity
            ;;
        load)
            test_load_balancing
            ;;
        cache)
            test_distributed_caching
            ;;
        stampede)
            test_cache_stampede_protection
            ;;
        failover)
            test_redis_failover
            ;;
        rollback)
            test_deploy_rollback
            ;;
        canary)
            test_canary_rollout
            ;;
        all)
            test_basic_connectivity
            test_load_balancing
            test_distributed_caching
            test_cache_stampede_protection
            test_redis_failover
            ;;
        *)
            log_error "Unknown test type: $test_type"
            exit 1
            ;;
    esac

    log_info "Scale-out testing completed!"

    if [ -n "$monitoring_flag" ]; then
        show_monitoring_urls
    fi

    print_stop_command
}

# Run main function with all arguments
main "$@"
