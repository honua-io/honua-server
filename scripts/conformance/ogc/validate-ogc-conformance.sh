#!/bin/bash

# Validate that advertised OGC API Features conformance classes were actually tested.

set -euo pipefail

RESULTS_DIR="cite-results"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --results)
            RESULTS_DIR="$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: $0 [--results PATH]"
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 1
            ;;
    esac
done

CONFORMANCE_FILE="${RESULTS_DIR}/conformance.json"
OUTCOMES_FILE="${RESULTS_DIR}/test-outcomes.tsv"

if [[ ! -f "$CONFORMANCE_FILE" ]]; then
    echo "Missing conformance declaration at ${CONFORMANCE_FILE}" >&2
    exit 1
fi

use_outcomes=true
if [[ ! -f "$OUTCOMES_FILE" ]]; then
    use_outcomes=false
    echo "CITE outcomes not found at ${OUTCOMES_FILE}; falling back to CITE log summary." >&2
fi

if ! command -v jq >/dev/null 2>&1; then
    echo "jq is required to validate conformance classes" >&2
    exit 1
fi

mapfile -t CONFORMANCE_CLASSES < <(jq -r '.conformsTo[]?' "$CONFORMANCE_FILE")

if [[ ${#CONFORMANCE_CLASSES[@]} -eq 0 ]]; then
    echo "No conformance classes advertised in ${CONFORMANCE_FILE}" >&2
    exit 1
fi

failures=0
if [[ "$use_outcomes" == "true" ]]; then
    for conformance in "${CONFORMANCE_CLASSES[@]}"; do
        class_name="${conformance##*/}"

        statuses=$(awk -F'\t' -v class="$conformance" -v name="$class_name" '
            BEGIN { IGNORECASE = 1 }
            {
                field = tolower($2);
                if (index(field, tolower(class)) > 0 ||
                    index(field, "conformance/" tolower(name)) > 0 ||
                    index(field, "conf/" tolower(name)) > 0) {
                    print tolower($1);
                }
            }' "$OUTCOMES_FILE" | sort -u | tr '\n' ' ')

        if [[ -z "$statuses" ]]; then
            echo "Conformance class ${conformance} advertised but no tests were recorded." >&2
            failures=1
            continue
        fi

        if echo "$statuses" | grep -Eq "(skipped|canttell)"; then
            echo "Conformance class ${conformance} advertised but tests were skipped or could not be determined." >&2
            echo "Observed statuses: $statuses" >&2
            failures=1
            continue
        fi

        if echo "$statuses" | grep -Eq "failed"; then
            echo "Conformance class ${conformance} advertised but tests failed." >&2
            echo "Observed statuses: $statuses" >&2
            failures=1
            continue
        fi
    done
else
    log_file=$(ls -1t "${RESULTS_DIR}"/cite-session-*/log.xml 2>/dev/null | head -n 1 || true)
    if [[ -z "$log_file" ]]; then
        echo "Missing CITE outcomes and log summary at ${RESULTS_DIR}/cite-session-*/log.xml" >&2
        exit 1
    fi

    declare -A GROUP_PASSED GROUP_FAILED GROUP_SKIPPED
    while IFS='|' read -r group passed failed skipped; do
        if [[ -z "$group" ]]; then
            continue
        fi
        GROUP_PASSED["$group"]="$passed"
        GROUP_FAILED["$group"]="$failed"
        GROUP_SKIPPED["$group"]="$skipped"
    done < <(
        sed 's/<[^>]*>//g' "$log_file" | awk '
            BEGIN { in_groups = 0; group = "" }
            /Test groups/ { in_groups = 1; next }
            in_groups {
                line = $0
                gsub(/^[ \t]+|[ \t]+$/, "", line)
                if (line == "") { next }
                if (line ~ /^=+$/) { next }
                if (line ~ /Passed:/) {
                    split(line, parts, "|")
                    passed = parts[1]; failed = parts[2]; skipped = parts[3]
                    gsub(/[^0-9]/, "", passed)
                    gsub(/[^0-9]/, "", failed)
                    gsub(/[^0-9]/, "", skipped)
                    print group "|" passed "|" failed "|" skipped
                    next
                }
                if (line ~ /^Test groups/) { next }
                group = line
            }')

    if [[ ${#GROUP_FAILED[@]} -eq 0 ]]; then
        echo "Unable to parse CITE group summary from ${log_file}" >&2
        exit 1
    fi

    declare -A CLASS_TO_GROUP=(
        ["http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core"]="Core"
        ["http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30"]="Core"
        ["http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html"]="Core"
        ["http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson"]="Core"
        ["http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs"]="Coordinate Reference Systems by Reference"
    )

    resolve_group() {
        local desired="$1"
        if [[ -n "${GROUP_FAILED[$desired]:-}" ]]; then
            echo "$desired"
            return 0
        fi
        local desired_lower="${desired,,}"
        for candidate in "${!GROUP_FAILED[@]}"; do
            local candidate_lower="${candidate,,}"
            if [[ "$candidate_lower" == *"$desired_lower"* ]]; then
                echo "$candidate"
                return 0
            fi
        done
        return 1
    }

    for conformance in "${CONFORMANCE_CLASSES[@]}"; do
        mapped_group="${CLASS_TO_GROUP[$conformance]:-}"
        if [[ -z "$mapped_group" ]]; then
            echo "Conformance class ${conformance} is not validated by this CITE suite; skipping." >&2
            continue
        fi

        if ! group_name=$(resolve_group "$mapped_group"); then
            echo "Conformance class ${conformance} advertised but no test group results were recorded for ${mapped_group}." >&2
            failures=1
            continue
        fi

        if [[ "${GROUP_FAILED[$group_name]:-0}" != "0" ]]; then
            echo "Conformance class ${conformance} advertised but tests failed (group: ${group_name})." >&2
            failures=1
            continue
        fi

        if [[ "${GROUP_SKIPPED[$group_name]:-0}" != "0" ]]; then
            echo "Conformance class ${conformance} advertised but tests were skipped (group: ${group_name})." >&2
            failures=1
            continue
        fi
    done
fi

if [[ $failures -ne 0 ]]; then
    exit 1
fi

echo "OGC API Features conformance validation passed."
