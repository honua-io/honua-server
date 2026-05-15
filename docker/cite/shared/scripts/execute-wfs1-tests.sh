#!/bin/sh

# OGC CITE Team Engine test execution script for WFS 1.0.0 and 1.1.0.

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

WFS_VERSION=${WFS_VERSION:-1.1.0}
WFS_ENDPOINT=${WFS_ENDPOINT:-http://honua-server:8080/wfs}
TEST_PROFILE=${TEST_PROFILE:-basic}
RESULTS_DIR=${RESULTS_DIR:-/results}
TE_BASE_DIR=${TE_BASE_DIR:-/root/te_base}
TEAMENGINE_CONSOLE_VERSION=6.0.0-RC2
TEAMENGINE_CONSOLE_URL="https://repo1.maven.org/maven2/org/opengis/cite/teamengine/teamengine-console/${TEAMENGINE_CONSOLE_VERSION}/teamengine-console-${TEAMENGINE_CONSOLE_VERSION}-bin.zip"

case "$WFS_VERSION" in
    1.0.0)
        SUITE_LABEL="WFS 1.0"
        ETS_NAME="ets-wfs10"
        ETS_VERSION="1.14"
        CTL_RESULT_PREFIX="test1.0.0-basic"
        CTL_ZIP="/root/ets-wfs10-1.14-ctl.zip"
        DEPS_ZIP="/root/ets-wfs10-1.14-deps.zip"
        CTL_SOURCE="$TE_BASE_DIR/scripts/wfs10/1.0.0/ctl/main.xml"
        SUMMARY_FILE="cite-wfs10-summary.md"
        ;;
    1.1.0)
        SUITE_LABEL="WFS 1.1"
        ETS_NAME="ets-wfs11"
        ETS_VERSION="1.38"
        CTL_RESULT_PREFIX="wfs-1.1.0-basic"
        CTL_ZIP="/root/ets-wfs11-1.38-ctl.zip"
        DEPS_ZIP="/root/ets-wfs11-1.38-deps.zip"
        CTL_SOURCE="$TE_BASE_DIR/scripts/wfs/1.1.0/ctl/main.ctl"
        SUMMARY_FILE="cite-wfs11-summary.md"
        ;;
    *)
        echo -e "${RED}Unsupported WFS_VERSION: $WFS_VERSION${NC}"
        exit 1
        ;;
esac

WFS_CAPABILITIES_URL="${WFS_ENDPOINT}?service=WFS&version=${WFS_VERSION}&request=GetCapabilities"

echo -e "${BLUE}Executing OGC CITE ${SUITE_LABEL} Conformance Tests${NC}"
echo "================================================"
echo "WFS Endpoint: $WFS_ENDPOINT"
echo "WFS Version: $WFS_VERSION"
echo "Test Profile: $TEST_PROFILE"
echo "Results Directory: $RESULTS_DIR"
echo ""

case "$TEST_PROFILE" in
    basic|default)
        ;;
    *)
        echo -e "${RED}Unknown test profile: $TEST_PROFILE${NC}"
        echo "Valid profiles: basic, default"
        exit 1
        ;;
esac

echo -e "${YELLOW}Waiting for Honua Server...${NC}"
for i in $(seq 1 30); do
    if curl -s -f "$WFS_CAPABILITIES_URL" > /dev/null; then
        echo -e "${GREEN}Honua Server is responding${NC}"
        break
    fi

    if [ "$i" -eq 30 ]; then
        echo -e "${RED}Timeout waiting for Honua Server${NC}"
        exit 1
    fi

    echo "Attempt $i/30: Waiting for Honua Server..."
    sleep 5
done

mkdir -p "$RESULTS_DIR"
mkdir -p "$TE_BASE_DIR/resources/lib" "$TE_BASE_DIR/scripts" "$TE_BASE_DIR/users/cite/logs"
rm -rf "$RESULTS_DIR"/*
rm -rf /tmp/te-console /tmp/teamengine-console-bin.zip

echo "Using WFS capabilities document: $WFS_CAPABILITIES_URL"
echo -e "${YELLOW}Preparing TeamEngine console...${NC}"
curl -sS --fail --retry 5 --retry-delay 2 --retry-all-errors -L \
    -o /tmp/teamengine-console-bin.zip \
    "$TEAMENGINE_CONSOLE_URL"
unzip -q /tmp/teamengine-console-bin.zip -d /tmp/te-console
unzip -qo /root/teamengine-web-6.0.0-RC2-common-libs.zip -d "$TE_BASE_DIR/resources/lib" || true
unzip -qo -j "$DEPS_ZIP" -d "$TE_BASE_DIR/resources/lib" || true
unzip -qo "$CTL_ZIP" -d "$TE_BASE_DIR/scripts" || true

export TE_BASE="$TE_BASE_DIR"

SESSION_NAME="$(echo "$ETS_NAME" | tr -cd '[:alnum:]-')-test-$(date +%s)"
ESCAPED_WFS_CAPABILITIES_URL=$(printf '%s' "$WFS_CAPABILITIES_URL" | sed 's/&/\&amp;/g')

if [ "$WFS_VERSION" = "1.0.0" ]; then
    cat > "$RESULTS_DIR/test-params.xml" << EOF_PARAMS
<?xml version="1.0" encoding="UTF-8"?>
<values xmlns:parsers="http://www.occamlab.com/te/parsers">
  <parsers:session>
    <parsers:test>${ETS_NAME}-${ETS_VERSION}</parsers:test>
    <parsers:profile>basic</parsers:profile>
  </parsers:session>
  <value key="capabilities-url">$ESCAPED_WFS_CAPABILITIES_URL</value>
</values>
EOF_PARAMS
else
    cat > "$RESULTS_DIR/test-params.xml" << EOF_PARAMS
<?xml version="1.0" encoding="UTF-8"?>
<values xmlns:parsers="http://www.occamlab.com/te/parsers">
  <parsers:session>
    <parsers:test>${ETS_NAME}-${ETS_VERSION}</parsers:test>
    <parsers:profile>basic</parsers:profile>
  </parsers:session>
  <value key="capabilities-url">$ESCAPED_WFS_CAPABILITIES_URL</value>
  <value key="profile">sf-0</value>
</values>
EOF_PARAMS
fi

echo -e "${YELLOW}Executing ${SUITE_LABEL} conformance tests...${NC}"
echo "Session: $SESSION_NAME"
echo "CTL source: $CTL_SOURCE"

TEST_EXECUTION_START=$(date +%s)
TEST_EXIT_CODE=0
CONSOLE_LOG="$RESULTS_DIR/cite-console.log"

set +e
/tmp/te-console/bin/unix/test.sh \
    -source="$CTL_SOURCE" \
    -form="$RESULTS_DIR/test-params.xml" \
    -logdir=users/cite/logs \
    -session="$SESSION_NAME" \
    > "$CONSOLE_LOG" 2>&1
TEST_EXIT_CODE=$?
set -e

cat "$CONSOLE_LOG"

TEST_EXECUTION_END=$(date +%s)
EXECUTION_TIME=$((TEST_EXECUTION_END - TEST_EXECUTION_START))

echo -e "${GREEN}CITE test execution completed${NC}"
echo "Execution time: ${EXECUTION_TIME}s"
echo "Runner exit code: ${TEST_EXIT_CODE}"

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

extract_ctl_counts() {
    logs_dir="$1"
    result_prefix="$2"

    find "$logs_dir" -name 'log.xml' -exec awk -v prefix="$result_prefix" '
        BEGIN {
            name = "";
            result = "";
            prefix = tolower(prefix);
        }

        function read_attr(line, attr_name, value) {
            value = line;
            sub("^.*" attr_name "=\"", "", value);
            sub("\".*$", "", value);
            return value;
        }

        /local-name=/ && name == "" {
            candidate = read_attr($0, "local-name");
            if (index(tolower(candidate), prefix) == 1) {
                name = candidate;
            }
        }

        /<endtest result=/ {
            result = read_attr($0, "result");
        }

        END {
            if (name != "" && result != "") {
                print result, name;
            }
        }
    ' {} \; | awk '
        {
            total++;
            if ($1 == "1") {
                passed++;
            } else {
                failed++;
            }
        }

        END {
            printf "%d %d %d %d\n", passed + 0, failed + 0, 0, total + 0;
        }
    '
}

if [ -n "$TESTNG_RESULTS" ] && [ -f "$TESTNG_RESULTS" ]; then
    PASSED=$(extract_count "passed" "$TESTNG_RESULTS")
    FAILED=$(extract_count "failed" "$TESTNG_RESULTS")
    SKIPPED=$(extract_count "skipped" "$TESTNG_RESULTS")
    TOTAL=$((PASSED + FAILED + SKIPPED))
else
    set -- $(extract_ctl_counts "$TE_BASE_DIR/users/cite/logs" "$CTL_RESULT_PREFIX")
    PASSED=$1
    FAILED=$2
    SKIPPED=$3
    TOTAL=$4
fi

if [ "$TOTAL" -gt 0 ]; then
    SUCCESS_RATE=$((PASSED * 100 / TOTAL))
else
    SUCCESS_RATE=0
fi

echo -e "${BLUE}Parsing CITE test results...${NC}"
echo "CITE Tests Passed: $PASSED"
echo "CITE Tests Failed: $FAILED"
echo "CITE Tests Skipped: $SKIPPED"
echo "Total CITE Tests: $TOTAL"

if [ "$TOTAL" -le 0 ]; then
    echo -e "${RED}No authoritative CITE results were captured${NC}"
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
        <testsSkipped>$SKIPPED</testsSkipped>
        <profile>$TEST_PROFILE</profile>
        <timestamp>$(date -Iseconds)</timestamp>
    </summary>
    <status>$COMPLIANCE_STATUS</status>
</testReport>
EOF_REPORT

cat > "$RESULTS_DIR/$SUMMARY_FILE" << EOF_SUMMARY
# ${SUITE_LABEL} CITE Conformance Summary

- **Profile**: $TEST_PROFILE
- **Endpoint**: $WFS_CAPABILITIES_URL
- **Total Tests**: $TOTAL
- **Passed**: $PASSED
- **Failed**: $FAILED
- **Skipped**: $SKIPPED
- **CantTell**: 0
- **Success Rate**: $SUCCESS_RATE%
- **Compliance Status**: $COMPLIANCE_STATUS
- **Generated**: $(date -Iseconds)
EOF_SUMMARY

HOST_UID=${HOST_UID:-}
HOST_GID=${HOST_GID:-}
if [ -n "$HOST_UID" ] && [ -n "$HOST_GID" ]; then
    chown -R "$HOST_UID:$HOST_GID" "$RESULTS_DIR" 2>/dev/null || true
fi

if [ "$FAILED" -eq 0 ]; then
    echo -e "${GREEN}${SUITE_LABEL} CITE gate passed with no failed tests${NC}"
    exit 0
fi

echo -e "${RED}${SUITE_LABEL} CITE compliance tests failed${NC}"
if [ "$TEST_EXIT_CODE" -ne 0 ]; then
    exit "$TEST_EXIT_CODE"
fi

exit 1
