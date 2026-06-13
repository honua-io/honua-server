#!/usr/bin/env bash

set -euo pipefail

if [ "$#" -ne 3 ]; then
    echo "Usage: $0 <manifest.json> <results-dir> <output-dir>" >&2
    exit 2
fi

manifest="$1"
results_dir="$2"
output_dir="$3"

if [ ! -f "$manifest" ]; then
    echo "Manifest not found: $manifest" >&2
    exit 1
fi

mkdir -p "$output_dir"

table_path="$output_dir/sdk-compatibility-matrix.md"
summary_path="$output_dir/sdk-compatibility-summary.json"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

results_json="$tmp_dir/results.json"
if [ -d "$results_dir" ] && find "$results_dir" -type f -name 'compat-result.json' -print -quit | grep -q .; then
    find "$results_dir" -type f -name 'compat-result.json' -print0 \
        | sort -z \
        | xargs -0 jq -s '.' > "$results_json"
else
    printf '[]\n' > "$results_json"
fi

jq -e '
  . as $manifest
  | ($manifest.schemaVersion == 1)
  and ($manifest.serverRefs | type == "array")
  and ($manifest.sdkSetVersions | type == "array")
  and ($manifest.matrix | type == "object")
' "$manifest" >/dev/null

jq -n \
  --slurpfile manifest "$manifest" \
  --slurpfile results "$results_json" \
  'def listed($m; $group; $server; $sdk):
      any($m.matrix[$group][]?; .server == $server and .sdk == $sdk);
  def status($m; $server; $sdk):
      if listed($m; "supported"; $server; $sdk) then "supported"
      elif listed($m; "evaluation"; $server; $sdk) then "evaluation"
      elif listed($m; "unsupported"; $server; $sdk) then "unsupported"
      else "not-tested"
      end;
  def resultFor($results; $server; $sdk):
      [$results[]? | select(.server_label == $server and .sdk_label == $sdk)] | last;
  def cell($m; $results; $server; $sdk):
      (status($m; $server; $sdk)) as $status
      | (resultFor($results; $server; $sdk)) as $result
      | {
          server_label: $server,
          sdk_label: $sdk,
          status: $status,
          passed: (if $result == null then false else ($result.passed == true) end),
          exit_code: (if $result == null then null else $result.exit_code end),
          result: $result
        };
  ($manifest[0]) as $m
  | ($results[0]) as $results
  | [$m.serverRefs[].label] as $servers
  | [$m.sdkSetVersions[].label] as $sdks
  | [
      $servers[] as $server
      | $sdks[] as $sdk
      | cell($m; $results; $server; $sdk)
    ] as $cells
  | ($cells | map(select(.status == "supported"))) as $supported
  | ($cells | map(select(.status == "evaluation"))) as $evaluation
  | ($supported | map(select(.passed != true))) as $regressions
  | {
      generated_at: (now | todateiso8601),
      admin_api_major: $m.adminApiMajor,
      matrix_depth: $m.matrixDepth,
      total_cells: (($supported | length) + ($evaluation | length)),
      supported_cells: ($supported | length),
      evaluation_cells: ($evaluation | length),
      passed: (($regressions | length) == 0),
      regressions: $regressions,
      cells: $cells
    }' > "$summary_path"

mapfile -t server_labels < <(jq -r '.serverRefs[].label' "$manifest")
mapfile -t sdk_labels < <(jq -r '.sdkSetVersions[].label' "$manifest")

cell_text() {
    local server="$1"
    local sdk="$2"

    jq -r \
      --arg server "$server" \
      --arg sdk "$sdk" \
      '.cells[]
        | select(.server_label == $server and .sdk_label == $sdk)
        | if .status == "not-tested" then "NOT TESTED"
          elif .status == "unsupported" then "UNSUPPORTED"
          elif .status == "evaluation" and .passed == true then "EVAL PASS"
          elif .status == "evaluation" then "EVAL FAIL"
          elif .passed == true then "PASS"
          else "FAIL"
          end' "$summary_path"
}

{
    echo "# SDK Compatibility Matrix"
    echo
    echo "- Manifest: \`$manifest\`"
    echo "- Generated: $(date -u +'%Y-%m-%dT%H:%M:%SZ')"
    echo "- Supported regressions: $(jq '.regressions | length' "$summary_path")"
    echo
    printf '| Server / SDK |'
    for sdk in "${sdk_labels[@]}"; do
        printf ' `%s` |' "$sdk"
    done
    echo
    printf '%s' '|---|'
    for _ in "${sdk_labels[@]}"; do
        printf '%s' '---|'
    done
    echo
    for server in "${server_labels[@]}"; do
        printf '| `%s` |' "$server"
        for sdk in "${sdk_labels[@]}"; do
            printf ' %s |' "$(cell_text "$server" "$sdk")"
        done
        echo
    done
    echo
    echo "Legend: PASS = supported cell passed; FAIL = supported cell failed; EVAL PASS/EVAL FAIL = non-blocking evaluation cell; NOT TESTED = absent from the runnable matrix; UNSUPPORTED = intentionally excluded."
} > "$table_path"
