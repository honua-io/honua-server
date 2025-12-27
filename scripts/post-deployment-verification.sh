#!/bin/bash
set -euo pipefail

# Post-Deployment Verification Script for Honua Server
# Comprehensive validation after deployments

BASE_URL="${BASE_URL:-https://api.honua.example.com}"
TIMEOUT="${TIMEOUT:-30}"
VERIFICATION_TIMEOUT="${VERIFICATION_TIMEOUT:-300}"
ENVIRONMENT="${ENVIRONMENT:-production}"

echo "🔍 Starting post-deployment verification for Honua Server"
echo "🔗 Base URL: $BASE_URL"
echo "🏷️  Environment: $ENVIRONMENT"

# Function to make HTTP requests with error handling
http_check() {
    local url=$1
    local expected_status=${2:-200}
    local description=$3

    echo "🔍 Checking $description..."

    local status_code=$(curl -s -o /dev/null -w "%{http_code}" \
        --max-time $TIMEOUT \
        --connect-timeout 10 \
        --retry 3 \
        --retry-delay 2 \
        "$url" || echo "000")

    if [[ "$status_code" == "$expected_status" ]]; then
        echo "✅ $description: HTTP $status_code"
        return 0
    else
        echo "❌ $description: HTTP $status_code (expected $expected_status)"
        return 1
    fi
}

# Function to check JSON response
json_check() {
    local url=$1
    local json_path=$2
    local expected_value=$3
    local description=$4

    echo "🔍 Checking $description..."

    local response=$(curl -s --max-time $TIMEOUT "$url")
    local actual_value=$(echo "$response" | jq -r "$json_path" 2>/dev/null || echo "null")

    if [[ "$actual_value" == "$expected_value" ]]; then
        echo "✅ $description: $actual_value"
        return 0
    else
        echo "❌ $description: got '$actual_value', expected '$expected_value'"
        echo "Response: $response"
        return 1
    fi
}

# Function to check response time
response_time_check() {
    local url=$1
    local max_time_ms=$2
    local description=$3

    echo "⏱️  Checking $description response time (max: ${max_time_ms}ms)..."

    local response_time_ms=$(curl -s -o /dev/null -w "%{time_total}" \
        --max-time $TIMEOUT \
        "$url" | awk '{print int($1*1000)}')

    if [[ $response_time_ms -le $max_time_ms ]]; then
        echo "✅ $description: ${response_time_ms}ms"
        return 0
    else
        echo "❌ $description: ${response_time_ms}ms (exceeds ${max_time_ms}ms)"
        return 1
    fi
}

# Health check verification
health_checks() {
    echo "🔵 Health Check Verification"
    echo "============================"

    local failed_checks=0

    # Liveness probe
    if ! http_check "$BASE_URL/healthz/live" 200 "Liveness probe"; then
        ((failed_checks++))
    fi

    # Readiness probe
    if ! http_check "$BASE_URL/healthz/ready" 200 "Readiness probe"; then
        ((failed_checks++))
    fi

    # Response time checks
    if ! response_time_check "$BASE_URL/healthz/live" 1000 "Liveness probe"; then
        ((failed_checks++))
    fi

    if ! response_time_check "$BASE_URL/healthz/ready" 2000 "Readiness probe"; then
        ((failed_checks++))
    fi

    return $failed_checks
}

# API contract verification
api_contract_checks() {
    echo "🔵 API Contract Verification"
    echo "============================"

    local failed_checks=0

    # OpenAPI specification
    if ! http_check "$BASE_URL/swagger.json" 200 "OpenAPI specification"; then
        ((failed_checks++))
    fi

    # Check if OpenAPI contains expected info
    if command -v jq >/dev/null 2>&1; then
        if ! json_check "$BASE_URL/swagger.json" ".info.title" "Honua Server API" "API title"; then
            ((failed_checks++))
        fi

        if ! json_check "$BASE_URL/swagger.json" ".openapi" "3.0.1" "OpenAPI version"; then
            ((failed_checks++))
        fi
    fi

    # Feature services endpoint (if available)
    if http_check "$BASE_URL/rest/services" 200 "Feature services list" &>/dev/null; then
        echo "✅ Feature services endpoint available"

        if ! response_time_check "$BASE_URL/rest/services" 5000 "Feature services"; then
            ((failed_checks++))
        fi
    else
        echo "ℹ️  Feature services endpoint not available (may be expected)"
    fi

    return $failed_checks
}

# Security verification
security_checks() {
    echo "🔵 Security Verification"
    echo "========================"

    local failed_checks=0

    # Check security headers
    echo "🔍 Checking security headers..."
    local headers=$(curl -s -I --max-time $TIMEOUT "$BASE_URL/healthz/live")

    # Security headers validation
    local required_headers=("x-frame-options" "x-content-type-options")

    for header in "${required_headers[@]}"; do
        if echo "$headers" | grep -qi "$header"; then
            echo "✅ $header header present"
        else
            echo "⚠️  $header header missing"
            ((failed_checks++))
        fi
    done

    # HTTPS verification (if applicable)
    if [[ "$BASE_URL" == https* ]]; then
        echo "🔍 Checking HTTPS configuration..."
        local ssl_info=$(curl -s -I --max-time $TIMEOUT "$BASE_URL/healthz/live" | head -n1)
        if echo "$ssl_info" | grep -q "200"; then
            echo "✅ HTTPS working correctly"
        else
            echo "❌ HTTPS configuration issues"
            ((failed_checks++))
        fi
    fi

    return $failed_checks
}

# Performance verification
performance_checks() {
    echo "🔵 Performance Verification"
    echo "==========================="

    local failed_checks=0

    # Load test simulation (light)
    echo "🔍 Running light load test..."

    local concurrent_requests=5
    local total_requests=25
    local success_count=0

    for i in $(seq 1 $concurrent_requests); do
        {
            for j in $(seq 1 $((total_requests / concurrent_requests))); do
                if curl -f -s --max-time 10 "$BASE_URL/healthz/live" >/dev/null 2>&1; then
                    ((success_count++))
                fi
            done
        } &
    done

    wait

    local success_rate=$((success_count * 100 / total_requests))
    if [[ $success_rate -ge 95 ]]; then
        echo "✅ Load test: $success_rate% success rate"
    else
        echo "❌ Load test: $success_rate% success rate (expected ≥95%)"
        ((failed_checks++))
    fi

    # Check if service remains stable under light load
    if ! http_check "$BASE_URL/healthz/ready" 200 "Post-load readiness check"; then
        ((failed_checks++))
    fi

    return $failed_checks
}

# Database connectivity verification
database_checks() {
    echo "🔵 Database Connectivity Verification"
    echo "====================================="

    local failed_checks=0

    # Database connectivity is tested via readiness probe
    # Additional checks can be added here for specific database operations

    if http_check "$BASE_URL/healthz/ready" 200 "Database connectivity (via readiness)" &>/dev/null; then
        echo "✅ Database connectivity verified through readiness probe"
    else
        echo "❌ Database connectivity issues detected"
        ((failed_checks++))
    fi

    return $failed_checks
}

# Monitoring integration verification
monitoring_checks() {
    echo "🔵 Monitoring Integration Verification"
    echo "======================================"

    local failed_checks=0

    # Check if metrics endpoint is available
    if http_check "$BASE_URL/metrics" 200 "Metrics endpoint"; then
        echo "✅ Prometheus metrics endpoint available"
    else
        echo "⚠️  Metrics endpoint not available"
        ((failed_checks++))
    fi

    # Basic metrics validation
    if command -v curl >/dev/null 2>&1; then
        local metrics=$(curl -s --max-time $TIMEOUT "$BASE_URL/metrics" || echo "")

        if echo "$metrics" | grep -q "http_requests_total"; then
            echo "✅ HTTP request metrics present"
        else
            echo "⚠️  Expected HTTP metrics not found"
            ((failed_checks++))
        fi
    fi

    return $failed_checks
}

# Version verification
version_checks() {
    echo "🔵 Version Verification"
    echo "======================="

    local failed_checks=0

    # Check if version information is available
    echo "🔍 Checking application version..."

    # Try to get version from headers or API
    local version_header=$(curl -s -I --max-time $TIMEOUT "$BASE_URL/healthz/live" | grep -i "x-version" || echo "")

    if [[ -n "$version_header" ]]; then
        echo "✅ Version information: $version_header"
    else
        echo "ℹ️  Version header not found (may not be implemented)"
    fi

    return $failed_checks
}

# Comprehensive test suite
run_smoke_tests() {
    echo "🔵 Comprehensive Smoke Tests"
    echo "============================"

    local failed_checks=0

    # Test critical user journeys
    echo "🔍 Testing critical user journeys..."

    # API Discovery
    if http_check "$BASE_URL/swagger.json" 200 "API documentation access"; then
        echo "✅ API documentation accessible"
    else
        ((failed_checks++))
    fi

    # Feature service discovery (if enabled)
    if http_check "$BASE_URL/rest/services" 200 "Feature services discovery" &>/dev/null; then
        echo "✅ Feature services discovery working"
    fi

    # CORS preflight (if applicable)
    local cors_response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "Origin: https://example.com" \
        -H "Access-Control-Request-Method: GET" \
        -H "Access-Control-Request-Headers: Content-Type" \
        -X OPTIONS \
        --max-time $TIMEOUT \
        "$BASE_URL/healthz/live" || echo "000")

    if [[ "$cors_response" == "200" || "$cors_response" == "204" ]]; then
        echo "✅ CORS preflight handling working"
    else
        echo "⚠️  CORS preflight response: HTTP $cors_response"
        ((failed_checks++))
    fi

    return $failed_checks
}

# Main verification function
main() {
    local total_failures=0
    local start_time=$(date +%s)

    echo "🚀 Post-Deployment Verification Started"
    echo "======================================="
    echo "Timestamp: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    echo "Target: $BASE_URL"
    echo "Environment: $ENVIRONMENT"
    echo ""

    # Wait for service to be fully ready
    echo "⏳ Waiting for service to be ready..."
    local wait_time=0
    while [[ $wait_time -lt $VERIFICATION_TIMEOUT ]]; do
        if curl -f -s --max-time 5 "$BASE_URL/healthz/ready" >/dev/null 2>&1; then
            echo "✅ Service is ready after ${wait_time}s"
            break
        fi
        echo "⏳ Service not ready, waiting... (${wait_time}s elapsed)"
        sleep 10
        wait_time=$((wait_time + 10))
    done

    if [[ $wait_time -ge $VERIFICATION_TIMEOUT ]]; then
        echo "❌ Service did not become ready within ${VERIFICATION_TIMEOUT}s"
        exit 1
    fi

    # Run all verification checks
    echo ""
    health_checks || total_failures=$((total_failures + $?))
    echo ""

    api_contract_checks || total_failures=$((total_failures + $?))
    echo ""

    security_checks || total_failures=$((total_failures + $?))
    echo ""

    performance_checks || total_failures=$((total_failures + $?))
    echo ""

    database_checks || total_failures=$((total_failures + $?))
    echo ""

    monitoring_checks || total_failures=$((total_failures + $?))
    echo ""

    version_checks || total_failures=$((total_failures + $?))
    echo ""

    run_smoke_tests || total_failures=$((total_failures + $?))
    echo ""

    # Generate final report
    local end_time=$(date +%s)
    local duration=$((end_time - start_time))

    echo "📊 Post-Deployment Verification Summary"
    echo "======================================="
    echo "Duration: ${duration} seconds"
    echo "Environment: $ENVIRONMENT"
    echo "Target: $BASE_URL"

    if [[ $total_failures -eq 0 ]]; then
        echo "🎉 All verification checks passed!"
        echo "✅ Deployment is ready for traffic"
        return 0
    else
        echo "⚠️  $total_failures verification check(s) failed"

        if [[ $total_failures -ge 5 ]]; then
            echo "🚨 Critical: Multiple failures - consider rollback"
            return 2
        else
            echo "⚠️  Warning: Some issues detected - investigate and monitor"
            return 1
        fi
    fi
}

# Check dependencies
if ! command -v curl >/dev/null 2>&1; then
    echo "❌ curl not found. Please install curl."
    exit 1
fi

main "$@"