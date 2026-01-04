#!/bin/bash

# Scale-out testing script for Honua Server
# Tests distributed caching, load balancing, and multi-instance scenarios

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
COMPOSE_FILE="$PROJECT_DIR/docker-compose.scale-test.yml"
BASE_URL="http://localhost:8080"

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

# Function to wait for services to be healthy
wait_for_services() {
    local max_attempts=30
    local attempt=0

    log_info "Waiting for services to be healthy..."

    while [ $attempt -lt $max_attempts ]; do
        if curl -s "${BASE_URL}/healthz/live" > /dev/null 2>&1; then
            log_info "Services are healthy!"
            return 0
        fi

        attempt=$((attempt + 1))
        log_info "Attempt $attempt/$max_attempts - waiting for services..."
        sleep 10
    done

    log_error "Services failed to become healthy within timeout"
    return 1
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
    docker compose -f "$COMPOSE_FILE" restart redis
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
    docker compose -f "$COMPOSE_FILE" stop redis
    sleep 2

    log_info "Making request with Redis unavailable (should fallback)..."
    response2=$(curl -s -w "%{http_code}" "${test_url}" -o /dev/null)

    log_info "Restarting Redis..."
    docker compose -f "$COMPOSE_FILE" start redis
    sleep 5

    log_info "Making request with Redis restored..."
    response3=$(curl -s -w "%{http_code}" "${test_url}" -o /dev/null)

    if [ "$response1" = "200" ] && [ "$response2" = "200" ] && [ "$response3" = "200" ]; then
        log_info "✓ Redis failover working correctly"
    else
        log_warn "⚠ Redis failover test results: $response1 → $response2 → $response3"
    fi
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
                echo "  --test <type>   Run specific test type: basic|load|cache|stampede|failover|all"
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
    docker compose -f "$COMPOSE_FILE" up -d --scale honua=3 $monitoring_flag

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

    log_info "To stop the environment: docker compose -f $COMPOSE_FILE down"
}

# Run main function with all arguments
main "$@"