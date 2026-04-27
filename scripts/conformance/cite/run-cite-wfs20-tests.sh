#!/bin/bash

# OGC WFS 2.0 CITE conformance testing script for Honua Server
# Runs the official OGC Compliance and Interoperability Testing & Evaluation (CITE) suite

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
CITE_COMPOSE_FILE="docker/cite/wfs20/compose.yml"
CITE_RESULTS_DIR="cite-wfs20-results"
CITE_TIMEOUT=1800  # 30 minutes timeout
HEALTHCHECK_TIMEOUT=300  # 5 minutes
HONUA_BASE_URL="http://localhost:8090"
HONUA_WFS_URL="${HONUA_BASE_URL}/wfs?service=WFS&version=2.0.0&request=GetCapabilities"
HONUA_CITE_WFS20_POSTGRES_PORT="${HONUA_CITE_WFS20_POSTGRES_PORT:-5433}"
export HONUA_CITE_WFS20_POSTGRES_PORT
PASSED_TESTS=0
FAILED_TESTS=0
TOTAL_TESTS=0

echo -e "${BLUE}🧪 OGC WFS 2.0 CITE Conformance Tests${NC}"
echo "========================================="

# Parse command line options
CLEANUP=true
INTERACTIVE=false
VERBOSE=false
PROFILE="basic"

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
            echo "  --profile PROF    Use specific CITE profile (default: basic)"
            echo "  --help, -h        Show this help"
            echo ""
            echo "Profiles:"
            echo "  basic            Core WFS 2.0 operations"
            echo "  transactional    Include WFS-T operations"
            echo "  full             All WFS 2.0 features"
            echo ""
            echo "Examples:"
            echo "  $0                        Run basic WFS 2.0 tests"
            echo "  $0 --profile full         Run comprehensive WFS 2.0 tests"
            echo "  $0 --interactive          Run and keep everything running for manual testing"
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

# Determine docker compose command
if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
elif command -v docker compose &> /dev/null; then
    COMPOSE_CMD="docker compose"
else
    echo -e "${RED}❌ Docker Compose not found. Please install Docker Compose${NC}"
    exit 1
fi

# Ensure compose file exists
if [[ ! -f "$CITE_COMPOSE_FILE" ]]; then
    echo -e "${RED}❌ CITE Docker Compose file not found: $CITE_COMPOSE_FILE${NC}"
    echo "Expected file with CITE Team Engine setup for real conformance testing."
    exit 1
fi

if [[ "${HONUA_CITE_SKIP_BUILD:-false}" == "true" ]]; then
    echo -e "${YELLOW}Skipping Honua Server Docker image build; using existing honua-server:latest${NC}"
else
    # Build Honua Server image
    echo -e "${YELLOW}Building Honua Server Docker image...${NC}"
    if ! docker build -t honua-server:latest .; then
        echo -e "${RED}❌ Failed to build Honua Server Docker image${NC}"
        exit 1
    fi

    echo -e "${GREEN}✅ Honua Server image built successfully${NC}"
fi

# Cleanup function
cleanup() {
    if [[ "$CLEANUP" == "true" && "$INTERACTIVE" == "false" ]]; then
        echo -e "\n${YELLOW}🧹 Cleaning up containers and networks...${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    fi
}

# Set trap for cleanup
trap cleanup EXIT

# Reset results directory even if a previous run left root-owned artifacts behind.
reset_results_dir() {
    mkdir -p "$CITE_RESULTS_DIR"

    docker run --rm \
        -v "$(pwd)/$CITE_RESULTS_DIR:/results" \
        alpine:3.22 \
        sh -c "rm -rf /results/* /results/.[!.]* /results/..?* 2>/dev/null || true; chown -R $(id -u):$(id -g) /results"
}

# Create results directory
reset_results_dir

# Start the CITE test environment
echo -e "${YELLOW}Starting WFS 2.0 CITE test environment...${NC}"
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true

# Export profile for docker-compose
export CITE_PROFILE="$PROFILE"
export HOST_UID="$(id -u)"
export HOST_GID="$(id -g)"

# Start all services
if [[ "$VERBOSE" == "true" ]]; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d
else
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up -d > /dev/null 2>&1
fi

# Wait for services to be healthy
echo -e "${YELLOW}Waiting for services to be ready...${NC}"

# Wait for PostgreSQL
echo "Waiting for PostgreSQL..."
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}❌ Timeout waiting for PostgreSQL${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs postgres
        exit 1
    fi

    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps postgres | grep -q "healthy"; then
        break
    fi

    echo "PostgreSQL starting... (${elapsed}s elapsed)"
    sleep 5
done

echo -e "${GREEN}✅ PostgreSQL is ready${NC}"

# Wait for Honua Server
echo "Waiting for Honua Server..."
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}❌ Timeout waiting for Honua Server${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server
        exit 1
    fi

    if curl -fsS "$HONUA_WFS_URL" > /dev/null; then
        break
    fi

    echo "Honua Server starting... (${elapsed}s elapsed)"
    sleep 10
done

echo -e "${GREEN}✅ Honua Server is ready${NC}"

# Wait for CITE Team Engine
echo "Waiting for CITE Team Engine..."
start_time=$(date +%s)
while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))

    if [[ $elapsed -gt $HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}❌ Timeout waiting for CITE Team Engine${NC}"
        $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-teamengine
        exit 1
    fi

    if $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" ps cite-teamengine | grep -q "healthy"; then
        break
    fi

    echo "CITE Team Engine starting... (${elapsed}s elapsed)"
    sleep 15
done

echo -e "${GREEN}✅ CITE Team Engine is ready${NC}"

if [[ "$INTERACTIVE" == "true" ]]; then
    echo -e "${BLUE}🔗 Interactive mode enabled${NC}"
    echo "Services are running at:"
    echo "  Honua Server:        $HONUA_BASE_URL"
    echo "  WFS GetCapabilities: $HONUA_WFS_URL"
    echo "  CITE Team Engine:    http://localhost:8081/teamengine"
    echo "  PostgreSQL:          localhost:${HONUA_CITE_WFS20_POSTGRES_PORT}"
    echo ""
    echo "Manual WFS 2.0 testing commands:"
    echo "  GetCapabilities:     curl '$HONUA_WFS_URL'"
    echo "  DescribeFeatureType: curl '${HONUA_BASE_URL}/wfs?service=WFS&version=2.0.0&request=DescribeFeatureType'"
    echo "  GetFeature:          curl '${HONUA_BASE_URL}/wfs?service=WFS&version=2.0.0&request=GetFeature'"
    echo ""
    echo "CITE Team Engine web interface: http://localhost:8081/teamengine"
    echo ""
    echo "Press Ctrl+C to stop all services"

    # Wait indefinitely
    tail -f /dev/null
fi

# Execute CITE tests via the cite-runner service
echo -e "${YELLOW}Executing OGC CITE WFS 2.0 conformance tests...${NC}"
echo "Profile: $PROFILE"
echo "This may take 10-30 minutes depending on the test profile..."

# Start the CITE test runner
TEST_START_TIME=$(date +%s)
CITE_RUNNER_EXIT_CODE=0

# Run the CITE test runner container
if [[ "$VERBOSE" == "true" ]]; then
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up cite-runner || CITE_RUNNER_EXIT_CODE=$?
else
    $COMPOSE_CMD -f "$CITE_COMPOSE_FILE" up cite-runner > /dev/null 2>&1 || CITE_RUNNER_EXIT_CODE=$?
fi

TEST_END_TIME=$(date +%s)
TEST_DURATION=$((TEST_END_TIME - TEST_START_TIME))

echo -e "${GREEN}✅ CITE test execution completed${NC}"
echo "Execution time: ${TEST_DURATION}s"
echo "cite-runner exit code: ${CITE_RUNNER_EXIT_CODE}"

# Collect test results from the cite-runner container
echo -e "${YELLOW}Collecting CITE test results...${NC}"

# Collect container logs as additional results
echo "Collecting container logs..."
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs honua-server > "$CITE_RESULTS_DIR/honua-server.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-teamengine > "$CITE_RESULTS_DIR/cite-teamengine.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs cite-runner > "$CITE_RESULTS_DIR/cite-runner.log" 2>&1 || true
$COMPOSE_CMD -f "$CITE_COMPOSE_FILE" logs postgres > "$CITE_RESULTS_DIR/postgres.log" 2>&1 || true

# Verify we have test results and parse them
echo -e "${YELLOW}Analyzing CITE test results...${NC}"

# Check if we have an XML compliance report
COMPLIANCE_STATUS="UNKNOWN"
if [[ -f "$CITE_RESULTS_DIR/cite-compliance-report.xml" ]]; then
    echo "Found CITE compliance report"

    # Parse the XML report
    if command -v xmlstarlet &> /dev/null; then
        PASSED_TESTS=$(xmlstarlet sel -t -v "//testsPassed" "$CITE_RESULTS_DIR/cite-compliance-report.xml" 2>/dev/null || echo "0")
        TOTAL_TESTS=$(xmlstarlet sel -t -v "//testsRun" "$CITE_RESULTS_DIR/cite-compliance-report.xml" 2>/dev/null || echo "0")
        COMPLIANCE_STATUS=$(xmlstarlet sel -t -v "//status" "$CITE_RESULTS_DIR/cite-compliance-report.xml" 2>/dev/null || echo "UNKNOWN")
    else
        # Fallback to grep if xmlstarlet is not available
        PASSED_TESTS=$(grep -o "<testsPassed>[0-9]*</testsPassed>" "$CITE_RESULTS_DIR/cite-compliance-report.xml" 2>/dev/null | grep -o "[0-9]*" || echo "0")
        TOTAL_TESTS=$(grep -o "<testsRun>[0-9]*</testsRun>" "$CITE_RESULTS_DIR/cite-compliance-report.xml" 2>/dev/null | grep -o "[0-9]*" || echo "0")
        COMPLIANCE_STATUS=$(grep -o "<status>[^<]*</status>" "$CITE_RESULTS_DIR/cite-compliance-report.xml" 2>/dev/null | sed 's/<[^>]*>//g' || echo "UNKNOWN")
    fi

    FAILED_TESTS=$((TOTAL_TESTS - PASSED_TESTS))
else
    echo "No CITE compliance report found - checking cite-runner logs..."

    # Try to extract results from logs
    if [[ -f "$CITE_RESULTS_DIR/cite-runner.log" ]]; then
        PASSED_TESTS=$(grep "Tests Passed:" "$CITE_RESULTS_DIR/cite-runner.log" 2>/dev/null | tail -1 | grep -o "[0-9]*/[0-9]*" | cut -d'/' -f1 || echo "0")
        TOTAL_TESTS=$(grep "Tests Passed:" "$CITE_RESULTS_DIR/cite-runner.log" 2>/dev/null | tail -1 | grep -o "[0-9]*/[0-9]*" | cut -d'/' -f2 || echo "0")
        FAILED_TESTS=$((TOTAL_TESTS - PASSED_TESTS))

        if grep -q "FULL WFS 2.0 CITE COMPLIANCE ACHIEVED" "$CITE_RESULTS_DIR/cite-runner.log" 2>/dev/null; then
            COMPLIANCE_STATUS="COMPLIANT"
        elif grep -q "Partial WFS 2.0" "$CITE_RESULTS_DIR/cite-runner.log" 2>/dev/null; then
            COMPLIANCE_STATUS="PARTIAL"
        elif [[ $TOTAL_TESTS -gt 0 ]]; then
            COMPLIANCE_STATUS="NON_COMPLIANT"
        fi
    fi
fi

# Generate final summary report
echo -e "${BLUE}📊 Generating final CITE test summary...${NC}"

cat > "$CITE_RESULTS_DIR/cite-summary.md" << EOF
# OGC WFS 2.0 CITE Conformance Test Results

**Execution Date**: $(date)
**Profile**: $PROFILE
**Execution Time**: ${TEST_DURATION}s
**Honua Server Version**: $(git describe --tags --always 2>/dev/null || echo "unknown")

## Test Summary

- **Total Tests**: $TOTAL_TESTS
- **Passed**: $PASSED_TESTS
- **Failed**: $FAILED_TESTS
- **Success Rate**: $(( TOTAL_TESTS > 0 ? (PASSED_TESTS * 100) / TOTAL_TESTS : 0 ))%
- **Compliance Status**: $COMPLIANCE_STATUS

## Test Environment

- **Honua Server**: $HONUA_BASE_URL
- **WFS Endpoint**: ${HONUA_BASE_URL}/wfs
- **CITE Team Engine**: http://localhost:8081/teamengine
- **Database**: PostgreSQL with PostGIS + CITE test data

## CITE Team Engine Results

$(
if [[ "$COMPLIANCE_STATUS" == "COMPLIANT" ]]; then
    echo "🎉 **FULL WFS 2.0 CITE COMPLIANCE ACHIEVED!**"
    echo ""
    echo "The Honua Server WFS 2.0 implementation passes all required CITE conformance tests."
elif [[ "$COMPLIANCE_STATUS" == "PARTIAL" ]]; then
    echo "⚠️ **Partial WFS 2.0 CITE Compliance**"
    echo ""
    echo "The implementation passes core tests but some advanced features may need work."
elif [[ "$COMPLIANCE_STATUS" == "NON_COMPLIANT" ]]; then
    echo "❌ **WFS 2.0 CITE Compliance Tests Failed**"
    echo ""
    echo "The implementation has issues that prevent CITE compliance."
else
    echo "⚙️ **CITE Test Execution Status Unknown**"
    echo ""
    echo "Test execution completed but results could not be parsed."
fi
)

## Test Artifacts

### Generated Files
$(find "$CITE_RESULTS_DIR" -type f 2>/dev/null | sort | sed 's/^/- /' || echo "- No result files found")

### Container Logs
- honua-server.log: Honua Server application logs
- cite-teamengine.log: CITE Team Engine execution logs
- cite-runner.log: Test execution logs
- postgres.log: Database logs

## Next Steps

$(
if [[ "$COMPLIANCE_STATUS" == "COMPLIANT" ]]; then
    echo "✅ **WFS 2.0 Implementation Complete**"
    echo ""
    echo "- Document compliance achievement"
    echo "- Add to production deployment"
    echo "- Consider WFS 2.0 extensions or optimizations"
elif [[ "$COMPLIANCE_STATUS" == "PARTIAL" ]]; then
    echo "🔧 **Address Failing Tests**"
    echo ""
    echo "1. Review detailed test logs for specific failures"
    echo "2. Fix implementation gaps identified by CITE"
    echo "3. Re-run tests to validate fixes"
    echo "4. Consider iterative improvement approach"
else
    echo "🚧 **Implementation Work Required**"
    echo ""
    echo "1. **Analyze test failures** - review CITE logs for specific issues"
    echo "2. **Fix core compliance issues** - address fundamental WFS 2.0 requirements"
    echo "3. **Integrate with layer catalog** - populate actual feature types"
    echo "4. **Implement GML 3.2 serialization** - proper feature formatting"
    echo "5. **Connect FES 2.0 parser** - enable filtering capabilities"
    echo "6. **Re-run CITE tests** - validate fixes iteratively"
fi
)

---
Generated by: WFS 2.0 CITE Conformance Test Suite
Implementation: Honua Server (GitHub #376)
Test Engine: OGC CITE Team Engine with ets-wfs20
EOF

echo -e "${GREEN}✅ Final summary report saved to: $CITE_RESULTS_DIR/cite-summary.md${NC}"

# Display results summary
echo -e "\n${BLUE}🎯 WFS 2.0 CITE Conformance Results${NC}"
echo "========================================"
echo "Profile: $PROFILE"
echo "Tests Passed: $PASSED_TESTS/$TOTAL_TESTS"
echo "Compliance Status: $COMPLIANCE_STATUS"
echo "Execution Time: ${TEST_DURATION}s"

# Determine exit code
if [[ "$COMPLIANCE_STATUS" == "COMPLIANT" ]]; then
    echo -e "\n${GREEN}🎉 WFS 2.0 CITE COMPLIANCE ACHIEVED!${NC}"
    exit 0
elif [[ "$COMPLIANCE_STATUS" == "PARTIAL" && $PASSED_TESTS -gt 0 ]]; then
    echo -e "\n${YELLOW}⚠️ Partial WFS 2.0 compliance - some tests passed${NC}"
    echo -e "${RED}❌ Partial compliance is not sufficient for the CITE conformance gate${NC}"
    exit 1
elif [[ $TOTAL_TESTS -eq 0 ]]; then
    echo -e "\n${RED}❌ CITE tests did not yield any authoritative results${NC}"
    exit 1
elif [[ "$COMPLIANCE_STATUS" == "UNKNOWN" ]]; then
    echo -e "\n${RED}❌ CITE test execution status is unknown${NC}"
    echo -e "${BLUE}📋 See detailed logs in $CITE_RESULTS_DIR/ for specific issues${NC}"
    exit 1
elif [[ $CITE_RUNNER_EXIT_CODE -ne 0 ]]; then
    echo -e "\n${RED}❌ cite-runner exited with status ${CITE_RUNNER_EXIT_CODE}${NC}"
    echo -e "${BLUE}📋 See detailed logs in $CITE_RESULTS_DIR/ for specific issues${NC}"
    exit 1
else
    echo -e "\n${RED}❌ WFS 2.0 CITE compliance tests failed${NC}"
    echo -e "${BLUE}📋 See detailed logs in $CITE_RESULTS_DIR/ for specific issues${NC}"
    exit 1
fi
