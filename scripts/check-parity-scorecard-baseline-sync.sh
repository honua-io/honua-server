#!/usr/bin/env bash
set -euo pipefail

parity_test_file="${1:-tests/Honua.Server.Tests/Import/GeoservicesParityIntegrationTests.cs}"
baseline_scorecard="${2:-tests/Honua.Server.Tests/Import/parity-scorecard-baseline.json}"

if [[ ! -f "$parity_test_file" ]]; then
  echo "Parity test definition file not found: $parity_test_file" >&2
  exit 1
fi

if [[ ! -f "$baseline_scorecard" ]]; then
  echo "Parity baseline scorecard not found: $baseline_scorecard" >&2
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required to validate the parity baseline." >&2
  exit 1
fi

service_cases="$(
  awk '
    /private static readonly ParityServiceCase\[\] _serviceCases =/ { in_block = 1 }
    in_block { print }
    in_block && /^[[:space:]]*\];[[:space:]]*$/ { exit }
  ' "$parity_test_file" \
    | sed -n 's/^[[:space:]]*Name: "\([^"]*\)",[[:space:]]*$/\1/p' \
    | sort
)"

baseline_cases="$(
  jq -r '.Cases[].ServiceCase' "$baseline_scorecard" | sort
)"

if [[ -z "$service_cases" ]]; then
  echo "No parity service cases were found in $parity_test_file." >&2
  exit 1
fi

if [[ -z "$baseline_cases" ]]; then
  echo "No baseline service cases were found in $baseline_scorecard." >&2
  exit 1
fi

if ! diff_output="$(
  diff -u \
    <(printf '%s\n' "$service_cases") \
    <(printf '%s\n' "$baseline_cases")
)"; then
  echo "Parity scorecard baseline is out of sync with the current service-case list." >&2
  echo "$diff_output" >&2
  exit 1
fi

echo "Parity scorecard baseline matches the current service-case list."
