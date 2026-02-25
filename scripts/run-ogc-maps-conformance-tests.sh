#!/usr/bin/env bash

# OGC API - Maps conformance verification for Honua Server.
# This runner uses the server integration conformance suite while
# external OGC CITE publication for Maps is still maturing.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

RESULTS_DIR="ogc-maps-results"
CONFIGURATION="Release"
VERBOSE=false

TEST_PROJECT="tests/Honua.Server.Tests/Honua.Server.Tests.csproj"
TEST_FILTER="FullyQualifiedName~OgcMapsConformanceTests|FullyQualifiedName~OgcMapsBasicTests|FullyQualifiedName~OgcMapsConformanceHandlerTests"

usage() {
    cat <<EOF
Usage: $0 [OPTIONS]

Options:
  --results-dir <path>      Output directory (default: ogc-maps-results)
  --configuration <config>  dotnet test configuration (default: Release)
  --verbose                 Print dotnet test output to console
  --help, -h                Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --results-dir)
            RESULTS_DIR="$2"
            shift 2
            ;;
        --configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            usage
            exit 1
            ;;
    esac
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo -e "${RED}dotnet SDK is required${NC}"
    exit 1
fi

mkdir -p "$RESULTS_DIR"

TRX_FILE="$RESULTS_DIR/ogc-maps-conformance.trx"
LOG_FILE="$RESULTS_DIR/ogc-maps-conformance.log"
SUMMARY_FILE="$RESULTS_DIR/ogc-maps-summary.md"

rm -f "$TRX_FILE" "$LOG_FILE" "$SUMMARY_FILE"

echo -e "${BLUE}OGC API - Maps Conformance Tests${NC}"
echo "================================="

set +e
dotnet test "$TEST_PROJECT" \
    --configuration "$CONFIGURATION" \
    --filter "$TEST_FILTER" \
    --results-directory "$RESULTS_DIR" \
    --logger "trx;LogFileName=$(basename "$TRX_FILE")" \
    > "$LOG_FILE" 2>&1
TEST_EXIT_CODE=$?
set -e

if [[ "$VERBOSE" == "true" ]]; then
    cat "$LOG_FILE"
fi

if [[ ! -f "$TRX_FILE" ]]; then
    ALT_TRX=$(find "$RESULTS_DIR" -maxdepth 2 -type f -name "*.trx" | sort | tail -n 1 || true)
    if [[ -n "$ALT_TRX" ]]; then
        TRX_FILE="$ALT_TRX"
    fi
fi

extract_counter() {
    local line="$1"
    local counter="$2"
    local value
    value=$(printf "%s\n" "$line" | sed -n "s/.* ${counter}=\"\\([0-9]\\+\\)\".*/\\1/p")
    if [[ -n "$value" ]]; then
        printf "%s\n" "$value"
    else
        printf "0\n"
    fi
}

TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0
SKIPPED_TESTS=0

if [[ -f "$TRX_FILE" ]]; then
    COUNTERS_LINE=$(grep -m1 "<Counters " "$TRX_FILE" || true)
    if [[ -n "$COUNTERS_LINE" ]]; then
        TOTAL_TESTS=$(extract_counter "$COUNTERS_LINE" "total")
        PASSED_TESTS=$(extract_counter "$COUNTERS_LINE" "passed")
        FAILED_TESTS=$(extract_counter "$COUNTERS_LINE" "failed")
        SKIPPED_TESTS=$(extract_counter "$COUNTERS_LINE" "notExecuted")
        if [[ "$SKIPPED_TESTS" == "0" ]]; then
            SKIPPED_TESTS=$(extract_counter "$COUNTERS_LINE" "skipped")
        fi
    fi
fi

if [[ "$TOTAL_TESTS" -gt 0 ]]; then
    SUCCESS_RATE=$(awk -v p="$PASSED_TESTS" -v t="$TOTAL_TESTS" 'BEGIN { printf "%.1f", (p/t)*100 }')
else
    SUCCESS_RATE="0.0"
fi

{
    echo "# OGC API - Maps Conformance Summary"
    echo
    echo "- Test project: \`$TEST_PROJECT\`"
    echo "- Configuration: \`$CONFIGURATION\`"
    echo "- Filter: \`$TEST_FILTER\`"
    echo "- TRX: \`$TRX_FILE\`"
    echo "- Raw log: \`$LOG_FILE\`"
    echo
    echo "## Results"
    echo
    echo "- Total Tests: $TOTAL_TESTS"
    echo "- Passed: $PASSED_TESTS"
    echo "- Failed: $FAILED_TESTS"
    echo "- Skipped: $SKIPPED_TESTS"
    echo "- Success Rate: ${SUCCESS_RATE}%"
    echo
    if [[ "$TEST_EXIT_CODE" -eq 0 ]]; then
        echo "Status: PASS"
    else
        echo "Status: FAIL (dotnet test exit code: $TEST_EXIT_CODE)"
    fi
} > "$SUMMARY_FILE"

echo "Summary report saved to: $SUMMARY_FILE"
echo "Raw test log saved to: $LOG_FILE"

if [[ "$TEST_EXIT_CODE" -ne 0 ]]; then
    echo -e "${RED}OGC API - Maps conformance tests failed${NC}"
    exit "$TEST_EXIT_CODE"
fi

if [[ "$TOTAL_TESTS" -eq 0 ]]; then
    echo -e "${RED}No conformance tests were executed${NC}"
    exit 1
fi

if [[ "$FAILED_TESTS" -ne 0 ]]; then
    echo -e "${RED}OGC API - Maps conformance tests reported failures${NC}"
    exit 1
fi

echo -e "${GREEN}OGC API - Maps conformance tests passed${NC}"
