#!/bin/sh

# OGC CITE Team Engine test execution script
# Runs actual WFS 2.0 conformance tests against Honua Server

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}🧪 Executing OGC CITE WFS 2.0 Conformance Tests${NC}"
echo "=============================================="

# Configuration from environment
WFS_ENDPOINT=${WFS_ENDPOINT:-http://honua-server:8080/wfs}
CITE_ENGINE=${CITE_ENGINE:-http://cite-teamengine:8080/teamengine}
TEST_PROFILE=${TEST_PROFILE:-basic}
RESULTS_DIR=${RESULTS_DIR:-/results}

echo "WFS Endpoint: $WFS_ENDPOINT"
echo "CITE Engine: $CITE_ENGINE"
echo "Test Profile: $TEST_PROFILE"
echo "Results Directory: $RESULTS_DIR"
echo ""

# Wait for services to be ready
echo -e "${YELLOW}Waiting for services to be ready...${NC}"

# Wait for Honua Server
echo "Checking Honua Server availability..."
for i in $(seq 1 30); do
    if curl -s -f "$WFS_ENDPOINT?service=WFS&version=2.0.0&request=GetCapabilities" > /dev/null; then
        echo -e "${GREEN}✅ Honua Server is responding${NC}"
        break
    fi

    if [ $i -eq 30 ]; then
        echo -e "${RED}❌ Timeout waiting for Honua Server${NC}"
        exit 1
    fi

    echo "Attempt $i/30: Waiting for Honua Server..."
    sleep 5
done

# Wait for CITE Team Engine
echo "Checking CITE Team Engine availability..."
for i in $(seq 1 30); do
    if curl -s -f "$CITE_ENGINE/" > /dev/null; then
        echo -e "${GREEN}✅ CITE Team Engine is responding${NC}"
        break
    fi

    if [ $i -eq 30 ]; then
        echo -e "${RED}❌ Timeout waiting for CITE Team Engine${NC}"
        exit 1
    fi

    echo "Attempt $i/30: Waiting for CITE Team Engine..."
    sleep 10
done

# Create results directory
mkdir -p "$RESULTS_DIR"

# Determine test suite based on profile
case "$TEST_PROFILE" in
    "basic")
        TEST_SUITE="ets-wfs20-basic"
        echo "Running basic WFS 2.0 conformance tests (Core + Transactional)"
        ;;
    "transactional")
        TEST_SUITE="ets-wfs20-transaction"
        echo "Running WFS-T (transactional) conformance tests"
        ;;
    "full")
        TEST_SUITE="ets-wfs20"
        echo "Running full WFS 2.0 conformance test suite"
        ;;
    *)
        echo -e "${RED}❌ Unknown test profile: $TEST_PROFILE${NC}"
        echo "Valid profiles: basic, transactional, full"
        exit 1
        ;;
esac

echo -e "${YELLOW}Starting CITE test execution...${NC}"
echo "Test Suite: $TEST_SUITE"
echo ""

# Create test session and execute tests using CITE REST API
SESSION_ID=$(date +%s)
TEST_RUN_ID="wfs20-test-$SESSION_ID"

echo "Creating test session: $TEST_RUN_ID"

# Prepare test input parameters
cat > "$RESULTS_DIR/test-params.xml" << EOF
<testRunArgs xmlns="http://www.occamlab.com/te/engine">
    <suite>$TEST_SUITE</suite>
    <profile>$TEST_PROFILE</profile>
    <webServiceUrl>$WFS_ENDPOINT</webServiceUrl>
    <capabilities>$WFS_ENDPOINT?service=WFS&amp;version=2.0.0&amp;request=GetCapabilities</capabilities>
</testRunArgs>
EOF

# Execute the test suite via CITE Team Engine REST API
echo -e "${YELLOW}Executing WFS 2.0 conformance tests...${NC}"
echo "This may take 10-30 minutes depending on the test profile..."

# Start test execution
TEST_EXECUTION_START=$(date +%s)

# Use CITE Team Engine REST API to execute tests
curl -fsS -X POST \
    -H "Content-Type: application/xml" \
    -d @"$RESULTS_DIR/test-params.xml" \
    "$CITE_ENGINE/rest/sessions/$SESSION_ID/suites/$TEST_SUITE/run" \
    > "$RESULTS_DIR/test-execution.xml" || {

    echo -e "${RED}❌ Failed to start CITE test execution${NC}"
    echo "CITE Engine Response:"
    cat "$RESULTS_DIR/test-execution.xml" 2>/dev/null || echo "No response received"
    exit 1
}

# If we get here, CITE tests executed successfully
TEST_EXECUTION_END=$(date +%s)
EXECUTION_TIME=$((TEST_EXECUTION_END - TEST_EXECUTION_START))

echo -e "${GREEN}✅ CITE test execution completed${NC}"
echo "Execution time: ${EXECUTION_TIME}s"

# Retrieve test results
echo -e "${YELLOW}Collecting CITE test results...${NC}"

# Get test results via REST API
curl -fsS "$CITE_ENGINE/rest/sessions/$SESSION_ID/results" > "$RESULTS_DIR/cite-results.xml" || {
    echo -e "${YELLOW}⚠️ Could not retrieve full CITE results via REST API${NC}"
}

# Get detailed test log
curl -fsS "$CITE_ENGINE/rest/sessions/$SESSION_ID/log" > "$RESULTS_DIR/cite-test.log" || {
    echo -e "${YELLOW}⚠️ Could not retrieve CITE test log${NC}"
}

if [ ! -s "$RESULTS_DIR/cite-results.xml" ]; then
    echo -e "${RED}❌ No authoritative CITE results were captured${NC}"
    exit 1
fi

echo -e "${BLUE}📊 Parsing CITE test results...${NC}"

PASSED=$(grep -Eio 'status[^>]*[=:][[:space:]]*"?passed"?|status[^>]*>passed<' "$RESULTS_DIR/cite-results.xml" | wc -l | tr -d ' ')
FAILED=$(grep -Eio 'status[^>]*[=:][[:space:]]*"?failed"?|status[^>]*>failed<' "$RESULTS_DIR/cite-results.xml" | wc -l | tr -d ' ')
TOTAL=$((PASSED + FAILED))

echo "CITE Tests Passed: $PASSED"
echo "CITE Tests Failed: $FAILED"
echo "Total CITE Tests: $TOTAL"

if [ "$TOTAL" -le 0 ]; then
    echo -e "${RED}❌ CITE results were retrieved but no executed tests could be parsed${NC}"
    exit 1
fi

if [ "$FAILED" -eq 0 ]; then
    COMPLIANCE_STATUS="COMPLIANT"
elif [ "$PASSED" -gt 0 ]; then
    COMPLIANCE_STATUS="PARTIAL"
else
    COMPLIANCE_STATUS="NON_COMPLIANT"
fi

cat > "$RESULTS_DIR/cite-compliance-report.xml" << EOF_REPORT
<?xml version="1.0" encoding="UTF-8"?>
<testReport>
    <summary>
        <testsRun>$TOTAL</testsRun>
        <testsPassed>$PASSED</testsPassed>
        <testsFailed>$FAILED</testsFailed>
        <profile>$TEST_PROFILE</profile>
        <timestamp>$(date -Iseconds)</timestamp>
    </summary>
    <status>$COMPLIANCE_STATUS</status>
</testReport>
EOF_REPORT

if [ "$FAILED" -eq 0 ]; then
    echo -e "${GREEN}🎉 FULL WFS 2.0 CITE COMPLIANCE ACHIEVED!${NC}"
    exit 0
fi

if [ "$PASSED" -gt 0 ]; then
    echo -e "${YELLOW}⚠️ Partial WFS 2.0 CITE compliance${NC}"
    echo "Some tests failed - see detailed results for specific issues"
    exit 0
fi

echo -e "${RED}❌ WFS 2.0 CITE compliance tests failed${NC}"
exit 1
