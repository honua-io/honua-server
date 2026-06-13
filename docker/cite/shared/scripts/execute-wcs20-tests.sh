#!/bin/sh

# Runs the official OGC WCS 2.0 ETS from the ogccite/ets-wcs20 image and
# writes machine-readable results into /results for the outer harness.

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

WCS_ENDPOINT=${WCS_ENDPOINT:-http://honua-server:8080/ogc/services/cite/wcs}
WCS_CAPABILITIES_URL=${WCS_CAPABILITIES_URL:-"$WCS_ENDPOINT?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1"}
TEST_PROFILE=${TEST_PROFILE:-core}
RESULTS_DIR=${RESULTS_DIR:-/results}
TE_BASE_DIR=${TE_BASE_DIR:-/root/te_base}
TEAMENGINE_CONSOLE_VERSION=6.0.0-RC2
TEAMENGINE_CONSOLE_URL="https://repo1.maven.org/maven2/org/opengis/cite/teamengine/teamengine-console/${TEAMENGINE_CONSOLE_VERSION}/teamengine-console-${TEAMENGINE_CONSOLE_VERSION}-bin.zip"
WCS_ETS_VERSION=1.22

case "$TEST_PROFILE" in
    core)
        WCS_CORE="core"
        WCS_EXT_POST="false"
        WCS_EXT_PROC="false"
        WCS_EXT_SCAL="false"
        WCS_EXT_INT="false"
        WCS_EXT_RSUB="false"
        WCS_EXT_CRS="false"
        WCS_PROFILE_EOWCS="false"
        ;;
    crs)
        WCS_CORE="core"
        WCS_EXT_POST="false"
        WCS_EXT_PROC="false"
        WCS_EXT_SCAL="false"
        WCS_EXT_INT="false"
        WCS_EXT_RSUB="false"
        WCS_EXT_CRS="crs"
        WCS_PROFILE_EOWCS="false"
        ;;
    extensions)
        WCS_CORE="core"
        WCS_EXT_POST="post"
        WCS_EXT_PROC="processing"
        WCS_EXT_SCAL="scaling"
        WCS_EXT_INT="interpolation"
        WCS_EXT_RSUB="range subsetting"
        WCS_EXT_CRS="crs"
        WCS_PROFILE_EOWCS="false"
        ;;
    full)
        WCS_CORE="core"
        WCS_EXT_POST="post"
        WCS_EXT_PROC="processing"
        WCS_EXT_SCAL="scaling"
        WCS_EXT_INT="interpolation"
        WCS_EXT_RSUB="range subsetting"
        WCS_EXT_CRS="crs"
        WCS_PROFILE_EOWCS="eowcs"
        ;;
    *)
        echo -e "${RED}Unknown WCS CITE profile: $TEST_PROFILE${NC}"
        echo "Valid profiles: core, crs, extensions, full"
        exit 1
        ;;
esac

echo -e "${BLUE}OGC WCS 2.0 CITE runner${NC}"
echo "WCS endpoint: $WCS_ENDPOINT"
echo "Capabilities URL: $WCS_CAPABILITIES_URL"
echo "Profile: $TEST_PROFILE"
echo "Results directory: $RESULTS_DIR"

echo -e "${YELLOW}Waiting for Honua WCS endpoint...${NC}"
for attempt in $(seq 1 30); do
    if curl -sS --fail "$WCS_CAPABILITIES_URL" >/dev/null; then
        echo -e "${GREEN}Honua WCS endpoint is responding${NC}"
        break
    fi

    if [ "$attempt" -eq 30 ]; then
        echo -e "${RED}Timed out waiting for Honua WCS endpoint${NC}"
        exit 1
    fi

    echo "Attempt $attempt/30: waiting for WCS capabilities..."
    sleep 5
done

mkdir -p "$RESULTS_DIR" "$TE_BASE_DIR/resources/lib" "$TE_BASE_DIR/scripts" "$TE_BASE_DIR/users/cite/logs"
rm -rf "$RESULTS_DIR"/* /tmp/te-console /tmp/teamengine-console-bin.zip

curl -sS --fail "$WCS_CAPABILITIES_URL" > "$RESULTS_DIR/capabilities.xml"

ESCAPED_WCS_ENDPOINT=$(printf '%s' "$WCS_ENDPOINT" | sed 's/&/\&amp;/g')
cat > "$RESULTS_DIR/test-params.xml" << EOF_PARAMS
<?xml version="1.0" encoding="UTF-8"?>
<values xmlns:parsers="http://www.occamlab.com/te/parsers">
  <parsers:session>
    <parsers:test>ets-wcs20-${WCS_ETS_VERSION}</parsers:test>
    <parsers:profile>${TEST_PROFILE}</parsers:profile>
  </parsers:session>
  <value key="url">${ESCAPED_WCS_ENDPOINT}</value>
  <value key="core">${WCS_CORE}</value>
  <value key="ext_post">${WCS_EXT_POST}</value>
  <value key="ext_proc">${WCS_EXT_PROC}</value>
  <value key="ext_scal">${WCS_EXT_SCAL}</value>
  <value key="ext_int">${WCS_EXT_INT}</value>
  <value key="ext_rsub">${WCS_EXT_RSUB}</value>
  <value key="ext_crs">${WCS_EXT_CRS}</value>
  <value key="profile_eowcs">${WCS_PROFILE_EOWCS}</value>
</values>
EOF_PARAMS

cat > "$RESULTS_DIR/expected-known-failures.md" << EOF_LIMITS
# Expected WCS 2.0 CITE Thin-Slice Limitations

The WCS harness intentionally executes the official ETS even though Honua's
current WCS 2.0.1 surface is a thin slice. Failures in these areas are expected
until follow-up raster and coverage tickets expand the implementation:

- XML POST and SOAP bindings
- GML coverage output
- WCPS/processing extension
- Scaling extension
- Interpolation extension
- Range subsetting extension
- Broad CRS extension coverage beyond the advertised native CRS path
- EO-WCS profile

Unexpected harness failures include no TeamEngine result files, zero executable
tests, inability to fetch GetCapabilities, or missing seeded coverages.
EOF_LIMITS

echo -e "${YELLOW}Preparing TeamEngine console...${NC}"
curl -sS --fail --retry 5 --retry-delay 2 --retry-all-errors -L \
    -o /tmp/teamengine-console-bin.zip \
    "$TEAMENGINE_CONSOLE_URL"
unzip -q /tmp/teamengine-console-bin.zip -d /tmp/te-console
unzip -qo "$TE_BASE_DIR/../teamengine-web-${TEAMENGINE_CONSOLE_VERSION}-common-libs.zip" -d "$TE_BASE_DIR/resources/lib" || true
unzip -qo -j "$TE_BASE_DIR/../ets-wcs20-${WCS_ETS_VERSION}-deps.zip" -d "$TE_BASE_DIR/resources/lib" || true
unzip -qo "$TE_BASE_DIR/../ets-wcs20-${WCS_ETS_VERSION}-ctl.zip" -d "$TE_BASE_DIR/scripts" || true

export TE_BASE="$TE_BASE_DIR"

SESSION_NAME="cite-wcs20-session-$(date +%Y%m%d-%H%M%S)"
CONSOLE_LOG="$RESULTS_DIR/cite-console.log"
TEST_EXECUTION_START=$(date +%s)

echo -e "${YELLOW}Executing WCS 2.0 ETS...${NC}"
set +e
/tmp/te-console/bin/unix/test.sh \
    -source="$TE_BASE_DIR/scripts/wcs/2.0.1/ctl/wcs2suite-auto.xml" \
    -test=wcs2:main \
    "@url=$WCS_ENDPOINT" \
    "@core=$WCS_CORE" \
    "@ext_post=$WCS_EXT_POST" \
    "@ext_proc=$WCS_EXT_PROC" \
    "@ext_scal=$WCS_EXT_SCAL" \
    "@ext_int=$WCS_EXT_INT" \
    "@ext_rsub=$WCS_EXT_RSUB" \
    "@ext_crs=$WCS_EXT_CRS" \
    "@profile_eowcs=$WCS_PROFILE_EOWCS" \
    -logdir=users/cite/logs \
    -session="$SESSION_NAME" \
    > "$CONSOLE_LOG" 2>&1
TEST_EXIT_CODE=$?
set -e

TEST_EXECUTION_END=$(date +%s)
EXECUTION_TIME=$((TEST_EXECUTION_END - TEST_EXECUTION_START))

cat "$CONSOLE_LOG"

echo -e "${YELLOW}Collecting TeamEngine results...${NC}"
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
    TOTAL=$(extract_count "total" "$TESTNG_RESULTS")
    PASSED=$(extract_count "passed" "$TESTNG_RESULTS")
    FAILED=$(extract_count "failed" "$TESTNG_RESULTS")
    SKIPPED=$(extract_count "skipped" "$TESTNG_RESULTS")
    CANTTELL=0
else
    RESULT_CODE_LINES=$(grep -Rho 'endtest result="[0-9][0-9]*"' "$RESULTS_DIR/te-logs" 2>/dev/null || true)
    if [ -n "$RESULT_CODE_LINES" ]; then
        TOTAL=$(printf '%s\n' "$RESULT_CODE_LINES" | wc -l | tr -d ' ')
        PASSED=$(printf '%s\n' "$RESULT_CODE_LINES" | grep -c 'result="1"' || true)
        SKIPPED=$(printf '%s\n' "$RESULT_CODE_LINES" | grep -c 'result="3"' || true)
        CANTTELL=$(printf '%s\n' "$RESULT_CODE_LINES" | grep -c 'result="4"' || true)
        FAILED=$((TOTAL - PASSED - SKIPPED - CANTTELL))
    else
        TOTAL=0
        PASSED=0
        FAILED=0
        SKIPPED=0
        CANTTELL=0
    fi
fi

if [ "$TOTAL" -gt 0 ] && [ "$FAILED" -eq 0 ] && [ "$SKIPPED" -eq 0 ] && [ "$CANTTELL" -eq 0 ] && [ "$TEST_EXIT_CODE" -eq 0 ]; then
    COMPLIANCE_STATUS="COMPLIANT"
elif [ "$TOTAL" -gt 0 ] && [ "$PASSED" -gt 0 ]; then
    COMPLIANCE_STATUS="PARTIAL"
elif [ "$TOTAL" -gt 0 ]; then
    COMPLIANCE_STATUS="NON_COMPLIANT"
else
    COMPLIANCE_STATUS="UNKNOWN"
fi

cat > "$RESULTS_DIR/cite-compliance-report.xml" << EOF_REPORT
<?xml version="1.0" encoding="UTF-8"?>
<testReport>
  <summary>
    <testsRun>${TOTAL}</testsRun>
    <testsPassed>${PASSED}</testsPassed>
    <testsFailed>${FAILED}</testsFailed>
    <testsSkipped>${SKIPPED}</testsSkipped>
    <testsCantTell>${CANTTELL}</testsCantTell>
    <profile>${TEST_PROFILE}</profile>
    <executionSeconds>${EXECUTION_TIME}</executionSeconds>
    <timestamp>$(date -Iseconds)</timestamp>
  </summary>
  <status>${COMPLIANCE_STATUS}</status>
</testReport>
EOF_REPORT

echo -e "${BLUE}WCS 2.0 CITE result summary${NC}"
echo "Total: $TOTAL"
echo "Passed: $PASSED"
echo "Failed: $FAILED"
echo "Skipped: $SKIPPED"
echo "CantTell: $CANTTELL"
echo "Status: $COMPLIANCE_STATUS"
echo "Runner exit code: $TEST_EXIT_CODE"

HOST_UID=${HOST_UID:-}
HOST_GID=${HOST_GID:-}
if [ -n "$HOST_UID" ] && [ -n "$HOST_GID" ]; then
    chown -R "$HOST_UID:$HOST_GID" "$RESULTS_DIR" 2>/dev/null || true
fi

if [ "$TOTAL" -le 0 ]; then
    echo -e "${RED}No authoritative WCS CITE results were captured${NC}"
    exit 2
fi

if [ "$FAILED" -eq 0 ] && [ "$SKIPPED" -eq 0 ] && [ "$CANTTELL" -eq 0 ] && [ "$TEST_EXIT_CODE" -eq 0 ]; then
    echo -e "${GREEN}WCS 2.0 CITE compliance achieved${NC}"
    exit 0
fi

echo -e "${YELLOW}WCS 2.0 CITE completed with failures or skipped tests${NC}"
exit 0
