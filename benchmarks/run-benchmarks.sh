#!/bin/bash

# Honua Server Performance Benchmarks Runner
# Provides a convenient way to run performance benchmarks with various configurations

set -euo pipefail

# Configuration
PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/Honua.Benchmarks" && pwd)"
ARTIFACTS_PATH="${PROJECT_PATH}/BenchmarkDotNet.Artifacts"
RESULTS_PATH="${ARTIFACTS_PATH}/results"

# Default values
CATEGORY="All"
JOB="Default"
OUTPUT="Console"
PROFILER="None"
CLEAN=false
VALIDATE=false

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Helper functions
print_header() {
    echo ""
    echo -e "${CYAN}$(printf '=%.0s' {1..80})${NC}"
    echo -e "${YELLOW}$1${NC}"
    echo -e "${CYAN}$(printf '=%.0s' {1..80})${NC}"
}

print_info() {
    echo -e "${GREEN}$1${NC}"
}

print_warning() {
    echo -e "${YELLOW}$1${NC}"
}

print_error() {
    echo -e "${RED}$1${NC}"
}

show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Runs Honua Server performance benchmarks with various configurations.

OPTIONS:
    -c, --category CATEGORY     Benchmark category to run
                               Options: All, SqlGeneration, Query
                               Default: All

    -j, --job JOB              Job configuration
                               Options: Default, Short, Long, Memory
                               Default: Default

    -o, --output OUTPUT        Output formats
                               Options: Console, Json, Html, Csv, Markdown, All
                               Default: Console

    -p, --profiler PROFILER    Profiler to use
                               Options: None, ETW (Windows), Perf (Linux)
                               Default: None

    --clean                    Clean previous benchmark results before running
    --validate                 Run validation checks after benchmarks complete
    -h, --help                 Show this help message

EXAMPLES:
    $0 -c SqlGeneration -j Short -o Json
        Run SQL generation benchmarks with short job configuration and JSON output

    $0 -c All -j Default -o All --clean
        Run all benchmarks with default configuration, all output formats, cleaning previous results

    $0 -c Query -p Perf --validate
        Run query benchmarks with Perf profiling and validation
EOF
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -c|--category)
            CATEGORY="$2"
            shift 2
            ;;
        -j|--job)
            JOB="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT="$2"
            shift 2
            ;;
        -p|--profiler)
            PROFILER="$2"
            shift 2
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        --validate)
            VALIDATE=true
            shift
            ;;
        -h|--help)
            show_usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Validate arguments
validate_category() {
    case $CATEGORY in
        All|SqlGeneration|Query)
            ;;
        *)
            print_error "Invalid category: $CATEGORY"
            show_usage
            exit 1
            ;;
    esac
}

validate_job() {
    case $JOB in
        Default|Short|Long|Memory)
            ;;
        *)
            print_error "Invalid job: $JOB"
            show_usage
            exit 1
            ;;
    esac
}

validate_output() {
    case $OUTPUT in
        Console|Json|Html|Csv|Markdown|All)
            ;;
        *)
            print_error "Invalid output: $OUTPUT"
            show_usage
            exit 1
            ;;
    esac
}

validate_profiler() {
    case $PROFILER in
        None|ETW|Perf)
            ;;
        *)
            print_error "Invalid profiler: $PROFILER"
            show_usage
            exit 1
            ;;
    esac
}

test_prerequisites() {
    print_info "Checking prerequisites..."

    # Check .NET SDK
    if command -v dotnet &> /dev/null; then
        DOTNET_VERSION=$(dotnet --version)
        print_info "✓ .NET SDK version: $DOTNET_VERSION"
    else
        print_error "✗ .NET SDK not found. Please install .NET 10.0 or later."
        exit 1
    fi

    # Check PostgreSQL for query benchmarks
    if [[ "$CATEGORY" == "All" || "$CATEGORY" == "Query" ]]; then
        if command -v psql &> /dev/null; then
            export PGPASSWORD="honua"
            if psql -h localhost -U honua -d honua_dev -c "SELECT version();" -t &>/dev/null; then
                print_info "✓ PostgreSQL connection successful"
            else
                print_warning "⚠ PostgreSQL connection failed. Query benchmarks may fail."
            fi
        else
            print_warning "⚠ PostgreSQL client not available. Query benchmarks will be skipped."
        fi
    fi

    print_info "Prerequisites check complete."
}

clear_previous_results() {
    if [[ "$CLEAN" == true && -d "$ARTIFACTS_PATH" ]]; then
        print_info "Cleaning previous benchmark results..."
        rm -rf "$ARTIFACTS_PATH"
        print_info "✓ Previous results cleaned"
    fi
}

build_arguments() {
    local args=()

    # Filter by category
    if [[ "$CATEGORY" != "All" ]]; then
        args+=("--filter" "*$CATEGORY*")
    fi

    # Job configuration
    case $JOB in
        Short)
            args+=("--job" "short")
            ;;
        Long)
            args+=("--job" "long")
            ;;
        Memory)
            args+=("--job" "dry" "--diagnosers" "memory")
            ;;
    esac

    # Output formats
    if [[ "$OUTPUT" != "Console" ]]; then
        case $OUTPUT in
            Json)
                args+=("--exporters" "json")
                ;;
            Html)
                args+=("--exporters" "html")
                ;;
            Csv)
                args+=("--exporters" "csv")
                ;;
            Markdown)
                args+=("--exporters" "markdown")
                ;;
            All)
                args+=("--exporters" "json,html,csv,markdown")
                ;;
        esac
    fi

    # Profiler
    if [[ "$PROFILER" != "None" ]]; then
        args+=("--profiler" "$PROFILER")
    fi

    printf '%s\n' "${args[@]}"
}

start_benchmarks() {
    local args=("$@")

    print_header "Running Benchmarks"
    print_info "Category: $CATEGORY"
    print_info "Job: $JOB"
    print_info "Output: $OUTPUT"
    print_info "Profiler: $PROFILER"
    print_info "Arguments: ${args[*]}"

    cd "$PROJECT_PATH"
    local start_time=$(date +%s)

    if [[ ${#args[@]} -gt 0 ]]; then
        dotnet run -c Release -- "${args[@]}"
    else
        dotnet run -c Release
    fi

    local exit_code=$?
    local end_time=$(date +%s)
    local duration=$((end_time - start_time))
    local duration_formatted=$(printf '%02d:%02d' $((duration / 60)) $((duration % 60)))

    if [[ $exit_code -eq 0 ]]; then
        print_info "✓ Benchmarks completed successfully in $duration_formatted"
        return 0
    else
        print_error "✗ Benchmarks failed with exit code $exit_code"
        return $exit_code
    fi
}

show_results() {
    if [[ -d "$RESULTS_PATH" ]]; then
        print_header "Benchmark Results"

        # Show latest results files
        local html_files=($(find "$RESULTS_PATH" -name "*.html" -type f | sort -t/ -k1 -r))
        if [[ ${#html_files[@]} -gt 0 ]]; then
            local latest_html="${html_files[0]}"
            print_info "Latest HTML report: $latest_html"

            if command -v xdg-open &> /dev/null; then
                print_info "Opening results in default browser..."
                xdg-open "$latest_html" &
            elif command -v open &> /dev/null; then
                print_info "Opening results in default browser..."
                open "$latest_html"
            else
                print_info "HTML report available at: $latest_html"
            fi
        fi

        # Show JSON results if available
        local json_files=($(find "$RESULTS_PATH" -name "*.json" -type f | sort -t/ -k1 -r))
        if [[ ${#json_files[@]} -gt 0 ]]; then
            print_info "JSON results: ${json_files[0]}"
        fi

        # Show summary statistics
        local log_files=($(find "$ARTIFACTS_PATH" -name "*.log" -type f | sort -t/ -k1 -r))
        if [[ ${#log_files[@]} -gt 0 ]]; then
            local latest_log="${log_files[0]}"
            print_info "Latest log: $latest_log"

            # Extract summary from log
            local summary=$(grep -E "Summary|Total time|Mean|StdDev" "$latest_log" | tail -10)
            if [[ -n "$summary" ]]; then
                print_info "Summary:"
                echo "$summary" | sed 's/^/  /'
            fi
        fi
    else
        print_warning "No benchmark results found at $RESULTS_PATH"
    fi
}

validate_performance() {
    if [[ "$VALIDATE" != true ]]; then
        return
    fi

    print_header "Validating Performance Results"

    # Check for performance regressions using jq if available
    local json_files=($(find "$RESULTS_PATH" -name "*.json" -type f | sort -t/ -k1 -r))
    if [[ ${#json_files[@]} -eq 0 ]]; then
        print_warning "No JSON results found for validation"
        return
    fi

    if ! command -v jq &> /dev/null; then
        print_warning "jq not available. Skipping detailed performance validation."
        print_info "Install jq for detailed performance analysis."
        return
    fi

    local results_file="${json_files[0]}"
    local failures=()

    # Extract benchmark data and validate
    while IFS= read -r benchmark; do
        local method=$(echo "$benchmark" | jq -r '.Method')
        local mean=$(echo "$benchmark" | jq -r '.Statistics.Mean')
        local allocated=$(echo "$benchmark" | jq -r '.Memory.Allocated // 0')

        # Define thresholds based on benchmark type
        local mean_threshold=999999999 # Default high threshold
        local allocated_threshold=999999999

        case $method in
            *SqlGeneration*Simple*)
                mean_threshold=1000 # 1μs in nanoseconds
                allocated_threshold=1024 # 1KB
                ;;
            *SqlGeneration*Complex*)
                mean_threshold=10000 # 10μs
                allocated_threshold=5120 # 5KB
                ;;
        esac

        # Check thresholds
        if (( $(echo "$mean > $mean_threshold" | bc -l) )); then
            local mean_ms=$(echo "scale=2; $mean / 1000000" | bc)
            local threshold_ms=$(echo "scale=2; $mean_threshold / 1000000" | bc)
            failures+=("❌ $method: Mean ${mean_ms}ms exceeds threshold ${threshold_ms}ms")
        fi

        if (( $(echo "$allocated > $allocated_threshold" | bc -l) )); then
            local allocated_kb=$(echo "scale=2; $allocated / 1024" | bc)
            local threshold_kb=$(echo "scale=2; $allocated_threshold / 1024" | bc)
            failures+=("❌ $method: Allocated ${allocated_kb}KB exceeds threshold ${threshold_kb}KB")
        fi
    done < <(jq -c '.Benchmarks[]' "$results_file")

    if [[ ${#failures[@]} -eq 0 ]]; then
        print_info "✅ All performance targets met!"
    else
        print_warning "⚠️ Performance issues detected:"
        printf '%s\n' "${failures[@]}" | while read -r line; do
            print_error "  $line"
        done
    fi
}

# Main execution
main() {
    validate_category
    validate_job
    validate_output
    validate_profiler

    print_header "Honua Server Performance Benchmarks"

    test_prerequisites
    clear_previous_results

    # Build arguments array
    mapfile -t arguments < <(build_arguments)

    if start_benchmarks "${arguments[@]}"; then
        show_results
        validate_performance

        print_header "Benchmark Run Complete"
        print_info "✅ Benchmarks completed successfully!"
        print_info "Results saved to: $ARTIFACTS_PATH"
        exit 0
    else
        print_header "Benchmark Run Failed"
        print_error "❌ Benchmarks failed. Check the output above for details."
        exit 1
    fi
}

# Check if bc is available for floating point arithmetic
if ! command -v bc &> /dev/null; then
    print_warning "Warning: 'bc' not found. Some validation features may not work correctly."
fi

# Trap for cleanup on exit
cleanup() {
    if [[ -n ${PROJECT_PATH:-} ]]; then
        cd ~
    fi
}
trap cleanup EXIT

# Run main function
main "$@"
