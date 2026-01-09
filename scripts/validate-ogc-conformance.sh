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

if [[ ! -f "$OUTCOMES_FILE" ]]; then
    echo "Missing CITE outcomes at ${OUTCOMES_FILE}" >&2
    exit 1
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

if [[ $failures -ne 0 ]]; then
    exit 1
fi

echo "OGC API Features conformance validation passed."
