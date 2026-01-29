#!/bin/bash

# Load/soak testing script for Honua Server
# Runs NBomber scenarios and captures system + API metrics.

set -euo pipefail

LOAD_TEST_PROJECT="tests/Honua.LoadTests"
BASE_URL="${BASE_URL:-${HONUA_LOAD_BASE_URL:-http://localhost:5000}}"
PROFILE="${PROFILE:-${HONUA_LOAD_PROFILE:-quick}}"
DURATION="${DURATION:-${HONUA_LOAD_DURATION:-}}"
RAMP_UP="${RAMP_UP:-${HONUA_LOAD_RAMP_UP:-}}"
RAMP_DOWN="${RAMP_DOWN:-${HONUA_LOAD_RAMP_DOWN:-}}"
LAYER_ID="${LAYER_ID:-${HONUA_LOAD_LAYER_ID:-0}}"
COLLECTION_ID="${COLLECTION_ID:-${HONUA_LOAD_COLLECTION_ID:-0}}"
TILE_MATRIX_SET_ID="${TILE_MATRIX_SET_ID:-${HONUA_LOAD_TILE_MATRIX_SET_ID:-WebMercatorQuad}}"
TARGET_SCENARIOS="${TARGET_SCENARIOS:-${HONUA_LOAD_TARGET_SCENARIOS:-}}"
MAX_FAILURE_RATE="${MAX_FAILURE_RATE:-${HONUA_LOAD_MAX_FAILURE_RATE:-}}"
REPORT_ROOT="${REPORT_DIR:-${HONUA_LOAD_REPORT_FOLDER:-load-test-reports}}"
SAMPLE_INTERVAL="${SAMPLE_INTERVAL:-30}"
HONUA_DOCKER_CONTAINER="${HONUA_DOCKER_CONTAINER:-}"
HONUA_PROCESS_PID="${HONUA_PROCESS_PID:-}"
HONUA_API_KEY="${HONUA_API_KEY:-}"

usage() {
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  --base-url <url>         Base URL for Honua Server"
    echo "  --profile <name>         Load profile: quick, nightly, soak"
    echo "  --duration <timespan>    Override steady-state duration (e.g., 30m, 00:30:00)"
    echo "  --ramp-up <timespan>     Override ramp-up duration"
    echo "  --ramp-down <timespan>   Override ramp-down duration"
    echo "  --layer-id <id>          Feature layer id (default: 0)"
    echo "  --collection-id <id>     OGC collection id (default: 0)"
    echo "  --tile-matrix-set <id>   Tile matrix set id (default: WebMercatorQuad)"
    echo "  --target-scenarios <csv> Comma-separated scenario names to run"
    echo "  --report-dir <path>      Root output directory (default: load-test-reports)"
    echo "  --max-failure-rate <n>   Max failed request ratio (0-1, e.g. 0.0001 = 0.01%)"
    echo "  --sample-interval <sec>  Metrics sampling interval (default: 30)"
    echo "  --container <name>       Docker container name for CPU/memory sampling"
    echo "  --pid <pid>              Process ID for CPU/memory sampling"
    echo "  --api-key <key>          API key for private metrics endpoints"
    echo "  --help                   Show this help"
}

while [[ $# -gt 0 ]]; do
    case $1 in
        --base-url)
            BASE_URL="$2"
            shift 2
            ;;
        --profile)
            PROFILE="$2"
            shift 2
            ;;
        --duration)
            DURATION="$2"
            shift 2
            ;;
        --ramp-up)
            RAMP_UP="$2"
            shift 2
            ;;
        --ramp-down)
            RAMP_DOWN="$2"
            shift 2
            ;;
        --layer-id)
            LAYER_ID="$2"
            shift 2
            ;;
        --collection-id)
            COLLECTION_ID="$2"
            shift 2
            ;;
        --tile-matrix-set)
            TILE_MATRIX_SET_ID="$2"
            shift 2
            ;;
        --target-scenarios)
            TARGET_SCENARIOS="$2"
            shift 2
            ;;
        --max-failure-rate)
            MAX_FAILURE_RATE="$2"
            shift 2
            ;;
        --report-dir)
            REPORT_ROOT="$2"
            shift 2
            ;;
        --sample-interval)
            SAMPLE_INTERVAL="$2"
            shift 2
            ;;
        --container)
            HONUA_DOCKER_CONTAINER="$2"
            shift 2
            ;;
        --pid)
            HONUA_PROCESS_PID="$2"
            shift 2
            ;;
        --api-key)
            HONUA_API_KEY="$2"
            shift 2
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

if ! command -v dotnet &> /dev/null; then
    echo "dotnet CLI not found. Install .NET 10.0+ to run load tests."
    exit 1
fi

RUN_TIMESTAMP=$(date +%Y%m%d_%H%M%S)
RUN_DIR="$REPORT_ROOT/run_$RUN_TIMESTAMP"
NBOMBER_DIR="$RUN_DIR/nbomber"
METRICS_DIR="$RUN_DIR/metrics"
mkdir -p "$NBOMBER_DIR" "$METRICS_DIR"

METRICS_BASE="$BASE_URL/api/metrics"
RESOURCE_LOG="$METRICS_DIR/resources.csv"
echo "timestamp,source,cpu_percent,mem_usage,mem_percent,notes" > "$RESOURCE_LOG"

AUTH_HEADER=()
if [[ -n "$HONUA_API_KEY" ]]; then
    AUTH_HEADER=(-H "X-API-Key: $HONUA_API_KEY")
fi

PRIVATE_METRICS_AVAILABLE="false"
STAMP=$(date -u +"%Y%m%dT%H%M%SZ")
if curl -sf "${AUTH_HEADER[@]}" "$METRICS_BASE/database" -o "$METRICS_DIR/database_${STAMP}.json"; then
    PRIVATE_METRICS_AVAILABLE="true"
else
    echo "Private metrics unavailable. Enable HONUA_DEV_AUTH or provide HONUA_API_KEY." \
        > "$METRICS_DIR/private-metrics-unavailable.txt"
fi

sample_metrics() {
    local stamp
    stamp=$(date -u +"%Y%m%dT%H%M%SZ")

    curl -sf "$METRICS_BASE/health" -o "$METRICS_DIR/health_${stamp}.json" || true

    if [[ "$PRIVATE_METRICS_AVAILABLE" == "true" ]]; then
        curl -sf "${AUTH_HEADER[@]}" "$METRICS_BASE/memory" -o "$METRICS_DIR/memory_${stamp}.json" || true
        curl -sf "${AUTH_HEADER[@]}" "$METRICS_BASE/performance" -o "$METRICS_DIR/performance_${stamp}.json" || true
        curl -sf "${AUTH_HEADER[@]}" "$METRICS_BASE/database" -o "$METRICS_DIR/database_${stamp}.json" || true
        curl -sf "${AUTH_HEADER[@]}" "$METRICS_BASE/cache" -o "$METRICS_DIR/cache_${stamp}.json" || true
    fi
}

sample_resources() {
    local stamp
    stamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

    if [[ -n "$HONUA_DOCKER_CONTAINER" ]]; then
        if command -v docker &> /dev/null; then
            local stats
            stats=$(docker stats --no-stream --format '{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}}' "$HONUA_DOCKER_CONTAINER" 2>/dev/null || true)
            if [[ -n "$stats" ]]; then
                echo "$stamp,docker,$stats," >> "$RESOURCE_LOG"
            fi
        fi
        return
    fi

    if [[ -n "$HONUA_PROCESS_PID" ]]; then
        local stats
        stats=$(ps -p "$HONUA_PROCESS_PID" -o %cpu=,rss= 2>/dev/null | awk '{print $1","$2}' || true)
        if [[ -n "$stats" ]]; then
            echo "$stamp,process,$stats,,rss_kb" >> "$RESOURCE_LOG"
        fi
    fi
}

start_samplers() {
    sample_metrics || true
    sample_resources || true

    while true; do
        sleep "$SAMPLE_INTERVAL"
        sample_metrics || true
        sample_resources || true
    done
}

SAMPLER_PID=""
start_samplers &
SAMPLER_PID=$!

cleanup() {
    if [[ -n "$SAMPLER_PID" ]]; then
        kill "$SAMPLER_PID" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

LOAD_ARGS=(--base-url "$BASE_URL" --profile "$PROFILE" --report-folder "$NBOMBER_DIR")
LOAD_ARGS+=(--layer-id "$LAYER_ID" --collection-id "$COLLECTION_ID" --tile-matrix-set "$TILE_MATRIX_SET_ID")

if [[ -n "$DURATION" ]]; then
    LOAD_ARGS+=(--duration "$DURATION")
fi

if [[ -n "$RAMP_UP" ]]; then
    LOAD_ARGS+=(--ramp-up "$RAMP_UP")
fi

if [[ -n "$RAMP_DOWN" ]]; then
    LOAD_ARGS+=(--ramp-down "$RAMP_DOWN")
fi

if [[ -n "$TARGET_SCENARIOS" ]]; then
    LOAD_ARGS+=(--target-scenarios "$TARGET_SCENARIOS")
fi

if [[ -n "$MAX_FAILURE_RATE" ]]; then
    LOAD_ARGS+=(--max-failure-rate "$MAX_FAILURE_RATE")
fi

echo "Running load tests against $BASE_URL (profile: $PROFILE)"
echo "Reports: $RUN_DIR"

dotnet run --project "$LOAD_TEST_PROJECT" -- "${LOAD_ARGS[@]}"

sample_metrics || true
sample_resources || true

echo "Load/soak test run complete."
echo "NBomber reports: $NBOMBER_DIR"
echo "Metrics samples: $METRICS_DIR"
