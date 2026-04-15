#!/bin/bash

# Performance baseline testing script for Honua Server
# Runs BenchmarkDotNet benchmarks

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BENCHMARK_PROJECT_DIR="$REPO_ROOT/benchmarks/Honua.Benchmarks"
BENCHMARK_PROJECT_FILE="$BENCHMARK_PROJECT_DIR/Honua.Benchmarks.csproj"
RESULTS_DIR="$REPO_ROOT/benchmark-results"
REPORT_DIR="$REPO_ROOT/performance-reports"
BASELINE_FILE="$REPO_ROOT/performance-baseline.json"
PERF_TEST_DB_PORT="${HONUA_PERF_TEST_DB_PORT:-5433}"
SERVER_BUILD_PROPERTIES=(
    "-p:HonuaIncludeAdminUi=false"
    "-p:HonuaIncludeStacOpsDemo=false"
)
BENCHMARK_WORKSPACE_ROOT=""

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
rm -rf "$RESULTS_DIR/results" "$RESULTS_DIR/BenchmarkDotNet.Artifacts"

cleanup() {
    docker stop honua-perf-test-db 2>/dev/null || true
    docker rm honua-perf-test-db 2>/dev/null || true
    if [[ -n "$BENCHMARK_WORKSPACE_ROOT" ]]; then
        rm -rf "$BENCHMARK_WORKSPACE_ROOT"
    fi
}

trap cleanup EXIT

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
if ! docker ps --filter "name=honua-perf-test-db" --format '{{.Names}}' | grep -q '^honua-perf-test-db$'; then
    echo "Starting PostgreSQL with PostGIS..."
    docker run -d \
        --name honua-perf-test-db \
        -e POSTGRES_PASSWORD=test \
        -e POSTGRES_DB=honua_test \
        -p "${PERF_TEST_DB_PORT}:5432" \
        postgis/postgis:16-3.4 || true

    # Wait for database to be ready
    echo "Waiting for database to be ready..."
    sleep 10
fi

# Set environment for tests
export ConnectionStrings__DefaultConnection="Server=localhost;Port=${PERF_TEST_DB_PORT};Database=honua_test;User Id=postgres;Password=test;"
export HONUA_BENCH_DB_URL="$ConnectionStrings__DefaultConnection"
export ASPNETCORE_ENVIRONMENT="Testing"

prepare_benchmark_workspace() {
    local cache_root="${XDG_CACHE_HOME:-$HOME/.cache}/honua-bench-workspace"
    mkdir -p "$cache_root"
    BENCHMARK_WORKSPACE_ROOT="$(mktemp -d "$cache_root/run-XXXXXX")"
    local workspace="$BENCHMARK_WORKSPACE_ROOT/workspace"
    local isolated_project_dir="$workspace/benchmarks/Honua.Benchmarks"

    mkdir -p "$workspace/benchmarks" "$workspace/src"
    cp -R "$BENCHMARK_PROJECT_DIR" "$isolated_project_dir"

    cp -R "$REPO_ROOT/src/Honua.Core" "$workspace/src/Honua.Core"
    cp -R "$REPO_ROOT/src/Honua.Postgres" "$workspace/src/Honua.Postgres"
    cp -R "$REPO_ROOT/src/Honua.Server" "$workspace/src/Honua.Server"
    cp -R "$REPO_ROOT/src/Honua.DuckDB" "$workspace/src/Honua.DuckDB"
    cp -R "$REPO_ROOT/src/Honua.ServiceDefaults" "$workspace/src/Honua.ServiceDefaults"

    for root_file in Directory.Build.props Directory.Packages.props global.json NuGet.Config nuget.config; do
        if [[ -e "$REPO_ROOT/$root_file" ]]; then
            ln -s "$REPO_ROOT/$root_file" "$workspace/$root_file"
        fi
    done

    BENCHMARK_PROJECT_DIR="$isolated_project_dir"
    BENCHMARK_PROJECT_FILE="$BENCHMARK_PROJECT_DIR/Honua.Benchmarks.csproj"
}

prepare_benchmark_workspace

# Build the project
echo -e "${YELLOW}Building benchmark project...${NC}"
pushd "$BENCHMARK_PROJECT_DIR" >/dev/null
dotnet build "$BENCHMARK_PROJECT_FILE" -c Release --no-restore "${SERVER_BUILD_PROPERTIES[@]}"

# Run benchmarks
echo -e "${YELLOW}Running performance benchmarks...${NC}"

BENCHMARK_ARGS=()
if [[ "$QUICK_MODE" == "true" ]]; then
    BENCHMARK_ARGS+=(--job short)
fi

if [[ -n "$BENCHMARK_FILTER" ]]; then
    BENCHMARK_ARGS+=(--filter "*$BENCHMARK_FILTER*")
else
    if [[ "$QUICK_MODE" == "true" ]]; then
        BENCHMARK_ARGS+=(--filter "*QueryBenchmarks*" "*ParameterCount:*10)*")
    else
        BENCHMARK_ARGS+=(--filter "*QueryBenchmarks*" "*SqlGenerationBenchmarks*")
    fi
fi

# Export results in multiple formats
EXPORT_ARGS=(--exporters json html csv --artifacts "$RESULTS_DIR")

# Run the benchmarks
echo "Executing: dotnet run --project $BENCHMARK_PROJECT_FILE -c Release ${SERVER_BUILD_PROPERTIES[*]} -- ${BENCHMARK_ARGS[*]} ${EXPORT_ARGS[*]}"
dotnet run --project "$BENCHMARK_PROJECT_FILE" -c Release "${SERVER_BUILD_PROPERTIES[@]}" -- "${BENCHMARK_ARGS[@]}" "${EXPORT_ARGS[@]}"
popd >/dev/null

# Copy results to report directory with timestamp
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
REPORT_SUBDIR="$REPORT_DIR/run_$TIMESTAMP"
mkdir -p "$REPORT_SUBDIR"

# Move BenchmarkDotNet artifacts
if [[ -d "$RESULTS_DIR/results" ]]; then
    cp -r "$RESULTS_DIR/results"/* "$REPORT_SUBDIR/"
elif [[ -d "$RESULTS_DIR/BenchmarkDotNet.Artifacts" ]]; then
    cp -r "$RESULTS_DIR/BenchmarkDotNet.Artifacts"/* "$REPORT_SUBDIR/"
fi

echo -e "${GREEN}✅ Benchmarks completed!${NC}"
echo "Results saved to: $REPORT_SUBDIR"

# Generate a normalized results.json for regression checks if QueryBenchmarks output exists
QUERY_JSON="$REPORT_SUBDIR/Honua.Benchmarks.QueryBenchmarks-report-full-compressed.json"
if [[ ! -f "$QUERY_JSON" ]]; then
    echo -e "${RED}❌ Benchmark run did not produce QueryBenchmarks JSON output${NC}"
    exit 1
fi

python3 - "$QUERY_JSON" "$REPORT_SUBDIR/results.json" <<'PY'
import json
import math
from datetime import date
from pathlib import Path
import sys

input_path = Path(sys.argv[1])
output_path = Path(sys.argv[2])

with input_path.open() as f:
    data = json.load(f)

env = data.get("HostEnvironmentInfo", {})
all_benchmarks = data.get("Benchmarks") or []
query_benchmarks = [bench for bench in all_benchmarks if bench.get("Type") == "QueryBenchmarks"]

if not query_benchmarks:
    raise SystemExit("No QueryBenchmarks results were produced.")

def percentile(values, p):
    if not values:
        return 0.0
    if len(values) == 1:
        return float(values[0])
    values = sorted(values)
    idx = p * (len(values) - 1)
    lower = int(math.floor(idx))
    upper = int(math.ceil(idx))
    if lower == upper:
        return float(values[lower])
    weight = idx - lower
    return float(values[lower] + (values[upper] - values[lower]) * weight)

benchmarks = []
for bench in query_benchmarks:
    stats = bench.get("Statistics") or {}
    memory = bench.get("Memory") or {}
    values = stats.get("OriginalValues") or []
    if stats.get("Mean") is None:
        raise SystemExit(f"Incomplete benchmark statistics for {bench.get('Method', '<unknown>')}.")

    percentiles = stats.get("Percentiles") or {}
    p95 = percentiles.get("P95")
    if p95 is None:
        p95 = percentile(values, 0.95)
    p99 = percentile(values, 0.99)

    benchmarks.append({
        "Method": bench.get("Method"),
        "Type": bench.get("Type"),
        "Statistics": {
            "Mean": stats.get("Mean", 0.0),
            "StandardError": stats.get("StandardError", 0.0),
            "Percentile95": p95,
            "Percentile99": p99,
        },
        "Memory": {
            "AllocatedBytes": memory.get("BytesAllocatedPerOperation", 0.0),
            "Gen0Collections": memory.get("Gen0Collections", 0.0),
            "Gen1Collections": memory.get("Gen1Collections", 0.0),
            "Gen2Collections": memory.get("Gen2Collections", 0.0),
        },
    })

results = {
    "Version": "1.0",
    "Created": date.today().isoformat(),
    "Description": "BenchmarkDotNet results for query benchmarks",
    "Environment": {
        "Runtime": env.get("RuntimeVersion", ""),
        "OS": env.get("OsVersion", ""),
        "Hardware": f"{env.get('ProcessorName', '')} ({env.get('PhysicalCoreCount', '')}C/{env.get('LogicalCoreCount', '')}T)",
    },
    "Benchmarks": benchmarks,
}

with output_path.open("w") as f:
    json.dump(results, f, indent=2)
    f.write("\n")
PY

# Parse and display key metrics
RESULT_JSON="$REPORT_SUBDIR/results.json"
if [[ ! -f "$RESULT_JSON" ]]; then
    RESULT_JSON="$(ls "$REPORT_SUBDIR"/*-report-full-compressed.json 2>/dev/null | head -1 || true)"
fi

if [[ -n "$RESULT_JSON" && -f "$RESULT_JSON" ]]; then
    echo -e "\n${BLUE}📊 Performance Summary${NC}"
    echo "====================="

    # Extract key metrics using jq if available
    if command -v jq &> /dev/null; then
        echo "Parsing detailed results..."
        jq -r '.Benchmarks[] | "\(.Namespace).\(.Type).\(.Method): \(.Statistics.Mean/1000000 | round)ms (±\(.Statistics.StandardError/1000000 | round)ms)"' "$RESULT_JSON" | head -10
    else
        echo "Install 'jq' for detailed result parsing"
        echo "Results available in: $RESULT_JSON"
    fi
fi

# Performance thresholds check
echo -e "\n${BLUE}🎯 Performance Targets${NC}"
echo "======================"
echo "Query (100 features):"
echo "  p50 target: < 50ms"
echo "  p99 target: < 300ms"
echo "Baseline comparisons are based on BenchmarkDotNet results"

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
    echo "Run: ./scripts/check-perf-regression.py --baseline $BASELINE_FILE --current $REPORT_SUBDIR/results.json"
fi

echo -e "\n${GREEN}🎉 Performance testing complete!${NC}"
echo "View detailed results at: $REPORT_SUBDIR/"

# Return non-zero if any critical thresholds were exceeded
# This would be implemented with actual threshold checking
exit 0
