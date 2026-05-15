#!/bin/bash

# WCS 2.0 CITE conformance testing script for Honua Server.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

CITE_COMPOSE_FILE="docker/cite/wcs20/compose.yml"
CITE_RESULTS_DIR="cite-wcs20-results"
CITE_TIMEOUT=2700
HONUA_HEALTHCHECK_TIMEOUT=300
POSTGRES_HEALTHCHECK_TIMEOUT=120
HONUA_CITE_WCS20_SERVER_PORT="${HONUA_CITE_WCS20_SERVER_PORT:-8092}"
HONUA_CITE_WCS20_POSTGRES_PORT="${HONUA_CITE_WCS20_POSTGRES_PORT:-5438}"
HONUA_CITE_WCS20_TEAMENGINE_PORT="${HONUA_CITE_WCS20_TEAMENGINE_PORT:-8086}"
export HONUA_CITE_WCS20_SERVER_PORT
export HONUA_CITE_WCS20_POSTGRES_PORT
export HONUA_CITE_WCS20_TEAMENGINE_PORT

PASSED_TESTS=0
FAILED_TESTS=0
SKIPPED_TESTS=0
CANTTELL_TESTS=0
TOTAL_TESTS=0

CLEANUP=true
INTERACTIVE=false
VERBOSE=false
PROFILE="core"
SKIP_BUILD="${HONUA_CITE_SKIP_BUILD:-false}"

echo -e "${BLUE}WCS 2.0 CITE Conformance Tests${NC}"
echo "================================"

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
        --skip-build)
            SKIP_BUILD=true
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --no-cleanup      Don't cleanup containers after tests"
            echo "  --interactive     Run in interactive mode (keep containers running)"
            echo "  --verbose         Enable verbose logging"
            echo "  --profile PROF    Use CITE profile (core|crs|extensions|full)"
            echo "  --skip-build      Reuse existing honua-server:latest image"
            echo "  --help, -h        Show this help"
            echo ""
            echo "Examples:"
            echo "  $0                        Run core WCS 2.0 tests"
            echo "  $0 --profile crs          Run core plus CRS extension tests"
            echo "  $0 --interactive          Keep services running for manual TeamEngine use"
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

case "$PROFILE" in
    core|crs|extensions|full)
        ;;
    *)
        echo -e "${RED}Unknown WCS CITE profile: $PROFILE${NC}"
        echo "Valid profiles: core, crs, extensions, full"
        exit 1
        ;;
esac

echo -e "${YELLOW}Checking prerequisites...${NC}"

if ! command -v docker &> /dev/null; then
    echo -e "${RED}Docker not found. Please install Docker${NC}"
    exit 1
fi

if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
elif command -v docker compose &> /dev/null; then
    COMPOSE_CMD="docker compose"
else
    echo -e "${RED}Docker Compose not found. Please install Docker Compose${NC}"
    exit 1
fi

if [[ ! -f "$CITE_COMPOSE_FILE" ]]; then
    echo -e "${RED}CITE Docker Compose file not found: $CITE_COMPOSE_FILE${NC}"
    exit 1
fi

if [[ "$SKIP_BUILD" == "true" ]]; then
    echo -e "${YELLOW}Skipping Honua Server Docker image build; using existing honua-server:latest${NC}"
else
    echo -e "${YELLOW}Building Honua Server Docker image...${NC}"
    if ! scripts/docker/build-with-github-packages.sh -t honua-server:latest .; then
        echo -e "${RED}Failed to build Honua Server Docker image${NC}"
        exit 1
    fi

    echo -e "${GREEN}Honua Server image built successfully${NC}"
fi

cleanup() {
    if [[ "$CLEANUP" == "true" && "$INTERACTIVE" == "false" ]]; then
        echo -e "\n${YELLOW}Cleaning up containers and networks...${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    fi
}

trap cleanup EXIT

reset_results_dir() {
    mkdir -p "$CITE_RESULTS_DIR"

    docker run --rm \
        -v "$(pwd)/$CITE_RESULTS_DIR:/results" \
        alpine:3.22 \
        sh -c "rm -rf /results/* /results/.[!.]* /results/..?* 2>/dev/null || true; chown -R $(id -u):$(id -g) /results"
}

wait_for_service_health() {
    local service_name="$1"
    local timeout_seconds="$2"
    local label="$3"
    local start_time current_time elapsed

    start_time=$(date +%s)
    while true; do
        current_time=$(date +%s)
        elapsed=$((current_time - start_time))

        if [[ $elapsed -gt $timeout_seconds ]]; then
            echo -e "${RED}Timeout waiting for ${label}${NC}"
            echo "Check logs with: $COMPOSE_CMD -f $CITE_COMPOSE_FILE logs $service_name"
            return 1
        fi

        if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps "$service_name" | grep -q "healthy"; then
            return 0
        fi

        if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps "$service_name" | grep -Eq "Exit|Restarting"; then
            echo -e "${RED}${label} exited or restarted before becoming healthy${NC}"
            return 2
        fi

        echo "Waiting for ${label}... (${elapsed}s elapsed)"
        sleep 5
    done
}

reset_results_dir

echo -e "${YELLOW}Starting WCS 2.0 CITE test environment...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true

export CITE_PROFILE="$PROFILE"
export HOST_UID="$(id -u)"
export HOST_GID="$(id -g)"

$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d postgres redis

echo -e "${YELLOW}Waiting for Postgres to be ready...${NC}"
if ! wait_for_service_health postgres "$POSTGRES_HEALTHCHECK_TIMEOUT" "Postgres"; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs postgres || true
    exit 1
fi

echo -e "${GREEN}Postgres is healthy${NC}"

echo -e "${YELLOW}Starting Honua Server to apply migrations...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d honua-server

if ! wait_for_service_health honua-server "$HONUA_HEALTHCHECK_TIMEOUT" "Honua Server (migrations)"; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server || true
    exit 1
fi

echo -e "${GREEN}Honua Server is healthy${NC}"

echo -e "${YELLOW}Stopping Honua Server to seed WCS data...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" stop honua-server

echo -e "${YELLOW}Seeding WCS CITE database...${NC}"
POSTGRES_CONTAINER=$($COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps -q postgres)
if [[ -z "$POSTGRES_CONTAINER" ]]; then
    echo -e "${RED}Postgres container not found${NC}"
    exit 1
fi

docker cp docker/cite/wcs20/seed.sql "$POSTGRES_CONTAINER":/tmp/cite-wcs20-seed.sql
docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cite_wcs -f /tmp/cite-wcs20-seed.sql >/dev/null
echo -e "${GREEN}WCS CITE database seeded${NC}"

echo -e "${YELLOW}Starting Honua Server and CITE TeamEngine...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d honua-server cite-engine

if ! wait_for_service_health honua-server "$HONUA_HEALTHCHECK_TIMEOUT" "Honua Server"; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server || true
    exit 1
fi

if ! wait_for_service_health cite-engine "$HONUA_HEALTHCHECK_TIMEOUT" "CITE TeamEngine"; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-engine || true
    exit 1
fi

HONUA_BASE_URL="http://localhost:${HONUA_CITE_WCS20_SERVER_PORT}"
CAPS_URL_HOST="${HONUA_BASE_URL}/ogc/services/cite/wcs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1"
DESCRIBE_URL_HOST="${HONUA_BASE_URL}/ogc/services/cite/wcs?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=coverage_101,coverage_102"
GETCOVERAGE_URL_HOST="${HONUA_BASE_URL}/ogc/services/cite/wcs?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=coverage_101&FORMAT=image/png"

echo -e "${YELLOW}Verifying WCS endpoints...${NC}"
if ! curl -sS --fail "$CAPS_URL_HOST" > "$CITE_RESULTS_DIR/capabilities.xml"; then
    echo -e "${RED}WCS GetCapabilities endpoint not accessible${NC}"
    exit 1
fi

if ! curl -sS --fail "$DESCRIBE_URL_HOST" > "$CITE_RESULTS_DIR/describe-coverage.xml"; then
    echo -e "${RED}WCS DescribeCoverage endpoint not accessible${NC}"
    exit 1
fi

if ! curl -sS --fail "$GETCOVERAGE_URL_HOST" > "$CITE_RESULTS_DIR/getcoverage.png"; then
    echo -e "${YELLOW}WCS GetCoverage preflight failed; continuing to CITE execution for full diagnostics${NC}"
fi

echo -e "${GREEN}WCS endpoints are accessible${NC}"

if [[ "$INTERACTIVE" == "true" ]]; then
    echo -e "${BLUE}Interactive mode enabled${NC}"
    echo "Services are running at:"
    echo "  Honua Server:        $HONUA_BASE_URL"
    echo "  WCS GetCapabilities: $CAPS_URL_HOST"
    echo "  CITE TeamEngine:     http://localhost:${HONUA_CITE_WCS20_TEAMENGINE_PORT}/teamengine"
    echo "  PostgreSQL:          localhost:${HONUA_CITE_WCS20_POSTGRES_PORT}"
    echo ""
    echo "Press Ctrl+C to stop all services"
    tail -f /dev/null
fi

echo -e "${YELLOW}Running WCS 2.0 CITE conformance tests (profile: $PROFILE)...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" rm -f -s cite-runner >/dev/null 2>&1 || true

TEST_START_TIME=$(date +%s)
CITE_RUNNER_EXIT_CODE=0
if [[ "$VERBOSE" == "true" ]]; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" --profile test up --force-recreate cite-runner || CITE_RUNNER_EXIT_CODE=$?
else
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" --profile test up --force-recreate cite-runner > /dev/null 2>&1 || CITE_RUNNER_EXIT_CODE=$?
fi

TEST_END_TIME=$(date +%s)
TEST_DURATION=$((TEST_END_TIME - TEST_START_TIME))
if [[ $TEST_DURATION -gt $CITE_TIMEOUT ]]; then
    echo -e "${RED}CITE tests exceeded ${CITE_TIMEOUT} seconds${NC}"
    CITE_RUNNER_EXIT_CODE=124
fi

echo -e "${YELLOW}Collecting container logs...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server > "$CITE_RESULTS_DIR/honua-server.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-engine > "$CITE_RESULTS_DIR/cite-teamengine.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-runner > "$CITE_RESULTS_DIR/cite-runner.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs postgres > "$CITE_RESULTS_DIR/postgres.log" 2>&1 || true

RESULTS_XML="$CITE_RESULTS_DIR/testng-results.xml"
if [[ -f "$RESULTS_XML" ]]; then
    TOTAL_TESTS=$(sed -n 's/.*total="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
    PASSED_TESTS=$(sed -n 's/.*passed="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
    FAILED_TESTS=$(sed -n 's/.*failed="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
    SKIPPED_TESTS=$(sed -n 's/.*skipped="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
    CANTTELL_TESTS=0
elif [[ -f "$CITE_RESULTS_DIR/cite-compliance-report.xml" ]]; then
    TOTAL_TESTS=$(sed -n 's/.*<testsRun>\([0-9]\+\)<\/testsRun>.*/\1/p' "$CITE_RESULTS_DIR/cite-compliance-report.xml" | head -n 1)
    PASSED_TESTS=$(sed -n 's/.*<testsPassed>\([0-9]\+\)<\/testsPassed>.*/\1/p' "$CITE_RESULTS_DIR/cite-compliance-report.xml" | head -n 1)
    FAILED_TESTS=$(sed -n 's/.*<testsFailed>\([0-9]\+\)<\/testsFailed>.*/\1/p' "$CITE_RESULTS_DIR/cite-compliance-report.xml" | head -n 1)
    SKIPPED_TESTS=$(sed -n 's/.*<testsSkipped>\([0-9]\+\)<\/testsSkipped>.*/\1/p' "$CITE_RESULTS_DIR/cite-compliance-report.xml" | head -n 1)
    CANTTELL_TESTS=$(sed -n 's/.*<testsCantTell>\([0-9]\+\)<\/testsCantTell>.*/\1/p' "$CITE_RESULTS_DIR/cite-compliance-report.xml" | head -n 1)
fi

TOTAL_TESTS=${TOTAL_TESTS:-0}
PASSED_TESTS=${PASSED_TESTS:-0}
FAILED_TESTS=${FAILED_TESTS:-0}
SKIPPED_TESTS=${SKIPPED_TESTS:-0}
CANTTELL_TESTS=${CANTTELL_TESTS:-0}

SUCCESS_RATE=0
if [[ $TOTAL_TESTS -gt 0 ]]; then
    SUCCESS_RATE=$((PASSED_TESTS * 100 / TOTAL_TESTS))
fi

cat > "$CITE_RESULTS_DIR/cite-wcs20-summary.md" << EOF_SUMMARY
# WCS 2.0 CITE Conformance Test Results

## Summary

- **Total Tests**: $TOTAL_TESTS
- **Passed**: $PASSED_TESTS
- **Failed**: $FAILED_TESTS
- **Skipped**: $SKIPPED_TESTS
- **CantTell**: $CANTTELL_TESTS
- **Success Rate**: ${SUCCESS_RATE}%
- **Runner Exit Code**: $CITE_RUNNER_EXIT_CODE
- **Execution Time**: ${TEST_DURATION}s

## Environment

- **Profile**: $PROFILE
- **CITE Suite**: ets-wcs20 1.22 / WCS 2.0.1
- **Capabilities URL**: $CAPS_URL_HOST
- **Seeded Coverages**: coverage_101, coverage_102
- **Data Source**: local PostGIS rasters from docker/cite/wcs20/seed.sql

## Expected Thin-Slice Limitations

The current WCS implementation is expected to fail official ETS coverage outside
the thin slice, including XML POST/SOAP bindings, GML coverage output,
processing, scaling, interpolation, range subsetting, broad CRS extension
coverage, and EO-WCS. See expected-known-failures.md.

## Artifacts

- capabilities.xml: captured WCS capabilities document
- describe-coverage.xml: captured DescribeCoverage response
- getcoverage.png: captured GetCoverage preflight response when available
- testng-results.xml: raw TeamEngine result summary when available
- cite-compliance-report.xml: normalized result summary
- expected-known-failures.md: current known limitation notes
- honua-server.log, cite-teamengine.log, cite-runner.log, postgres.log: service logs

EOF_SUMMARY

echo -e "${BLUE}WCS 2.0 CITE Results${NC}"
echo "===================="
echo "Profile: $PROFILE"
echo "Tests passed: $PASSED_TESTS/$TOTAL_TESTS"
echo "Tests failed: $FAILED_TESTS"
echo "Tests skipped: $SKIPPED_TESTS"
echo "Tests canttell: $CANTTELL_TESTS"
echo "Execution time: ${TEST_DURATION}s"
echo "Summary report: $CITE_RESULTS_DIR/cite-wcs20-summary.md"

if [[ "$VERBOSE" == "true" || $FAILED_TESTS -gt 0 || $CITE_RUNNER_EXIT_CODE -ne 0 ]]; then
    echo -e "\n${BLUE}Service Logs${NC}"
    echo "============"
    echo -e "${YELLOW}Honua Server logs:${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs --tail=80 honua-server || true

    echo -e "\n${YELLOW}CITE runner logs:${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-runner || true
fi

if [[ $TOTAL_TESTS -eq 0 ]]; then
    echo -e "${RED}CITE testing produced no executable tests.${NC}"
    exit 2
elif [[ $CITE_RUNNER_EXIT_CODE -eq 124 ]]; then
    echo -e "${RED}CITE testing timed out.${NC}"
    exit 2
elif [[ $FAILED_TESTS -gt 0 || $SKIPPED_TESTS -gt 0 || $CANTTELL_TESTS -gt 0 ]]; then
    echo -e "${YELLOW}CITE testing completed with expected thin-slice failures. Review results.${NC}"
    exit 1
else
    echo -e "${GREEN}CITE conformance testing completed successfully!${NC}"
    exit 0
fi
