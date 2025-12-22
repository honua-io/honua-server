#!/bin/bash

# Performance baseline testing script for Honua Server
# Runs BenchmarkDotNet benchmarks and NBomber load tests

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
BENCHMARK_PROJECT="benchmarks/Honua.Benchmarks"
RESULTS_DIR="benchmark-results"
REPORT_DIR="performance-reports"
BASELINE_FILE="performance-baseline.json"

echo -e "${BLUE}🚀 Honua Performance Baseline Tests${NC}"
echo "=================================="

# Check prerequisites
echo -e "${YELLOW}Checking prerequisites...${NC}"

if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}❌ .NET CLI not found. Please install .NET 10.0+${NC}"
    exit 1
fi

if ! command -v docker &> /dev/null; then
    echo -e "${RED}❌ Docker not found. Required for PostgreSQL test database${NC}"
    exit 1
fi

# Create results directories
mkdir -p "$RESULTS_DIR"
mkdir -p "$REPORT_DIR"

# Parse command line options
BENCHMARK_FILTER=""
QUICK_MODE=false
BASELINE_MODE=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --filter)
            BENCHMARK_FILTER="$2"
            shift 2
            ;;
        --quick)
            QUICK_MODE=true
            shift
            ;;
        --baseline)
            BASELINE_MODE=true
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --filter <pattern>    Run only benchmarks matching pattern"
            echo "  --quick              Run abbreviated benchmarks (faster, less accurate)"
            echo "  --baseline           Update performance baseline"
            echo "  --help, -h           Show this help"
            echo ""
            echo "Examples:"
            echo "  $0                   Run all benchmarks"
            echo "  $0 --filter Query    Run only query benchmarks"
            echo "  $0 --quick           Quick performance check"
            echo "  $0 --baseline        Update baseline and run full suite"
            exit 0
            ;;
        *)
            echo -e "${RED}❌ Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

# Start test database if not running
echo -e "${YELLOW}Starting test database...${NC}"
if ! docker ps | grep -q postgis; then
    echo "Starting PostgreSQL with PostGIS..."
    docker run -d \
        --name honua-perf-test-db \
        -e POSTGRES_PASSWORD=test \
        -e POSTGRES_DB=honua_test \
        -p 5433:5432 \
        postgis/postgis:16-3.4 || true

    # Wait for database to be ready
    echo "Waiting for database to be ready..."
    sleep 10
fi

# Set environment for tests
export ConnectionStrings__DefaultConnection="Server=localhost;Port=5433;Database=honua_test;User Id=postgres;Password=test;"
export ASPNETCORE_ENVIRONMENT="Testing"

# Build the project
echo -e "${YELLOW}Building benchmark project...${NC}"
dotnet build "$BENCHMARK_PROJECT" -c Release --no-restore

# Run benchmarks
echo -e "${YELLOW}Running performance benchmarks...${NC}"

BENCHMARK_ARGS=""
if [[ "$QUICK_MODE" == "true" ]]; then
    BENCHMARK_ARGS="--job short"
fi

if [[ -n "$BENCHMARK_FILTER" ]]; then
    BENCHMARK_ARGS="$BENCHMARK_ARGS --filter *$BENCHMARK_FILTER*"
fi

# Export results in multiple formats
EXPORT_ARGS="--exporters json,html,csv --artifacts $RESULTS_DIR"

# Run the benchmarks
echo "Executing: dotnet run --project $BENCHMARK_PROJECT -c Release -- $BENCHMARK_ARGS $EXPORT_ARGS"
dotnet run --project "$BENCHMARK_PROJECT" -c Release -- $BENCHMARK_ARGS $EXPORT_ARGS

# Copy results to report directory with timestamp
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
REPORT_SUBDIR="$REPORT_DIR/run_$TIMESTAMP"
mkdir -p "$REPORT_SUBDIR"

# Move BenchmarkDotNet artifacts
if [[ -d "$RESULTS_DIR/BenchmarkDotNet.Artifacts" ]]; then
    cp -r "$RESULTS_DIR/BenchmarkDotNet.Artifacts"/* "$REPORT_SUBDIR/"
fi

# Move NBomber results
if [[ -d "load-test-results" ]]; then
    cp -r load-test-results/* "$REPORT_SUBDIR/" 2>/dev/null || true
fi

echo -e "${GREEN}✅ Benchmarks completed!${NC}"
echo "Results saved to: $REPORT_SUBDIR"

# Parse and display key metrics
if [[ -f "$REPORT_SUBDIR/results.json" ]]; then
    echo -e "\n${BLUE}📊 Performance Summary${NC}"
    echo "====================="

    # Extract key metrics using jq if available
    if command -v jq &> /dev/null; then
        echo "Parsing detailed results..."
        jq -r '.Benchmarks[] | "\(.Namespace).\(.Type).\(.Method): \(.Statistics.Mean/1000000 | round)ms (±\(.Statistics.StandardError/1000000 | round)ms)"' "$REPORT_SUBDIR/results.json" | head -10
    else
        echo "Install 'jq' for detailed result parsing"
        echo "Results available in: $REPORT_SUBDIR/results.json"
    fi
fi

# Performance thresholds check
echo -e "\n${BLUE}🎯 Performance Targets${NC}"
echo "======================"
echo "Query (100 features):"
echo "  p50 target: < 50ms"
echo "  p99 target: < 300ms"
echo "Load test targets:"
echo "  Simple queries: > 1000 rps"
echo "  Spatial queries: > 500 rps"
echo "Memory targets:"
echo "  Memory delta: < 50MB after 10k queries"

# Update baseline if requested
if [[ "$BASELINE_MODE" == "true" ]]; then
    echo -e "\n${YELLOW}📈 Updating performance baseline...${NC}"
    if [[ -f "$REPORT_SUBDIR/results.json" ]]; then
        cp "$REPORT_SUBDIR/results.json" "$BASELINE_FILE"
        echo -e "${GREEN}✅ Baseline updated: $BASELINE_FILE${NC}"
    else
        echo -e "${RED}❌ No results.json found to use as baseline${NC}"
    fi
fi

# Compare to baseline if it exists
if [[ -f "$BASELINE_FILE" && -f "$REPORT_SUBDIR/results.json" ]]; then
    echo -e "\n${YELLOW}📊 Comparing to baseline...${NC}"
    echo "Baseline comparison requires manual analysis of:"
    echo "  Current: $REPORT_SUBDIR/results.json"
    echo "  Baseline: $BASELINE_FILE"
    echo "  Threshold: ±10% performance regression"
fi

# Cleanup
echo -e "\n${YELLOW}🧹 Cleaning up test database...${NC}"
docker stop honua-perf-test-db 2>/dev/null || true
docker rm honua-perf-test-db 2>/dev/null || true

echo -e "\n${GREEN}🎉 Performance testing complete!${NC}"
echo "View detailed results at: $REPORT_SUBDIR/"

# Return non-zero if any critical thresholds were exceeded
# This would be implemented with actual threshold checking
exit 0