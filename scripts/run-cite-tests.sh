#!/bin/bash

# OGC API Features CITE conformance testing script for Honua Server
# Runs the official OGC Compliance and Interoperability Testing & Evaluation (CITE) suite

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
CITE_COMPOSE_FILE="docker/cite-compose.yml"
CITE_RESULTS_DIR="cite-results"
CITE_RESULTS_CONTAINER_DIR="/root/te_base/users/cite/logs"
CITE_TIMEOUT=1800  # 30 minutes timeout
HONUA_HEALTHCHECK_TIMEOUT=300  # 5 minutes
POSTGRES_HEALTHCHECK_TIMEOUT=120  # 2 minutes
PASSED_TESTS=0
FAILED_TESTS=0
SKIPPED_TESTS=0
CANTTELL_TESTS=0
TOTAL_TESTS=0

echo -e "${BLUE}🧪 OGC API Features CITE Conformance Tests${NC}"
echo "============================================="

# Parse command line options
CLEANUP=true
INTERACTIVE=false
VERBOSE=false
PROFILE="full"

while [[ $# -gt 0 ]]; do
    case $1 in
        --no-cleanup)
            CLEANUP=false
            shift
            ;;
        --interactive)
            INTERACTIVE=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --profile)
            PROFILE="$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --no-cleanup      Don't cleanup containers after tests"
            echo "  --interactive     Run in interactive mode (keep containers running)"
            echo "  --verbose         Enable verbose logging"
            echo "  --profile PROF    Use specific CITE profile (default: full)"
            echo "  --help, -h        Show this help"
            echo ""
            echo "Examples:"
            echo "  $0                        Run CITE tests with cleanup"
            echo "  $0 --no-cleanup          Run tests and keep containers for debugging"
            echo "  $0 --interactive         Run and keep everything running for manual testing"
            echo "  $0 --profile minimal     Run minimal conformance profile"
            exit 0
            ;;
        *)
            echo -e "${RED}❌ Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

# Check prerequisites
echo -e "${YELLOW}Checking prerequisites...${NC}"

if ! command -v docker &> /dev/null; then
    echo -e "${RED}❌ Docker not found. Please install Docker${NC}"
    exit 1
fi

if ! command -v docker-compose &> /dev/null && ! command -v docker compose &> /dev/null; then
    echo -e "${RED}❌ Docker Compose not found. Please install Docker Compose${NC}"
    exit 1
fi

# Determine docker compose command
if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
else
    COMPOSE_CMD="docker compose"
fi

wait_for_honua_server() {
    local context_label="${1:-Honua Server}"
    local start_time current_time elapsed

    start_time=$(date +%s)
    while true; do
        current_time=$(date +%s)
        elapsed=$((current_time - start_time))

        if [[ $elapsed -gt $HONUA_HEALTHCHECK_TIMEOUT ]]; then
            echo -e "${RED}❌ Timeout waiting for ${context_label} to become healthy${NC}"
            echo "Check logs with: $COMPOSE_CMD -f $CITE_COMPOSE_FILE logs honua-server"
            return 1
        fi

        if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps honua-server | grep -q "healthy"; then
            return 0
        fi

        if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps honua-server | grep -Eq "Exit|Restarting"; then
            return 2
        fi

        echo "Waiting for ${context_label}... (${elapsed}s elapsed)"
        sleep 5
    done
}

# Build Honua Server image if it doesn't exist
echo -e "${YELLOW}Building Honua Server Docker image...${NC}"
if ! docker build -t honua-server:latest .; then
    echo -e "${RED}❌ Failed to build Honua Server Docker image${NC}"
    exit 1
fi

echo -e "${GREEN}✅ Honua Server image built successfully${NC}"

# Cleanup function
cleanup() {
    if [[ "$CLEANUP" == "true" && "$INTERACTIVE" == "false" ]]; then
        echo -e "\n${YELLOW}🧹 Cleaning up containers and networks...${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    fi
}

# Set trap for cleanup
trap cleanup EXIT

# Start the test environment
echo -e "${YELLOW}Starting CITE test environment...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true

# Start Postgres first so the database can be seeded before Honua Server caches metadata
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d postgres

# Wait for Postgres to be healthy
echo -e "${YELLOW}Waiting for Postgres to be ready...${NC}"
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $POSTGRES_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}❌ Timeout waiting for Postgres to become healthy${NC}"
        echo "Check logs with: $COMPOSE_CMD -f $CITE_COMPOSE_FILE logs postgres"
        exit 1
    fi

    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps postgres | grep -q "healthy"; then
        break
    fi

    echo "Waiting for Postgres... (${elapsed}s elapsed)"
    sleep 5
done

echo -e "${GREEN}✅ Postgres is healthy${NC}"

# Start Honua Server once to apply migrations, then stop to avoid caching empty catalog results
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d honua-server

echo -e "${YELLOW}Waiting for Honua Server to be ready (migrations)...${NC}"
if ! wait_for_honua_server "Honua Server (migrations)"; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server || true
    exit 1
fi

echo -e "${GREEN}✅ Honua Server is healthy${NC}"

echo -e "${YELLOW}Stopping Honua Server to seed data...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" stop honua-server

# Seed the database now that migrations exist
echo -e "${YELLOW}Seeding CITE database...${NC}"
POSTGRES_CONTAINER=$($COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps -q postgres)
if [[ -z "$POSTGRES_CONTAINER" ]]; then
    echo -e "${RED}❌ Postgres container not found${NC}"
    exit 1
fi

docker cp docker/cite-seed.sql "$POSTGRES_CONTAINER":/tmp/cite-seed.sql
docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cite -f /tmp/cite-seed.sql >/dev/null
echo -e "${GREEN}✅ CITE database seeded${NC}"

# Restart Honua Server after seeding. Retry on transient startup failures.
echo -e "${YELLOW}Starting Honua Server after seed...${NC}"
start_attempt=1
max_start_attempts=3
while true; do
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d honua-server || true

    echo -e "${YELLOW}Waiting for Honua Server to be ready...${NC}"
    if wait_for_honua_server "Honua Server"; then
        break
    fi

    if [[ $start_attempt -ge $max_start_attempts ]]; then
        echo -e "${RED}❌ Honua Server failed to become healthy after ${max_start_attempts} attempts${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server || true
        exit 1
    fi

    echo -e "${YELLOW}⚠️ Honua Server startup failed (attempt ${start_attempt}/${max_start_attempts}); retrying...${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" rm -f -s honua-server >/dev/null 2>&1 || true
    start_attempt=$((start_attempt + 1))
    sleep 5
done

echo -e "${GREEN}✅ Honua Server is healthy${NC}"

# Start CITE engine after Honua Server is confirmed healthy.
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d cite-engine

# Verify API endpoints are responding (after seeding to avoid cached empty data)
echo -e "${YELLOW}Verifying OGC API Features endpoints...${NC}"

# Test landing page
if ! curl -s -f http://localhost:8080/ogc/features > /dev/null; then
    echo -e "${RED}❌ Landing page not accessible${NC}"
    exit 1
fi

# Test conformance endpoint
if ! curl -s -f http://localhost:8080/ogc/features/conformance > /dev/null; then
    echo -e "${RED}❌ Conformance endpoint not accessible${NC}"
    exit 1
fi

# Test collections endpoint
if ! curl -s -f http://localhost:8080/ogc/features/collections > /dev/null; then
    echo -e "${RED}❌ Collections endpoint not accessible${NC}"
    exit 1
fi

echo -e "${GREEN}✅ OGC API Features endpoints are accessible${NC}"

if [[ "$INTERACTIVE" == "true" ]]; then
    echo -e "${BLUE}🔗 Interactive mode enabled${NC}"
    echo "Services are running at:"
    echo "  Honua Server:     http://localhost:8080"
    echo "  CITE Team Engine: http://localhost:8081/teamengine"
    echo "  PostgreSQL:       localhost:5433"
    echo ""
    echo "Run CITE tests manually via Team Engine web interface"
    echo "Press Ctrl+C to stop all services"

    # Wait indefinitely
    tail -f /dev/null
fi

# Create results directory
mkdir -p "$CITE_RESULTS_DIR"
rm -rf "$CITE_RESULTS_DIR"/*

# Capture conformance declaration for CI gating
echo -e "${YELLOW}Capturing conformance declaration...${NC}"
if ! curl -s -f http://localhost:8080/ogc/features/conformance > "$CITE_RESULTS_DIR/conformance.json"; then
    echo -e "${RED}❌ Failed to capture conformance declaration${NC}"
    exit 1
fi

# Update test parameters with current profile
if [[ "$PROFILE" != "default" ]]; then
    echo -e "${YELLOW}Using CITE profile: $PROFILE${NC}"
    # Update test-params.xml with the specified profile
    sed -i "s/<parsers:profile>.*<\/parsers:profile>/<parsers:profile>$PROFILE<\/parsers:profile>/" docker/cite-config/test-params.xml
fi

# Run CITE tests
echo -e "${YELLOW}Running OGC API Features CITE conformance tests...${NC}"
echo "This may take several minutes..."

# Start the CITE runner with test profile
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" rm -f -s cite-runner >/dev/null 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" --profile test up --force-recreate cite-runner

# Wait for tests to complete
echo -e "${YELLOW}Waiting for CITE tests to complete...${NC}"
start_time=$(date +%s)

while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $CITE_TIMEOUT ]]; then
        echo -e "${RED}❌ CITE tests timed out after ${CITE_TIMEOUT} seconds${NC}"
        break
    fi

    # Check if cite-runner container has finished
    if ! $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps cite-runner | grep -q "Up"; then
        break
    fi

    if [[ $((elapsed % 30)) -eq 0 ]]; then
        echo "CITE tests running... (${elapsed}s elapsed)"
    fi

    sleep 5
done

# Copy results from Docker volume
echo -e "${YELLOW}Extracting CITE test results...${NC}"
CITE_RUNNER_CONTAINER=$($COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps -aq cite-runner 2>/dev/null | tail -n 1 || echo "")
if [[ -n "$CITE_RUNNER_CONTAINER" ]]; then
    docker cp "$CITE_RUNNER_CONTAINER":"$CITE_RESULTS_CONTAINER_DIR"/. "$CITE_RESULTS_DIR/" 2>/dev/null || true
fi

# Analyze results
echo -e "\n${BLUE}📊 CITE Test Results Analysis${NC}"
echo "==============================="

RESULTS_FOUND=false
if [[ -d "$CITE_RESULTS_DIR" && $(ls -A "$CITE_RESULTS_DIR" 2>/dev/null) ]]; then
    RESULTS_FOUND=true
    echo "Results saved to: $CITE_RESULTS_DIR/"

    # Look for test result files
    RESULTS_XML=$(find "$CITE_RESULTS_DIR" -type f -name "testng-results.xml" | sort | tail -n 1)
    if [[ -n "$RESULTS_XML" && -f "$RESULTS_XML" ]]; then
        echo -e "${GREEN}✅ Test result files found${NC}"
        TOTAL_TESTS=$(sed -n 's/.*total="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        PASSED_TESTS=$(sed -n 's/.*passed="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        FAILED_TESTS=$(sed -n 's/.*failed="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        SKIPPED_TESTS=$(sed -n 's/.*skipped="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        CANTTELL_TESTS=0
    elif find "$CITE_RESULTS_DIR" -type f \( -name "*.xml" -o -name "*.html" \) -print -quit | grep -q .; then
        echo -e "${GREEN}✅ Test result files found${NC}"
        # Fallback if summary files are present
        PASSED_TESTS=$(wc -l < "$CITE_RESULTS_DIR/passed-tests.txt" 2>/dev/null || echo "0")
        FAILED_TESTS=$(wc -l < "$CITE_RESULTS_DIR/failed-tests.txt" 2>/dev/null || echo "0")
        SKIPPED_TESTS=$(wc -l < "$CITE_RESULTS_DIR/skipped-tests.txt" 2>/dev/null || echo "0")
        CANTTELL_TESTS=$(wc -l < "$CITE_RESULTS_DIR/canttell-tests.txt" 2>/dev/null || echo "0")
        TOTAL_TESTS=$((PASSED_TESTS + FAILED_TESTS + SKIPPED_TESTS + CANTTELL_TESTS))
    else
        echo -e "${YELLOW}⚠️ No test result files found${NC}"
    fi

    TOTAL_TESTS=${TOTAL_TESTS:-0}
    PASSED_TESTS=${PASSED_TESTS:-0}
    FAILED_TESTS=${FAILED_TESTS:-0}
    SKIPPED_TESTS=${SKIPPED_TESTS:-0}
    CANTTELL_TESTS=${CANTTELL_TESTS:-0}
    TOTAL_TESTS=$((PASSED_TESTS + FAILED_TESTS + SKIPPED_TESTS + CANTTELL_TESTS))

    echo "Total tests executed: $TOTAL_TESTS"
    echo "Tests passed: $PASSED_TESTS"
    echo "Tests failed: $FAILED_TESTS"
    echo "Tests skipped: $SKIPPED_TESTS"
    echo "Tests canttell: $CANTTELL_TESTS"

    if [[ $FAILED_TESTS -eq 0 && $TOTAL_TESTS -gt 0 ]]; then
        echo -e "${GREEN}🎉 All CITE conformance tests passed!${NC}"
    elif [[ $TOTAL_TESTS -gt 0 ]]; then
        echo -e "${YELLOW}⚠️ Some tests failed. Review results for details.${NC}"
    fi
else
    echo -e "${RED}❌ No test results found${NC}"
fi

# Show logs if verbose or if tests failed
if [[ "$VERBOSE" == "true" || $FAILED_TESTS -gt 0 ]]; then
    echo -e "\n${BLUE}📋 Service Logs${NC}"
    echo "==============="
    echo -e "${YELLOW}Honua Server logs:${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs --tail=50 honua-server || true

    echo -e "\n${YELLOW}CITE Runner logs:${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-runner || true
fi

# Generate summary report
echo -e "\n${BLUE}📄 Generating CITE Summary Report${NC}"
cat > "$CITE_RESULTS_DIR/cite-summary.md" << EOF
# OGC API Features CITE Conformance Test Results

**Execution Date**: $(date)
**Profile**: $PROFILE
**Honua Server Version**: $(git describe --tags --always 2>/dev/null || echo "unknown")

## Test Summary

- **Total Tests**: $TOTAL_TESTS
- **Passed**: $PASSED_TESTS
- **Failed**: $FAILED_TESTS
- **Skipped**: $SKIPPED_TESTS
- **CantTell**: $CANTTELL_TESTS
- **Success Rate**: $(( TOTAL_TESTS > 0 ? (PASSED_TESTS * 100) / TOTAL_TESTS : 0 ))%

## Test Environment

- **Honua Server**: http://localhost:8080
- **Database**: PostgreSQL with PostGIS
- **CITE Version**: Latest OGC API Features 1.0 test suite

## Conformance Classes Tested

**Part 1 - Core:**
- Core (http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core)
- OpenAPI 3.0 (http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30)
- HTML (http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html)
- GeoJSON (http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson)

**Part 2 - Coordinate Reference Systems:**
- CRS (http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs)

**Part 3 - Filtering:**
- Filter (http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter)
- Features Filter (http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/features-filter)
- Simple CQL (http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/simple-cql)
- CQL Text (http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/cql-text)
- Queryables (http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables)

## Results

$(if [[ $FAILED_TESTS -eq 0 && $TOTAL_TESTS -gt 0 ]]; then
    echo "✅ **PASSED**: All conformance tests passed successfully."
elif [[ $TOTAL_TESTS -gt 0 ]]; then
    echo "⚠️ **PARTIAL**: Some tests failed. Review detailed results."
else
    echo "❌ **ERROR**: No tests were executed successfully."
fi)

## Next Steps

$(if [[ $TOTAL_TESTS -eq 0 ]]; then
    echo "1. Confirm the results were copied from the CITE runner"
    echo "2. Check cite-results for testng-results.xml output"
    echo "3. Re-run CITE tests to validate output capture"
elif [[ $FAILED_TESTS -gt 0 ]]; then
    echo "1. Review failed test details in the XML/HTML result files"
    echo "2. Fix conformance issues in the Honua Server implementation"
    echo "3. Re-run CITE tests to validate fixes"
else
    echo "1. CITE conformance validated successfully"
    echo "2. Consider testing additional conformance classes"
    echo "3. Include CITE testing in CI pipeline"
fi)

## Files

$(find "$CITE_RESULTS_DIR" -type f \( -name "*.xml" -o -name "*.html" -o -name "*.log" \) 2>/dev/null | sort || echo "No result files found")

---
Generated by: $0
EOF

echo -e "${GREEN}✅ Summary report saved to: $CITE_RESULTS_DIR/cite-summary.md${NC}"

# Final status
if [[ $FAILED_TESTS -eq 0 && $TOTAL_TESTS -gt 0 ]]; then
    echo -e "\n${GREEN}🎉 CITE conformance testing completed successfully!${NC}"
    exit 0
elif [[ $TOTAL_TESTS -gt 0 ]]; then
    echo -e "\n${YELLOW}⚠️ CITE testing completed with failures. Review results.${NC}"
    exit 1
else
    echo -e "\n${RED}❌ CITE testing failed to execute properly.${NC}"
    exit 1
fi
