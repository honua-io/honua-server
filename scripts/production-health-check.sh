#!/bin/bash
set -euo pipefail

# Production Health Check Script for Honua Server
# Comprehensive health verification for production deployments

BASE_URL="${BASE_URL:-https://api.honua.example.com}"
TIMEOUT="${TIMEOUT:-30}"
MAX_RETRIES="${MAX_RETRIES:-3}"
RETRY_DELAY="${RETRY_DELAY:-5}"

echo "🏥 Starting comprehensive production health checks"
echo "🔗 Base URL: $BASE_URL"

# Function to make HTTP requests with retries
http_request() {
    local url=$1
    local expected_status=${2:-200}
    local description=$3
    local retries=0

    echo "🔍 Checking $description..."

    while [[ $retries -lt $MAX_RETRIES ]]; do
        local status_code=$(curl -s -o /dev/null -w "%{http_code}" \
            --max-time $TIMEOUT \
            --connect-timeout 10 \
            "$url" || echo "000")

        if [[ "$status_code" == "$expected_status" ]]; then
            echo "✅ $description: HTTP $status_code"
            return 0
        else
            ((retries++))
            echo "⚠️  $description failed (HTTP $status_code), retry $retries/$MAX_RETRIES"
            if [[ $retries -lt $MAX_RETRIES ]]; then
                sleep $RETRY_DELAY
            fi
        fi
    done

    echo "❌ $description failed after $MAX_RETRIES attempts (HTTP $status_code)"
    return 1
}

# Function to check response time
check_response_time() {
    local url=$1
    local max_time=$2
    local description=$3

    echo "⏱️  Checking response time for $description (max: ${max_time}ms)..."

    local response_time=$(curl -s -o /dev/null -w "%{time_total}" \
        --max-time $TIMEOUT \
        "$url" | awk '{print int($1*1000)}')

    if [[ $response_time -le $max_time ]]; then
        echo "✅ $description: ${response_time}ms (within ${max_time}ms limit)"
        return 0
    else
        echo "❌ $description: ${response_time}ms (exceeds ${max_time}ms limit)"
        return 1
    fi
}

# Function to check JSON response structure
check_json_response() {
    local url=$1
    local expected_key=$2
    local description=$3

    echo "🔍 Checking JSON response for $description..."

    local response=$(curl -s --max-time $TIMEOUT "$url")
    local key_exists=$(echo "$response" | jq -r "has(\"$expected_key\")" 2>/dev/null || echo "false")

    if [[ "$key_exists" == "true" ]]; then
        echo "✅ $description: JSON response contains '$expected_key'"
        return 0
    else
        echo "❌ $description: JSON response missing '$expected_key'"
        echo "Response: $response"
        return 1
    fi
}

# Core health checks
core_health_checks() {
    local failed_checks=0

    echo "🔵 Core Health Checks"
    echo "===================="

    # Liveness probe
    if ! http_request "$BASE_URL/healthz/live" 200 "Liveness probe"; then
        ((failed_checks++))
    fi

    # Readiness probe
    if ! http_request "$BASE_URL/healthz/ready" 200 "Readiness probe"; then
        ((failed_checks++))
    fi

    # Response time check for health endpoints
    if ! check_response_time "$BASE_URL/healthz/live" 1000 "Liveness response time"; then
        ((failed_checks++))
    fi

    if ! check_response_time "$BASE_URL/healthz/ready" 2000 "Readiness response time"; then
        ((failed_checks++))
    fi

    return $failed_checks
}

# API endpoint checks
api_endpoint_checks() {
    local failed_checks=0

    echo "🔵 API Endpoint Checks"
    echo "======================"

    # Check if jq is available
    if ! command -v jq &> /dev/null; then
        echo "⚠️  jq not available, skipping JSON validation"
        return 0
    fi

    # OpenAPI spec endpoint
    if ! http_request "$BASE_URL/swagger.json" 200 "OpenAPI specification"; then
        ((failed_checks++))
    fi

    # Check API response time
    if ! check_response_time "$BASE_URL/swagger.json" 3000 "API spec response time"; then
        ((failed_checks++))
    fi

    # If we have a features endpoint, test it
    if http_request "$BASE_URL/rest/services" 200 "Feature services list" &>/dev/null; then
        echo "✅ Feature services endpoint available"

        # Check response time for features
        if ! check_response_time "$BASE_URL/rest/services" 5000 "Feature services response time"; then
            ((failed_checks++))
        fi
    else
        echo "ℹ️  Feature services endpoint not available (may be expected)"
    fi

    return $failed_checks
}

# Security checks
security_checks() {
    local failed_checks=0

    echo "🔵 Security Checks"
    echo "=================="

    # Check security headers
    local headers=$(curl -s -I --max-time $TIMEOUT "$BASE_URL/healthz/live")

    # Check for security headers
    if echo "$headers" | grep -qi "x-frame-options"; then
        echo "✅ X-Frame-Options header present"
    else
        echo "⚠️  X-Frame-Options header missing"
        ((failed_checks++))
    fi

    if echo "$headers" | grep -qi "x-content-type-options"; then
        echo "✅ X-Content-Type-Options header present"
    else
        echo "⚠️  X-Content-Type-Options header missing"
        ((failed_checks++))
    fi

    # Check HTTPS redirect (if base URL is HTTP)
    if [[ "$BASE_URL" == http* ]] && [[ "$BASE_URL" != https* ]]; then
        local https_url="${BASE_URL/http:/https:}"
        if http_request "$https_url/healthz/live" 200 "HTTPS availability" &>/dev/null; then
            echo "✅ HTTPS available"
        else
            echo "⚠️  HTTPS not properly configured"
            ((failed_checks++))
        fi
    fi

    return $failed_checks
}

# Performance checks
performance_checks() {
    local failed_checks=0

    echo "🔵 Performance Checks"
    echo "====================="

    # DNS resolution time
    local domain=$(echo "$BASE_URL" | sed 's|https\?://||' | cut -d/ -f1)
    echo "🔍 Checking DNS resolution for $domain..."

    local dns_time=$(time -p nslookup "$domain" 2>/dev/null | grep real | awk '{print $2}' || echo "0")
    if [[ $(echo "$dns_time < 1.0" | bc -l 2>/dev/null || echo "1") == "1" ]]; then
        echo "✅ DNS resolution: ${dns_time}s"
    else
        echo "⚠️  Slow DNS resolution: ${dns_time}s"
        ((failed_checks++))
    fi

    # TCP connection time
    local connect_time=$(curl -s -o /dev/null -w "%{time_connect}" \
        --max-time $TIMEOUT "$BASE_URL/healthz/live")

    if [[ $(echo "$connect_time < 0.5" | bc -l 2>/dev/null || echo "1") == "1" ]]; then
        echo "✅ TCP connection time: ${connect_time}s"
    else
        echo "⚠️  Slow TCP connection: ${connect_time}s"
        ((failed_checks++))
    fi

    # TLS handshake time (if HTTPS)
    if [[ "$BASE_URL" == https* ]]; then
        local tls_time=$(curl -s -o /dev/null -w "%{time_appconnect}" \
            --max-time $TIMEOUT "$BASE_URL/healthz/live")

        if [[ $(echo "$tls_time < 1.0" | bc -l 2>/dev/null || echo "1") == "1" ]]; then
            echo "✅ TLS handshake time: ${tls_time}s"
        else
            echo "⚠️  Slow TLS handshake: ${tls_time}s"
            ((failed_checks++))
        fi
    fi

    return $failed_checks
}

# Database connectivity check (if possible)
database_checks() {
    local failed_checks=0

    echo "🔵 Database Checks"
    echo "=================="

    # The readiness probe should include database checks
    # So if readiness passes, database is likely healthy
    if http_request "$BASE_URL/healthz/ready" 200 "Database connectivity (via readiness)" &>/dev/null; then
        echo "✅ Database connectivity (inferred from readiness probe)"
    else
        echo "❌ Database connectivity issues (readiness probe failed)"
        ((failed_checks++))
    fi

    return $failed_checks
}

# Summary report
generate_summary() {
    local total_failures=$1

    echo ""
    echo "📊 Health Check Summary"
    echo "======================"

    if [[ $total_failures -eq 0 ]]; then
        echo "🎉 All health checks passed! System is healthy."
        echo "✅ Production deployment is ready for traffic."
        return 0
    else
        echo "⚠️  $total_failures health check(s) failed."
        echo "❌ Production deployment may have issues."

        if [[ $total_failures -ge 5 ]]; then
            echo "🚨 Critical: Multiple failures detected - consider rollback"
            return 2
        else
            echo "⚠️  Warning: Some issues detected - monitor closely"
            return 1
        fi
    fi
}

# Main health check execution
main() {
    local total_failures=0

    echo "🏥 Production Health Check Report"
    echo "================================="
    echo "Timestamp: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    echo "Target: $BASE_URL"
    echo ""

    # Run all health check categories
    core_health_checks || total_failures=$((total_failures + $?))
    echo ""

    api_endpoint_checks || total_failures=$((total_failures + $?))
    echo ""

    security_checks || total_failures=$((total_failures + $?))
    echo ""

    performance_checks || total_failures=$((total_failures + $?))
    echo ""

    database_checks || total_failures=$((total_failures + $?))
    echo ""

    # Generate final summary
    generate_summary $total_failures
    exit $?
}

# Validate dependencies
if ! command -v curl &> /dev/null; then
    echo "❌ curl not found. Please install curl."
    exit 1
fi

main "$@"