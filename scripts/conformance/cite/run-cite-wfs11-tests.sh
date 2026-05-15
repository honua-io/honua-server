#!/bin/bash

# OGC WFS 1.1 CITE conformance testing script for Honua Server.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

CITE_COMPOSE_FILE="docker/cite/wfs11/compose.yml"
CITE_RESULTS_DIR="cite-wfs11-results"
CITE_TIMEOUT=1800
HEALTHCHECK_TIMEOUT=300
HONUA_CITE_WFS11_SERVER_PORT="${HONUA_CITE_WFS11_SERVER_PORT:-8097}"
HONUA_BASE_URL="http://localhost:${HONUA_CITE_WFS11_SERVER_PORT}"
HONUA_WFS_URL="${HONUA_BASE_URL}/wfs?service=WFS&version=1.1.0&request=GetCapabilities"
HONUA_CITE_WFS11_POSTGRES_PORT="${HONUA_CITE_WFS11_POSTGRES_PORT:-5447}"
export HONUA_CITE_WFS11_SERVER_PORT
export HONUA_CITE_WFS11_POSTGRES_PORT

echo -e "${BLUE}OGC WFS 1.1 CITE Conformance Tests${NC}"
echo "===================================="

CLEANUP=true
INTERACTIVE=false
VERBOSE=false
PROFILE="basic"
SKIP_BUILD="${HONUA_CITE_SKIP_BUILD:-false}"

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
            echo "  --interactive     Run in interactive mode"
            echo "  --verbose         Enable verbose logging"
            echo "  --profile PROF    Use CITE profile: basic/default (default: basic)"
            echo "  --skip-build      Reuse existing honua-server:latest image"
            echo "  --help, -h        Show this help"
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

if ! command -v docker &> /dev/null; then
    echo -e "${RED}Docker not found. Please install Docker${NC}"
    exit 1
fi

if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
elif docker compose version &> /dev/null; then
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

reset_results_dir

echo -e "${YELLOW}Starting WFS 1.1 CITE test environment...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true

export CITE_PROFILE="$PROFILE"
export HOST_UID="$(id -u)"
export HOST_GID="$(id -g)"

if [[ "$VERBOSE" == "true" ]]; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d
else
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d > /dev/null 2>&1
fi

echo -e "${YELLOW}Waiting for services to be ready...${NC}"

echo "Waiting for PostgreSQL..."
start_time=$(date +%s)
while true; do
    elapsed=$(($(date +%s) - start_time))
    if [[ $elapsed -gt $HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Timeout waiting for PostgreSQL${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs postgres
        exit 1
    fi
    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps postgres | grep -q "healthy"; then
        break
    fi
    echo "PostgreSQL starting... (${elapsed}s elapsed)"
    sleep 5
done
echo -e "${GREEN}PostgreSQL is ready${NC}"

echo "Waiting for Honua Server..."
start_time=$(date +%s)
while true; do
    elapsed=$(($(date +%s) - start_time))
    if [[ $elapsed -gt $HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Timeout waiting for Honua Server${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server
        exit 1
    fi
    if curl -fsS "$HONUA_WFS_URL" > /dev/null; then
        break
    fi
    echo "Honua Server starting... (${elapsed}s elapsed)"
    sleep 10
done
echo -e "${GREEN}Honua Server is ready${NC}"

echo "Waiting for CITE Team Engine..."
start_time=$(date +%s)
while true; do
    elapsed=$(($(date +%s) - start_time))
    if [[ $elapsed -gt $HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Timeout waiting for CITE Team Engine${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-teamengine
        exit 1
    fi
    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps cite-teamengine | grep -q "healthy"; then
        break
    fi
    echo "CITE Team Engine starting... (${elapsed}s elapsed)"
    sleep 15
done
echo -e "${GREEN}CITE Team Engine is ready${NC}"

if [[ "$INTERACTIVE" == "true" ]]; then
    echo -e "${BLUE}Interactive mode enabled${NC}"
    echo "Honua Server:        $HONUA_BASE_URL"
    echo "WFS GetCapabilities: $HONUA_WFS_URL"
    echo "CITE Team Engine:    http://localhost:8089/teamengine"
    tail -f /dev/null
fi

echo -e "${YELLOW}Running WFS 1.1 CITE tests...${NC}"
set +e
timeout "$CITE_TIMEOUT" $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up --abort-on-container-exit --exit-code-from cite-runner cite-runner
TEST_EXIT_CODE=$?
set -e

echo -e "${YELLOW}Collecting logs...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-runner > "$CITE_RESULTS_DIR/cite-runner.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server > "$CITE_RESULTS_DIR/honua-server.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs postgres > "$CITE_RESULTS_DIR/postgres.log" 2>&1 || true

curl -fsS "$HONUA_WFS_URL" -o "$CITE_RESULTS_DIR/capabilities.xml" || true

if [[ $TEST_EXIT_CODE -eq 124 ]]; then
    echo -e "${RED}WFS 1.1 CITE tests timed out after ${CITE_TIMEOUT}s${NC}"
    exit 1
fi

if [[ $TEST_EXIT_CODE -eq 0 ]]; then
    echo -e "${GREEN}WFS 1.1 CITE tests completed successfully${NC}"
else
    echo -e "${RED}WFS 1.1 CITE tests failed with exit code $TEST_EXIT_CODE${NC}"
fi

exit "$TEST_EXIT_CODE"
