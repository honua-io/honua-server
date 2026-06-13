#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <current-scorecard.json> <baseline-scorecard.json>" >&2
  exit 1
fi

current_scorecard="$1"
baseline_scorecard="$2"

if [[ ! -f "$current_scorecard" ]]; then
  echo "Current scorecard not found: $current_scorecard" >&2
  exit 1
fi

if [[ ! -f "$baseline_scorecard" ]]; then
  echo "Baseline scorecard not found: $baseline_scorecard"
  echo "Skipping regression gate."
  exit 0
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required to compare scorecards." >&2
  exit 1
fi

regressions="$(
  jq -n \
    --slurpfile baseline "$baseline_scorecard" \
    --slurpfile current "$current_scorecard" '
      # Keep this query compatible with jq 1.6 on GitHub-hosted runners.
      def case_index($cases):
        reduce $cases[] as $case ({}; .[$case.ServiceCase] = $case);

      ($baseline[0].Cases // []) as $baselineCases
      | ($current[0].Cases // [] | case_index(.)) as $currentCases
      | [
        $baselineCases[] as $baselineCase
        | ($currentCases[$baselineCase.ServiceCase] // null) as $currentCase
        | if $currentCase == null then
            {
              serviceCase: $baselineCase.ServiceCase,
              check: "<case>",
              reason: "missing_case_in_current"
            }
          else
            $baselineCase.Checks[]
            | select(.Applicable == true and .Passed == true)
            | . as $baselineCheck
            | ([ $currentCase.Checks[]? | select(.Name == $baselineCheck.Name) ][0] // null) as $currentCheck
            | if $currentCheck == null then
                {
                  serviceCase: $baselineCase.ServiceCase,
                  check: $baselineCheck.Name,
                  reason: "missing_check_in_current"
                }
              elif ($currentCheck.Applicable == true and $currentCheck.Passed == false) then
                {
                  serviceCase: $baselineCase.ServiceCase,
                  check: $baselineCheck.Name,
                  reason: "pass_to_fail"
                }
              else
                empty
              end
          end
      ]
    '
)"

regression_count="$(jq 'length' <<<"$regressions")"
if [[ "$regression_count" -gt 0 ]]; then
  echo "Detected parity regressions against baseline:"
  jq -r '.[] | "- \(.serviceCase): \(.check) (\(.reason))"' <<<"$regressions"
  exit 1
fi

echo "No parity regressions detected against baseline."
