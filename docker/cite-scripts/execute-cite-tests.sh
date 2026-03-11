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
TEST_PROFILE=${TEST_PROFILE:-basic}
RESULTS_DIR=${RESULTS_DIR:-/results}
TE_BASE_DIR=${TE_BASE_DIR:-/root/te_base}
TEAMENGINE_CONSOLE_VERSION=6.0.0-RC2
TEAMENGINE_CONSOLE_URL="https://repo1.maven.org/maven2/org/opengis/cite/teamengine/teamengine-console/${TEAMENGINE_CONSOLE_VERSION}/teamengine-console-${TEAMENGINE_CONSOLE_VERSION}-bin.zip"
WFS_CAPABILITIES_URL="${WFS_ENDPOINT}?service=WFS&version=2.0.0&request=GetCapabilities"

echo "WFS Endpoint: $WFS_ENDPOINT"
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

# Prepare directories and console bits.
mkdir -p "$RESULTS_DIR"
mkdir -p "$TE_BASE_DIR/resources/lib" "$TE_BASE_DIR/scripts" "$TE_BASE_DIR/users/cite/logs"
rm -rf "$RESULTS_DIR"/*
rm -rf /tmp/te-console /tmp/teamengine-console-bin.zip

case "$TEST_PROFILE" in
    "basic"|"transactional"|"full")
        ;;
    *)
        echo -e "${RED}❌ Unknown test profile: $TEST_PROFILE${NC}"
        echo "Valid profiles: basic, transactional, full"
        exit 1
        ;;
esac

echo "Using WFS capabilities document: $WFS_CAPABILITIES_URL"
if [ "$TEST_PROFILE" != "basic" ]; then
    echo "Note: the WFS ETS derives applicable conformance classes from the capabilities document."
fi

echo -e "${YELLOW}Preparing TeamEngine console...${NC}"
curl -sS --fail --retry 5 --retry-delay 2 --retry-all-errors -L \
    -o /tmp/teamengine-console-bin.zip \
    "$TEAMENGINE_CONSOLE_URL"
unzip -q /tmp/teamengine-console-bin.zip -d /tmp/te-console
unzip -qo -j /root/ets-wfs20-1.43-deps.zip -d "$TE_BASE_DIR/resources/lib" || true
unzip -qo /root/ets-wfs20-1.43-ctl.zip -d "$TE_BASE_DIR/scripts" || true

export TE_BASE="$TE_BASE_DIR"

SESSION_NAME="wfs20-test-$(date +%s)"
ESCAPED_WFS_CAPABILITIES_URL=$(printf '%s' "$WFS_CAPABILITIES_URL" | sed 's/&/\&amp;/g')
cat > "$RESULTS_DIR/test-params.xml" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<values xmlns:parsers="http://www.occamlab.com/te/parsers">
  <parsers:session>
    <parsers:test>ets-wfs20-1.43</parsers:test>
    <parsers:profile>$TEST_PROFILE</parsers:profile>
  </parsers:session>
  <value key="wfs-uri">$ESCAPED_WFS_CAPABILITIES_URL</value>
</values>
EOF

echo -e "${YELLOW}Executing WFS 2.0 conformance tests...${NC}"
echo "Session: $SESSION_NAME"
echo "This may take 10-30 minutes depending on the advertised WFS capabilities..."

TEST_EXECUTION_START=$(date +%s)
TEST_EXIT_CODE=0
CONSOLE_LOG="$RESULTS_DIR/cite-console.log"

set +e
/tmp/te-console/bin/unix/test.sh \
    -source="$TE_BASE_DIR/scripts/wfs/2.0.0/ctl/wfs-suite.ctl" \
    -form="$RESULTS_DIR/test-params.xml" \
    -logdir=users/cite/logs \
    -session="$SESSION_NAME" \
    > "$CONSOLE_LOG" 2>&1
TEST_EXIT_CODE=$?
set -e

cat "$CONSOLE_LOG"

TEST_EXECUTION_END=$(date +%s)
EXECUTION_TIME=$((TEST_EXECUTION_END - TEST_EXECUTION_START))

echo -e "${GREEN}✅ CITE test execution completed${NC}"
echo "Execution time: ${EXECUTION_TIME}s"
echo "test.sh exit code: ${TEST_EXIT_CODE}"

echo -e "${YELLOW}Collecting CITE test results...${NC}"
mkdir -p "$RESULTS_DIR/te-logs"
cp -R "$TE_BASE_DIR/users/cite/logs/." "$RESULTS_DIR/te-logs/" 2>/dev/null || true

TESTNG_RESULTS=$(find "$TE_BASE_DIR/users/cite/logs" -name 'testng-results.xml' | sort | tail -n 1)
HTML_REPORT=$(find "$TE_BASE_DIR/users/cite/logs" -path '*/html/index.html' | sort | tail -n 1)

if [ -n "$TESTNG_RESULTS" ] && [ -f "$TESTNG_RESULTS" ]; then
    cp "$TESTNG_RESULTS" "$RESULTS_DIR/testng-results.xml"
fi

if [ -n "$HTML_REPORT" ] && [ -f "$HTML_REPORT" ]; then
    mkdir -p "$RESULTS_DIR/html-report"
    cp -R "$(dirname "$HTML_REPORT")/." "$RESULTS_DIR/html-report/" 2>/dev/null || true
fi

extract_count() {
    attr="$1"
    file="$2"
    value=$(grep -o "${attr}=\"[0-9][0-9]*\"" "$file" | head -n 1 | tr -cd '0-9')
    if [ -n "$value" ]; then
        printf '%s\n' "$value"
    else
        printf '0\n'
    fi
}

if [ -n "$TESTNG_RESULTS" ] && [ -f "$TESTNG_RESULTS" ]; then
    PASSED=$(extract_count "passed" "$TESTNG_RESULTS")
    FAILED=$(extract_count "failed" "$TESTNG_RESULTS")
    SKIPPED=$(extract_count "skipped" "$TESTNG_RESULTS")
    TOTAL=$((PASSED + FAILED))
else
    PASSED=0
    FAILED=0
    SKIPPED=0
    TOTAL=0
fi

echo -e "${BLUE}📊 Parsing CITE test results...${NC}"
echo "CITE Tests Passed: $PASSED"
echo "CITE Tests Failed: $FAILED"
echo "CITE Tests Skipped: $SKIPPED"
echo "Total CITE Tests: $TOTAL"

if [ "$TOTAL" -le 0 ]; then
    echo -e "${RED}❌ No authoritative CITE results were captured${NC}"
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
if [ "$TEST_EXIT_CODE" -ne 0 ]; then
    exit "$TEST_EXIT_CODE"
fi

exit 1
