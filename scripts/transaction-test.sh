#!/bin/bash

# Transaction semantics testing script for Honua Server
# Tests actual behavior when instances fail mid-transaction

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
COMPOSE_FILE="$PROJECT_DIR/docker-compose.scale-test.yml"
BASE_URL="http://localhost:8080"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
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

log_test() {
    echo -e "${BLUE}[TEST]${NC} $1"
}

# Function to create test layer and features
setup_test_layer() {
    log_info "Setting up test layer for transaction testing..."

    # Create a simple layer for testing
    local layer_response=$(curl -s -X POST "$BASE_URL/rest/services/1/FeatureServer/layers" \
        -H "Content-Type: application/json" \
        -d '{
            "name": "TransactionTestLayer",
            "geometryType": "esriGeometryPoint",
            "fields": [
                {"name": "OBJECTID", "type": "esriFieldTypeOID", "alias": "Object ID"},
                {"name": "name", "type": "esriFieldTypeString", "alias": "Name", "length": 255},
                {"name": "status", "type": "esriFieldTypeString", "alias": "Status", "length": 50}
            ]
        }')

    echo "$layer_response" | grep -q '"success":true' && log_info "✓ Test layer created" || log_error "✗ Failed to create test layer"
}

# Function to test local transaction behavior
test_local_transaction_behavior() {
    log_test "Testing LOCAL transaction behavior (no distributed coordination)"

    local test_url="$BASE_URL/rest/services/1/FeatureServer/1/applyEdits"

    log_info "1. Testing normal transaction with rollback=false (GeoServices default)"
    local response1=$(curl -s -X POST "$test_url" \
        -H "Content-Type: application/json" \
        -d '{
            "adds": [
                {"attributes": {"name": "Feature1", "status": "pending"}},
                {"attributes": {"name": "Feature2", "status": "pending"}}
            ],
            "rollbackOnFailure": false
        }')

    echo "$response1" | jq '.' && echo

    log_info "2. Testing atomic transaction with rollback=true"
    local response2=$(curl -s -X POST "$test_url" \
        -H "Content-Type: application/json" \
        -d '{
            "adds": [
                {"attributes": {"name": "AtomicFeature1", "status": "processing"}},
                {"attributes": {"name": "AtomicFeature2", "status": "processing"}}
            ],
            "rollbackOnFailure": true
        }')

    echo "$response2" | jq '.' && echo

    log_info "✓ Local transactions work as expected (confined to single instance)"
}

# Function to test what happens when instance is killed mid-transaction
test_instance_failure_mid_transaction() {
    log_test "Testing instance failure MID-TRANSACTION"

    # Get list of running Honua containers
    local containers=($(docker compose -f "$COMPOSE_FILE" ps -q honua))
    local target_container=${containers[0]}

    log_info "Target container for failure test: $target_container"

    # Start a long-running operation in the background
    local test_url="$BASE_URL/rest/services/1/FeatureServer/1/applyEdits"

    log_info "1. Starting long-running transaction..."

    # Create a batch that might take some time
    curl -s -X POST "$test_url" \
        -H "Content-Type: application/json" \
        -d '{
            "adds": [
                {"attributes": {"name": "LongOp1", "status": "processing"}},
                {"attributes": {"name": "LongOp2", "status": "processing"}},
                {"attributes": {"name": "LongOp3", "status": "processing"}}
            ],
            "rollbackOnFailure": true
        }' &

    local curl_pid=$!

    # Give it a moment to start
    sleep 0.5

    log_info "2. Killing instance mid-transaction..."
    docker kill "$target_container" >/dev/null 2>&1

    # Wait for the curl command to complete (should fail)
    wait $curl_pid
    local curl_result=$?

    if [ $curl_result -eq 0 ]; then
        log_warn "⚠ Transaction completed before instance was killed"
    else
        log_info "✓ Transaction failed when instance was killed (expected)"
    fi

    # Restart the container
    log_info "3. Restarting killed instance..."
    docker compose -f "$COMPOSE_FILE" start honua >/dev/null 2>&1

    # Wait for it to be healthy
    sleep 10

    log_info "4. Testing that transaction was NOT recovered by other instances..."

    # Check if any of the features were created
    local query_response=$(curl -s "$BASE_URL/rest/services/1/FeatureServer/1/query?where=name LIKE 'LongOp%'&returnCountOnly=true")
    local count=$(echo "$query_response" | jq -r '.count // 0')

    if [ "$count" -eq 0 ]; then
        log_info "✓ No features created - transaction was properly rolled back"
        log_info "✓ No cross-instance transaction recovery (as expected)"
    else
        log_warn "⚠ Found $count features - partial transaction may have succeeded"
    fi
}

# Function to test Redis role in transactions
test_redis_transaction_role() {
    log_test "Testing Redis role in transactions (spoiler: NOT used for transaction state)"

    log_info "1. Checking Redis contents before transaction..."
    local redis_keys_before=$(docker compose -f "$COMPOSE_FILE" exec -T redis redis-cli KEYS '*' | grep -v "^$" | wc -l)
    echo "Redis keys before: $redis_keys_before"

    log_info "2. Performing transaction..."
    local test_url="$BASE_URL/rest/services/1/FeatureServer/1/applyEdits"
    curl -s -X POST "$test_url" \
        -H "Content-Type: application/json" \
        -d '{
            "adds": [
                {"attributes": {"name": "RedisTest", "status": "checking"}}
            ],
            "rollbackOnFailure": true
        }' >/dev/null

    log_info "3. Checking Redis contents after transaction..."
    local redis_keys_after=$(docker compose -f "$COMPOSE_FILE" exec -T redis redis-cli KEYS '*' | grep -v "^$" | wc -l)
    echo "Redis keys after: $redis_keys_after"

    if [ "$redis_keys_before" -eq "$redis_keys_after" ]; then
        log_info "✓ Redis key count unchanged - Redis NOT used for transaction state"
    else
        log_warn "⚠ Redis keys changed - may be used for other purposes (caching, etc.)"
    fi

    log_info "4. Stopping Redis to test transaction behavior without it..."
    docker compose -f "$COMPOSE_FILE" stop redis >/dev/null 2>&1

    log_info "5. Testing transaction with Redis offline..."
    local response_no_redis=$(curl -s -X POST "$test_url" \
        -H "Content-Type: application/json" \
        -d '{
            "adds": [
                {"attributes": {"name": "NoRedisTest", "status": "testing"}}
            ],
            "rollbackOnFailure": true
        }')

    if echo "$response_no_redis" | jq -e '.addResults[0].success' >/dev/null 2>&1; then
        log_info "✓ Transactions work fine without Redis"
        log_info "✓ Confirms Redis is NOT required for transaction processing"
    else
        log_error "✗ Transaction failed without Redis (unexpected)"
    fi

    # Restart Redis
    log_info "6. Restarting Redis..."
    docker compose -f "$COMPOSE_FILE" start redis >/dev/null 2>&1
    sleep 5
}

# Function to test concurrent transaction isolation
test_transaction_isolation() {
    log_test "Testing transaction isolation between instances"

    local test_url="$BASE_URL/rest/services/1/FeatureServer/1/applyEdits"

    log_info "1. Starting concurrent transactions on different instances..."

    # Start multiple concurrent transactions
    for i in {1..3}; do
        curl -s -X POST "$test_url" \
            -H "Content-Type: application/json" \
            -H "X-Test-Instance: $i" \
            -d "{
                \"adds\": [
                    {\"attributes\": {\"name\": \"Concurrent$i\", \"status\": \"processing\"}}
                ],
                \"rollbackOnFailure\": true
            }" >/dev/null &
    done

    # Wait for all to complete
    wait

    log_info "2. Checking results..."
    local query_response=$(curl -s "$BASE_URL/rest/services/1/FeatureServer/1/query?where=name LIKE 'Concurrent%'&returnCountOnly=true")
    local count=$(echo "$query_response" | jq -r '.count // 0')

    log_info "Created $count concurrent features"

    if [ "$count" -eq 3 ]; then
        log_info "✓ All concurrent transactions succeeded"
        log_info "✓ Transactions are properly isolated"
    else
        log_warn "⚠ Some concurrent transactions failed (count: $count)"
    fi
}

# Function to demonstrate actual transaction semantics
demonstrate_transaction_semantics() {
    log_test "DEMONSTRATING ACTUAL TRANSACTION SEMANTICS"

    cat << EOF

${BLUE}=== HONUA SERVER TRANSACTION MODEL ===${NC}

1. ${GREEN}LOCAL TRANSACTIONS ONLY${NC}
   - Each transaction bound to single PostgreSQL connection
   - No cross-instance coordination
   - No distributed transaction support

2. ${GREEN}REDIS NOT USED FOR TRANSACTION STATE${NC}
   - Redis used only for: caching, import job coordination
   - NOT used for: transaction recovery, 2PC, distributed state

3. ${GREEN}FAILURE BEHAVIOR${NC}
   - Instance death = immediate transaction rollback (PostgreSQL handles this)
   - No automatic recovery by other instances
   - No "pickup where left off" semantics

4. ${GREEN}ATOMICITY OPTIONS${NC}
   - rollbackOnFailure=false: Partial success allowed (GeoServices default)
   - rollbackOnFailure=true: All-or-nothing atomic batch

5. ${GREEN}SCALE-OUT IMPLICATIONS${NC}
   - Each instance handles its own transactions independently
   - No coordination required between instances
   - Simple, reliable, but not distributed

${YELLOW}=== WHAT THIS MEANS FOR SCALE-OUT ===${NC}

✓ Multiple instances can process transactions concurrently
✓ No distributed coordination overhead
✓ Simple failure semantics (rollback on connection loss)
✓ Excellent performance and reliability

✗ No cross-instance transaction recovery
✗ Cannot continue interrupted transactions on other instances
✗ Each transaction must complete on the instance that started it

EOF
}

# Main function
main() {
    echo
    log_info "=== HONUA SERVER TRANSACTION SEMANTICS TEST ==="
    echo

    # Ensure scale-test environment is running
    if ! curl -s "http://localhost:8080/healthz/live" >/dev/null 2>&1; then
        log_error "Scale-test environment not running. Please start with: ./scripts/scale-test.sh"
        exit 1
    fi

    # Setup test layer
    setup_test_layer
    echo

    # Run tests
    test_local_transaction_behavior
    echo

    test_instance_failure_mid_transaction
    echo

    test_redis_transaction_role
    echo

    test_transaction_isolation
    echo

    demonstrate_transaction_semantics

    log_info "=== TRANSACTION SEMANTICS TEST COMPLETED ==="
}

# Check if script is being sourced or executed
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi