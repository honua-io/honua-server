#!/usr/bin/env bash

set -euo pipefail

SCRIPT_NAME="$(basename "$0")"

PHASE="all"
MODE="full"
AGENT_SELECTION="all"
OUTPUT_ROOT=".audit/runs"
DRY_RUN=false
FAIL_FAST=false
SKIP_SCALE=false
SKIP_CITE=false

ALL_AGENTS=("architecture" "security" "geodesy" "performance" "protocol")
SELECTED_AGENTS=()

RESULTS_FILE=""
SUMMARY_FILE=""
JSON_FILE=""
LOG_DIR=""
OUTPUT_DIR=""
TIMESTAMP_UTC="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
RUN_ID="$(date -u +"%Y%m%dT%H%M%SZ")"

REQUIRED_FAILURES=0
OPTIONAL_FAILURES=0
REQUIRED_SKIPS=0

usage() {
    cat <<EOF
Run Honua production-readiness audits using phase and agent gates.

Usage:
  ./$SCRIPT_NAME [options]

Options:
  --phase <1|2|3|all>                  Phase scope (default: all)
  --mode <quick|full>                  Execution profile (default: full)
  --agents <a,b,c|all>                 Agent subset (default: all)
  --output-dir <path>                  Audit artifact root (default: .audit/runs)
  --dry-run                            Print planned checks without executing
  --fail-fast                          Stop on first required check failure
  --skip-scale                         Skip scale tests (phase 3 optional checks)
  --skip-cite                          Skip CITE protocol suites
  --help, -h                           Show this help

Agents:
  architecture, security, geodesy, performance, protocol

Examples:
  ./$SCRIPT_NAME --mode full
  ./$SCRIPT_NAME --phase 1 --agents architecture,security,geodesy
  ./$SCRIPT_NAME --mode quick --dry-run
EOF
}

log_info() {
    echo "[INFO] $1"
}

log_warn() {
    echo "[WARN] $1"
}

log_error() {
    echo "[ERROR] $1" >&2
}

sanitize_field() {
    local value="${1:-}"
    value="${value//$'\t'/ }"
    value="${value//$'\r'/ }"
    value="${value//$'\n'/ }"
    printf "%s" "$value"
}

phase_selected() {
    local phase="$1"
    [[ "$PHASE" == "all" || "$PHASE" == "$phase" ]]
}

agent_selected() {
    local agent="$1"
    local selected
    for selected in "${SELECTED_AGENTS[@]}"; do
        if [[ "$selected" == "$agent" ]]; then
            return 0
        fi
    done
    return 1
}

phase_label() {
    case "$1" in
        1) echo "Critical Spatial + Security" ;;
        2) echo "Performance + Protocol" ;;
        3) echo "Integration + Observability" ;;
        *) echo "Unknown" ;;
    esac
}

record_result() {
    local agent="$1"
    local phase="$2"
    local check_name="$3"
    local required="$4"
    local status="$5"
    local duration="$6"
    local log_path="$7"
    local notes="${8:-}"

    printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n" \
        "$(sanitize_field "$agent")" \
        "$(sanitize_field "$phase")" \
        "$(sanitize_field "$check_name")" \
        "$(sanitize_field "$required")" \
        "$(sanitize_field "$status")" \
        "$(sanitize_field "$duration")" \
        "$(sanitize_field "$log_path")" \
        "$(sanitize_field "$notes")" \
        >> "$RESULTS_FILE"
}

record_skip() {
    local agent="$1"
    local phase="$2"
    local check_name="$3"
    local required="$4"
    local reason="$5"
    local log_file="$LOG_DIR/phase${phase}-${agent}-${check_name}.log"

    mkdir -p "$LOG_DIR"
    printf "SKIPPED: %s\n" "$reason" > "$log_file"
    if [[ "$required" == "required" ]]; then
        REQUIRED_SKIPS=$((REQUIRED_SKIPS + 1))
    fi
    record_result "$agent" "$phase" "$check_name" "$required" "SKIP" "0" "$log_file" "$reason"
}

run_check() {
    local agent="$1"
    local phase="$2"
    local check_name="$3"
    local required="$4"
    local command="$5"
    local log_file="$LOG_DIR/phase${phase}-${agent}-${check_name}.log"

    if [[ "$DRY_RUN" == "true" ]]; then
        log_info "[DRY-RUN] phase=$phase agent=$agent check=$check_name"
        log_info "[DRY-RUN] command: $command"
        record_result "$agent" "$phase" "$check_name" "$required" "PLANNED" "0" "$log_file" "$command"
        return 0
    fi

    log_info "phase=$phase agent=$agent check=$check_name"

    local start_epoch end_epoch duration exit_code status notes
    start_epoch=$(date +%s)

    set +e
    bash -lc "$command" > "$log_file" 2>&1
    exit_code=$?
    set -e

    end_epoch=$(date +%s)
    duration=$((end_epoch - start_epoch))

    status="PASS"
    notes=""

    if [[ $exit_code -eq 99 ]]; then
        status="SKIP"
        notes="precondition skip"
        if [[ "$required" == "required" ]]; then
            REQUIRED_SKIPS=$((REQUIRED_SKIPS + 1))
        fi
    elif [[ $exit_code -ne 0 ]]; then
        status="FAIL"
        notes="exit code $exit_code"
        if [[ "$required" == "required" ]]; then
            REQUIRED_FAILURES=$((REQUIRED_FAILURES + 1))
        else
            OPTIONAL_FAILURES=$((OPTIONAL_FAILURES + 1))
        fi
        log_warn "check failed: $check_name (see $log_file)"
    fi

    record_result "$agent" "$phase" "$check_name" "$required" "$status" "$duration" "$log_file" "$notes"

    if [[ "$status" == "FAIL" && "$required" == "required" && "$FAIL_FAST" == "true" ]]; then
        return 2
    fi

    return 0
}

run_architecture_agent() {
    if ! agent_selected "architecture"; then
        return 0
    fi

    if phase_selected "1"; then
        run_check "architecture" "1" "dotnet-format-verify" "required" \
            "dotnet format Honua.sln --verify-no-changes --verbosity minimal" || return $?

        run_check "architecture" "1" "build-warnings-as-errors" "required" \
            "dotnet build Honua.sln --configuration Release /p:TreatWarningsAsErrors=true" || return $?

        run_check "architecture" "1" "architecture-tests" "required" \
            "dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj --configuration Release --logger \"trx;LogFileName=architecture-tests.trx\"" || return $?

        run_check "architecture" "1" "aot-publish" "required" \
            "dotnet publish src/Honua.Server/Honua.Server.csproj --configuration Release --runtime linux-x64 --self-contained -p:PublishAot=true -p:HonuaSkipAdminClientForAotVerification=true -p:StripSymbols=true -o \"$OUTPUT_DIR/aot-publish\"" || return $?
    fi

    if phase_selected "3"; then
        run_check "architecture" "3" "api-surface-registry-drift" "required" \
            "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~EndpointRegistryDrift|FullyQualifiedName~ApiSurfaceComplianceTests' --logger \"trx;LogFileName=api-surface-compliance.trx\"" || return $?
    fi
}

run_security_agent() {
    if ! agent_selected "security"; then
        return 0
    fi

    if phase_selected "1"; then
        run_check "security" "1" "server-security-auth-tests" "required" \
            "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Security|FullyQualifiedName~Authentication|FullyQualifiedName~ApiKey' --logger \"trx;LogFileName=server-security.trx\"" || return $?

        run_check "security" "1" "postgres-security-tests" "required" \
            "dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Security|FullyQualifiedName~ConnectionEncryption|FullyQualifiedName~SecureConnection' --logger \"trx;LogFileName=postgres-security.trx\"" || return $?

        run_check "security" "1" "input-validation-tests" "required" \
            "dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Validation|FullyQualifiedName~FilterExpression' --logger \"trx;LogFileName=core-validation.trx\"" || return $?
    fi
}

run_geodesy_agent() {
    if ! agent_selected "geodesy"; then
        return 0
    fi

    if phase_selected "1"; then
        run_check "geodesy" "1" "postgres-crs-transform-tests" "required" \
            "dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Crs|FullyQualifiedName~SpatialReference|FullyQualifiedName~Wkt|FullyQualifiedName~Wkb|FullyQualifiedName~Transform' --logger \"trx;LogFileName=geodesy-postgres.trx\"" || return $?

        run_check "geodesy" "1" "server-spatial-query-tests" "required" \
            "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~AdvancedSpatialQuery|FullyQualifiedName~SpatialReference|FullyQualifiedName~OgcCrsResolver|FullyQualifiedName~Wms|FullyQualifiedName~Wmts|FullyQualifiedName~Mvt' --logger \"trx;LogFileName=geodesy-server.trx\"" || return $?

        run_check "geodesy" "1" "core-bbox-tiling-tests" "required" \
            "dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj --configuration Release --filter 'FullyQualifiedName~BoundingBox|FullyQualifiedName~TileMath|FullyQualifiedName~Cql2' --logger \"trx;LogFileName=geodesy-core.trx\"" || return $?
    fi
}

run_performance_agent() {
    if ! agent_selected "performance"; then
        return 0
    fi

    if phase_selected "2"; then
        run_check "performance" "2" "performance-category-tests" "required" \
            "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'Category=Performance' --logger \"trx;LogFileName=performance-tests.trx\"" || return $?

    fi

    if phase_selected "3"; then
        run_check "performance" "3" "observability-tests" "required" \
            "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~MetricsEndpointsTests|FullyQualifiedName~DatabasePerformanceEndpointsTests|FullyQualifiedName~HealthEndpointTests|FullyQualifiedName~HealthEndpointsTests'" || return $?

        if [[ "$SKIP_SCALE" == "true" ]]; then
            record_skip "performance" "3" "scale-tests" "optional" "skipped by --skip-scale"
        elif [[ -z "${HONUA_SCALE_TEST_BASE_URL:-}" ]]; then
            record_skip "performance" "3" "scale-tests" "optional" "set HONUA_SCALE_TEST_BASE_URL to enable scale checks"
        else
            run_check "performance" "3" "scale-tests" "optional" \
                "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --filter 'Category=Scale'" || return $?
        fi
    fi
}

run_protocol_agent() {
    if ! agent_selected "protocol"; then
        return 0
    fi

    if phase_selected "2"; then
        local cite_profile
        if [[ "$MODE" == "quick" ]]; then
            cite_profile="minimal"
        else
            cite_profile="full"
        fi

        run_check "protocol" "2" "openapi-contract-validation" "required" \
            "./scripts/ci/validate-openapi-contracts.sh" || return $?

        run_check "protocol" "2" "ogc-maps-conformance-tests" "required" \
            "./scripts/conformance/ogc/run-ogc-maps-conformance-tests.sh --configuration Release" || return $?

        if [[ "$SKIP_CITE" == "true" ]]; then
            record_skip "protocol" "2" "cite-ogc-features" "required" "skipped by --skip-cite"
            record_skip "protocol" "2" "cite-ogc-tiles" "required" "skipped by --skip-cite"
            record_skip "protocol" "2" "cite-wms-13" "required" "skipped by --skip-cite"
            record_skip "protocol" "2" "cite-wmts-10" "required" "skipped by --skip-cite"
        else
            run_check "protocol" "2" "cite-ogc-features" "required" \
                "./scripts/conformance/cite/run-cite-tests.sh --profile $cite_profile" || return $?
            run_check "protocol" "2" "cite-ogc-tiles" "required" \
                "./scripts/conformance/cite/run-cite-tiles-tests.sh --profile $cite_profile" || return $?
            run_check "protocol" "2" "cite-wms-13" "required" \
                "./scripts/conformance/cite/run-cite-wms-tests.sh --profile $cite_profile" || return $?
            run_check "protocol" "2" "cite-wmts-10" "required" \
                "./scripts/conformance/cite/run-cite-wmts-tests.sh --profile $cite_profile" || return $?
        fi
    fi

    if phase_selected "3"; then
        run_check "protocol" "3" "integration-protocol-tests" "required" \
            "dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~OgcFeaturesEndpointTests|FullyQualifiedName~OData|FullyQualifiedName~MapServer|FullyQualifiedName~FeatureServer|FullyQualifiedName~ApiSurfaceComplianceTests'" || return $?

        record_skip "protocol" "3" "desktop-gis-client-validation" "optional" \
            "manual validation required with QGIS, ArcGIS Pro, and web clients"
    fi
}

generate_summary_files() {
    local total_checks pass_count fail_count skip_count planned_count
    total_checks=$(awk -F'\t' 'NR>1 {count++} END {print count+0}' "$RESULTS_FILE")
    pass_count=$(awk -F'\t' 'NR>1 && $5=="PASS" {count++} END {print count+0}' "$RESULTS_FILE")
    fail_count=$(awk -F'\t' 'NR>1 && $5=="FAIL" {count++} END {print count+0}' "$RESULTS_FILE")
    skip_count=$(awk -F'\t' 'NR>1 && $5=="SKIP" {count++} END {print count+0}' "$RESULTS_FILE")
    planned_count=$(awk -F'\t' 'NR>1 && $5=="PLANNED" {count++} END {print count+0}' "$RESULTS_FILE")

    {
        echo "# Honua Production Audit Summary"
        echo
        echo "- Run ID: \`$RUN_ID\`"
        echo "- Generated (UTC): \`$TIMESTAMP_UTC\`"
        echo "- Phase selection: \`$PHASE\`"
        echo "- Mode: \`$MODE\`"
        echo "- Agents: \`${SELECTED_AGENTS[*]}\`"
        echo "- Dry run: \`$DRY_RUN\`"
        echo
        echo "## Result Counts"
        echo
        echo "| Status | Count |"
        echo "|---|---:|"
        echo "| PASS | $pass_count |"
        echo "| FAIL | $fail_count |"
        echo "| SKIP | $skip_count |"
        echo "| PLANNED | $planned_count |"
        echo "| TOTAL | $total_checks |"
        echo
        echo "## Phase Gates"
        echo
        echo "| Phase | Label | Required Fails | Required Skips | Gate |"
        echo "|---|---|---:|---:|---|"

        local phase required_fails required_skips gate
        for phase in 1 2 3; do
            if ! phase_selected "$phase"; then
                continue
            fi
            required_fails=$(awk -F'\t' -v p="$phase" 'NR>1 && $2==p && $4=="required" && $5=="FAIL" {count++} END {print count+0}' "$RESULTS_FILE")
            required_skips=$(awk -F'\t' -v p="$phase" 'NR>1 && $2==p && $4=="required" && $5=="SKIP" {count++} END {print count+0}' "$RESULTS_FILE")

            if [[ "$required_fails" -gt 0 ]]; then
                gate="FAIL"
            elif [[ "$required_skips" -gt 0 ]]; then
                gate="INCOMPLETE"
            else
                gate="PASS"
            fi

            echo "| $phase | $(phase_label "$phase") | $required_fails | $required_skips | $gate |"
        done

        echo
        echo "## Detailed Results"
        echo
        echo "| Agent | Phase | Check | Required | Status | Duration (s) | Log | Notes |"
        echo "|---|---|---|---|---|---:|---|---|"
        awk -F'\t' 'NR>1 { printf "| %s | %s | %s | %s | %s | %s | `%s` | %s |\n", $1, $2, $3, $4, $5, $6, $7, $8 }' "$RESULTS_FILE"
    } > "$SUMMARY_FILE"

    if command -v python3 >/dev/null 2>&1; then
        python3 - "$RESULTS_FILE" "$JSON_FILE" "$RUN_ID" "$TIMESTAMP_UTC" "$PHASE" "$MODE" "${SELECTED_AGENTS[*]}" "$DRY_RUN" <<'PY'
import csv
import json
import sys
from collections import Counter, defaultdict

results_file, json_file, run_id, timestamp_utc, phase, mode, agents, dry_run = sys.argv[1:]

rows = []
with open(results_file, "r", encoding="utf-8", newline="") as f:
    reader = csv.DictReader(
        f,
        delimiter="\t",
        fieldnames=["agent", "phase", "check", "required", "status", "duration_seconds", "log_path", "notes"],
    )
    next(reader, None)
    for row in reader:
        rows.append(row)

status_counts = Counter(row["status"] for row in rows)
phase_counts = defaultdict(lambda: Counter())
for row in rows:
    phase_counts[row["phase"]][row["status"]] += 1

document = {
    "run_id": run_id,
    "generated_utc": timestamp_utc,
    "phase_selection": phase,
    "mode": mode,
    "agents": agents.split(),
    "dry_run": dry_run.lower() == "true",
    "status_counts": dict(status_counts),
    "phase_status_counts": {phase: dict(counts) for phase, counts in phase_counts.items()},
    "checks": rows,
}

with open(json_file, "w", encoding="utf-8") as f:
    json.dump(document, f, indent=2)
    f.write("\n")
PY
    fi
}

parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --phase)
                PHASE="$2"
                shift 2
                ;;
            --mode)
                MODE="$2"
                shift 2
                ;;
            --agents)
                AGENT_SELECTION="$2"
                shift 2
                ;;
            --output-dir)
                OUTPUT_ROOT="$2"
                shift 2
                ;;
            --dry-run)
                DRY_RUN=true
                shift
                ;;
            --fail-fast)
                FAIL_FAST=true
                shift
                ;;
            --skip-scale)
                SKIP_SCALE=true
                shift
                ;;
            --skip-cite)
                SKIP_CITE=true
                shift
                ;;
            --help|-h)
                usage
                exit 0
                ;;
            *)
                log_error "Unknown option: $1"
                usage
                exit 1
                ;;
        esac
    done
}

validate_inputs() {
    case "$PHASE" in
        1|2|3|all) ;;
        *)
            log_error "--phase must be one of: 1, 2, 3, all"
            exit 1
            ;;
    esac

    case "$MODE" in
        quick|full) ;;
        *)
            log_error "--mode must be one of: quick, full"
            exit 1
            ;;
    esac

    if [[ "$AGENT_SELECTION" == "all" ]]; then
        SELECTED_AGENTS=("${ALL_AGENTS[@]}")
        return
    fi

    IFS=',' read -r -a SELECTED_AGENTS <<< "$AGENT_SELECTION"
    if [[ "${#SELECTED_AGENTS[@]}" -eq 0 ]]; then
        log_error "--agents cannot be empty"
        exit 1
    fi

    local idx agent known valid
    for idx in "${!SELECTED_AGENTS[@]}"; do
        agent="$(echo "${SELECTED_AGENTS[$idx]}" | xargs)"
        SELECTED_AGENTS[$idx]="$agent"
        valid=false
        for known in "${ALL_AGENTS[@]}"; do
            if [[ "$agent" == "$known" ]]; then
                valid=true
                break
            fi
        done
        if [[ "$valid" == "false" ]]; then
            log_error "Unknown agent: $agent"
            log_error "Allowed agents: ${ALL_AGENTS[*]}"
            exit 1
        fi
    done
}

prepare_output() {
    OUTPUT_DIR="$OUTPUT_ROOT/$RUN_ID"
    LOG_DIR="$OUTPUT_DIR/logs"
    RESULTS_FILE="$OUTPUT_DIR/results.tsv"
    SUMMARY_FILE="$OUTPUT_DIR/summary.md"
    JSON_FILE="$OUTPUT_DIR/summary.json"

    mkdir -p "$LOG_DIR"
    printf "agent\tphase\tcheck\trequired\tstatus\tduration_seconds\tlog_path\tnotes\n" > "$RESULTS_FILE"
}

main() {
    parse_args "$@"
    validate_inputs
    prepare_output

    log_info "Starting production audit"
    log_info "run_id=$RUN_ID phase=$PHASE mode=$MODE agents=${SELECTED_AGENTS[*]}"
    log_info "artifacts=$OUTPUT_DIR"

    run_architecture_agent || true
    if [[ "$FAIL_FAST" == "true" && "$REQUIRED_FAILURES" -gt 0 ]]; then
        generate_summary_files
        log_error "Fail-fast stop after required check failure"
        exit 1
    fi

    run_security_agent || true
    if [[ "$FAIL_FAST" == "true" && "$REQUIRED_FAILURES" -gt 0 ]]; then
        generate_summary_files
        log_error "Fail-fast stop after required check failure"
        exit 1
    fi

    run_geodesy_agent || true
    if [[ "$FAIL_FAST" == "true" && "$REQUIRED_FAILURES" -gt 0 ]]; then
        generate_summary_files
        log_error "Fail-fast stop after required check failure"
        exit 1
    fi

    run_performance_agent || true
    if [[ "$FAIL_FAST" == "true" && "$REQUIRED_FAILURES" -gt 0 ]]; then
        generate_summary_files
        log_error "Fail-fast stop after required check failure"
        exit 1
    fi

    run_protocol_agent || true

    generate_summary_files

    if [[ "$DRY_RUN" == "true" ]]; then
        log_info "Dry run complete: no checks executed."
        log_info "Summary: $SUMMARY_FILE"
        exit 0
    fi

    if [[ "$REQUIRED_FAILURES" -gt 0 ]]; then
        log_error "Audit completed with $REQUIRED_FAILURES required check failure(s)."
        log_error "Summary: $SUMMARY_FILE"
        exit 1
    fi

    if [[ "$REQUIRED_SKIPS" -gt 0 ]]; then
        log_error "Audit completed with $REQUIRED_SKIPS required check skip(s); audit is incomplete."
        log_error "Summary: $SUMMARY_FILE"
        exit 1
    fi

    if [[ "$OPTIONAL_FAILURES" -gt 0 ]]; then
        log_warn "Audit completed with $OPTIONAL_FAILURES optional check failure(s)."
    fi

    log_info "Audit completed successfully."
    log_info "Summary: $SUMMARY_FILE"
}

main "$@"
