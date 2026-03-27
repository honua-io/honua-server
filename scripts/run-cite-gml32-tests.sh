#!/bin/bash

# GML 3.2 CITE conformance testing script for Honua Server.
# Validates GML document output against the OGC GML 3.2 specification.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

CITE_COMPOSE_FILE="docker/cite-gml32-compose.yml"
CITE_RESULTS_DIR="cite-gml32-results"
CITE_RESULTS_CONTAINER_DIR="/root/te_base/users/cite/logs"
CITE_TIMEOUT=1800
HONUA_HEALTHCHECK_TIMEOUT=300
POSTGRES_HEALTHCHECK_TIMEOUT=120
PASSED_TESTS=0
FAILED_TESTS=0
SKIPPED_TESTS=0
CANTTELL_TESTS=0
TOTAL_TESTS=0

CLEANUP=true
INTERACTIVE=false
VERBOSE=false
PROFILE="default"

echo -e "${BLUE}GML 3.2 CITE Conformance Tests${NC}"
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
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --no-cleanup      Don't cleanup containers after tests"
            echo "  --interactive     Run in interactive mode (keep containers running)"
            echo "  --verbose         Enable verbose logging"
            echo "  --profile PROF    Accepted for CLI consistency (GML 3.2 has a single validation pass)"
            echo "  --help, -h        Show this help"
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

echo -e "${YELLOW}Checking prerequisites...${NC}"

if ! command -v docker &> /dev/null; then
    echo -e "${RED}Docker not found. Please install Docker${NC}"
    exit 1
fi

if ! command -v docker-compose &> /dev/null && ! command -v docker compose &> /dev/null; then
    echo -e "${RED}Docker Compose not found. Please install Docker Compose${NC}"
    exit 1
fi

if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
else
    COMPOSE_CMD="docker compose"
fi

echo -e "${YELLOW}Building Honua Server Docker image...${NC}"
if ! docker build -t honua-server:latest .; then
    echo -e "${RED}Failed to build Honua Server Docker image${NC}"
    exit 1
fi

echo -e "${GREEN}Honua Server image built successfully${NC}"

cleanup() {
    if [[ "$CLEANUP" == "true" && "$INTERACTIVE" == "false" ]]; then
        echo -e "\n${YELLOW}Cleaning up containers and networks...${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    fi
}

trap cleanup EXIT

echo -e "${YELLOW}Starting CITE GML 3.2 test environment...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d postgres

echo -e "${YELLOW}Waiting for Postgres to be ready...${NC}"
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $POSTGRES_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Timeout waiting for Postgres to become healthy${NC}"
        echo "Check logs with: $COMPOSE_CMD -f $CITE_COMPOSE_FILE logs postgres"
        exit 1
    fi

    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps postgres | grep -q "healthy"; then
        break
    fi

    echo "Waiting for Postgres... (${elapsed}s elapsed)"
    sleep 5
done

echo -e "${GREEN}Postgres is healthy${NC}"

$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d honua-server

echo -e "${YELLOW}Waiting for Honua Server to be ready (migrations)...${NC}"
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $HONUA_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Timeout waiting for Honua Server to become healthy${NC}"
        echo "Check logs with: $COMPOSE_CMD -f $CITE_COMPOSE_FILE logs honua-server"
        exit 1
    fi

    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps honua-server | grep -q "healthy"; then
        break
    fi

    echo "Waiting for Honua Server... (${elapsed}s elapsed)"
    sleep 5
done

echo -e "${GREEN}Honua Server is healthy${NC}"

echo -e "${YELLOW}Stopping Honua Server to seed data...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" stop honua-server

echo -e "${YELLOW}Seeding CITE database...${NC}"
POSTGRES_CONTAINER=$($COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps -q postgres)
if [[ -z "$POSTGRES_CONTAINER" ]]; then
    echo -e "${RED}Postgres container not found${NC}"
    exit 1
fi

docker cp docker/cite-mapserver-seed.sql "$POSTGRES_CONTAINER":/tmp/cite-mapserver-seed.sql
docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cite_gml -f /tmp/cite-mapserver-seed.sql >/dev/null
echo -e "${GREEN}CITE database seeded${NC}"

$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d honua-server cite-engine

echo -e "${YELLOW}Waiting for Honua Server to be ready...${NC}"
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $HONUA_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Timeout waiting for Honua Server to become healthy${NC}"
        echo "Check logs with: $COMPOSE_CMD -f $CITE_COMPOSE_FILE logs honua-server"
        exit 1
    fi

    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps honua-server | grep -q "healthy"; then
        break
    fi

    echo "Waiting for Honua Server... (${elapsed}s elapsed)"
    sleep 5
done

echo -e "${GREEN}Honua Server is healthy${NC}"

GML_URL_HOST="http://localhost:8080/ogc/features/collections/cite:BasicPolygons/items"

echo -e "${YELLOW}Verifying GML endpoint...${NC}"
if ! curl -s -f -H 'Accept: application/gml+xml; version=3.2' "$GML_URL_HOST" > /dev/null; then
    echo -e "${YELLOW}GML content negotiation preflight failed; continuing to CITE execution for full diagnostics${NC}"
else
    echo -e "${GREEN}GML endpoint is accessible${NC}"
fi

if [[ "$INTERACTIVE" == "true" ]]; then
    echo -e "${BLUE}Interactive mode enabled${NC}"
    echo "Services are running at:"
    echo "  Honua Server:     http://localhost:8080"
    echo "  CITE Team Engine: http://localhost:8086/teamengine"
    echo "  PostgreSQL:       localhost:5438"
    echo ""
    echo "Press Ctrl+C to stop all services"
    tail -f /dev/null
fi

mkdir -p "$CITE_RESULTS_DIR"
rm -rf "$CITE_RESULTS_DIR"/*

echo -e "${YELLOW}Capturing GML output...${NC}"
if ! curl -s -f -H 'Accept: application/gml+xml; version=3.2' "$GML_URL_HOST" > "$CITE_RESULTS_DIR/output.gml"; then
    echo -e "${YELLOW}Failed to capture GML output; continuing to CITE execution${NC}"
fi

echo -e "${YELLOW}Running GML 3.2 CITE conformance tests (profile: $PROFILE)...${NC}"
echo -e "${YELLOW}Note: GML 3.2 format validation has a single pass; --profile is accepted for CLI consistency.${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" rm -f -s cite-runner >/dev/null 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" --profile test up --force-recreate cite-runner

echo -e "${YELLOW}Waiting for CITE tests to complete...${NC}"
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $CITE_TIMEOUT ]]; then
        echo -e "${RED}CITE tests timed out after ${CITE_TIMEOUT} seconds${NC}"
        break
    fi

    if ! $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps cite-runner | grep -q "Up"; then
        break
    fi

    if [[ $((elapsed % 30)) -eq 0 ]]; then
        echo "CITE tests running... (${elapsed}s elapsed)"
    fi

    sleep 5
done

echo -e "${YELLOW}Extracting CITE test results...${NC}"
CITE_RUNNER_CONTAINER=$($COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps -aq cite-runner 2>/dev/null | tail -n 1 || echo "")
if [[ -n "$CITE_RUNNER_CONTAINER" ]]; then
    docker cp "$CITE_RUNNER_CONTAINER":"$CITE_RESULTS_CONTAINER_DIR"/. "$CITE_RESULTS_DIR/" 2>/dev/null || true
fi

echo -e "\n${BLUE}CITE Test Results Analysis${NC}"
echo "==============================="

RESULTS_FOUND=false
if [[ -d "$CITE_RESULTS_DIR" && $(ls -A "$CITE_RESULTS_DIR" 2>/dev/null) ]]; then
    RESULTS_FOUND=true
    echo "Results saved to: $CITE_RESULTS_DIR/"

    RESULTS_XML=$(find "$CITE_RESULTS_DIR" -type f -name "testng-results.xml" | sort | tail -n 1)
    if [[ -n "$RESULTS_XML" && -f "$RESULTS_XML" ]]; then
        echo -e "${GREEN}Test result files found${NC}"
        TOTAL_TESTS=$(sed -n 's/.*total="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        PASSED_TESTS=$(sed -n 's/.*passed="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        FAILED_TESTS=$(sed -n 's/.*failed="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        SKIPPED_TESTS=$(sed -n 's/.*skipped="\([0-9]\+\)".*/\1/p' "$RESULTS_XML" | head -n 1)
        CANTTELL_TESTS=0
    else
        SESSION_DIR=$(find "$CITE_RESULTS_DIR" -maxdepth 1 -type d -name "cite-gml32-session-*" | sort | tail -n 1)
        RESULT_CODE_LINES=""
        if [[ -n "$SESSION_DIR" && -d "$SESSION_DIR" ]]; then
            RESULT_CODE_LINES=$(grep -Rho 'endtest result="[0-9]\+"' "$SESSION_DIR" 2>/dev/null || true)
        fi

        if [[ -n "$RESULT_CODE_LINES" ]]; then
            TOTAL_TESTS=$(printf '%s\n' "$RESULT_CODE_LINES" | wc -l | tr -d ' ')
            PASSED_TESTS=$(printf '%s\n' "$RESULT_CODE_LINES" | grep -c 'result="1"' || true)
            SKIPPED_TESTS=$(printf '%s\n' "$RESULT_CODE_LINES" | grep -c 'result="3"' || true)
            CANTTELL_TESTS=$(printf '%s\n' "$RESULT_CODE_LINES" | grep -c 'result="4"' || true)
            FAILED_TESTS=$((TOTAL_TESTS - PASSED_TESTS - SKIPPED_TESTS - CANTTELL_TESTS))
        fi
    fi

    TOTAL_TESTS=${TOTAL_TESTS:-0}
    PASSED_TESTS=${PASSED_TESTS:-0}
    FAILED_TESTS=${FAILED_TESTS:-0}
    SKIPPED_TESTS=${SKIPPED_TESTS:-0}
    CANTTELL_TESTS=${CANTTELL_TESTS:-0}

    echo "Total tests executed: $TOTAL_TESTS"
    echo "Tests passed: $PASSED_TESTS"
    echo "Tests failed: $FAILED_TESTS"
    echo "Tests skipped: $SKIPPED_TESTS"
    echo "Tests canttell: $CANTTELL_TESTS"
else
    echo -e "${RED}No test results found${NC}"
fi

if [[ "$VERBOSE" == "true" || $FAILED_TESTS -gt 0 ]]; then
    echo -e "\n${BLUE}Service Logs${NC}"
    echo "==============="
    echo -e "${YELLOW}Honua Server logs:${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs --tail=50 honua-server || true

    echo -e "\n${YELLOW}CITE Runner logs:${NC}"
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-runner || true
fi

SUCCESS_RATE=0
if [[ ${TOTAL_TESTS:-0} -gt 0 ]]; then
    SUCCESS_RATE=$((PASSED_TESTS * 100 / TOTAL_TESTS))
fi

cat > "$CITE_RESULTS_DIR/cite-gml32-summary.md" << EOF_SUMMARY
# GML 3.2 CITE Conformance Test Results

## Summary

- **Total Tests**: $TOTAL_TESTS
- **Passed**: $PASSED_TESTS
- **Failed**: $FAILED_TESTS
- **Skipped**: $SKIPPED_TESTS
- **CantTell**: $CANTTELL_TESTS
- **Success Rate**: ${SUCCESS_RATE}%

## Environment

- **Profile**: $PROFILE (GML 3.2 has a single validation pass)
- **CITE Suite**: ets-gml32
- **GML Source**: OGC API Features content negotiation (cite:BasicPolygons)

## Artifacts

- output.gml: captured GML document
- testng-results.xml: raw TeamEngine result summary (when available)

EOF_SUMMARY

echo -e "${GREEN}Summary report saved to: $CITE_RESULTS_DIR/cite-gml32-summary.md${NC}"

if [[ "$RESULTS_FOUND" != "true" ]]; then
    echo -e "${RED}CITE testing failed to execute properly.${NC}"
    exit 2
elif [[ $FAILED_TESTS -gt 0 ]]; then
    echo -e "${YELLOW}CITE testing completed with failures. Review results.${NC}"
    exit 1
elif [[ $TOTAL_TESTS -eq 0 ]]; then
    echo -e "${RED}CITE testing produced no executable tests.${NC}"
    exit 2
else
    echo -e "${GREEN}CITE conformance testing completed successfully!${NC}"
    exit 0
fi
