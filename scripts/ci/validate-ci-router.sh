#!/usr/bin/env bash
# Validate CI shard routing without starting runtime test runners.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "$1 is required for CI router validation." >&2
    exit 2
  fi
}

require_command jq
require_command python3

echo "Validating ci-shards.json structure..."
jq -e '
  type == "object"
  and (.infrastructure_paths | type == "array")
  and (.unmapped_source_run_all_prefixes | type == "array")
  and (.default_shards_when_no_match | type == "array" and length > 0)
  and (.shards | type == "array" and length > 0)
  and (
    [
      .shards[]
      | (
          (.name | type == "string" and length > 0)
          and (.shard_name | type == "string" and length > 0)
          and (.artifact_suffix | type == "string" and length > 0)
          and (.log_name | type == "string" and length > 0)
          and (.timeout_minutes | type == "number" and . > 0)
          and (.test_timeout_minutes | type == "number" and . > 0)
          and (.filter | type == "string" and length > 0)
          and (.paths | type == "array" and length > 0)
          and (.upload_operator_eval_report | type == "boolean")
          and (.upload_odata_evidence | type == "boolean")
        )
    ]
    | all
  )
  and (([.shards[].name] | length) == ([.shards[].name] | unique | length))
  and (([.shards[].artifact_suffix] | length) == ([.shards[].artifact_suffix] | unique | length))
  and (([.shards[].log_name] | length) == ([.shards[].log_name] | unique | length))
' .github/ci-shards.json >/dev/null

echo "Checking shell script syntax..."
bash -n scripts/ci/*.sh

echo "Checking Python helper syntax..."
python3 -m py_compile scripts/ci/*.py

echo "Checking workflow YAML syntax..."
python3 - <<'PY'
from pathlib import Path
try:
    import yaml
except ModuleNotFoundError:
    print("PyYAML is not installed; GitHub will still parse workflow YAML before running jobs.")
    raise SystemExit(0)

for path in sorted(Path(".github/workflows").glob("*.yml")):
    with path.open("r", encoding="utf-8") as handle:
        yaml.safe_load(handle)
PY

assert_descriptor() {
  local name="$1"
  local changed_files="$2"
  local expected_reason="$3"
  local expected_run_all="$4"
  local expected_shard="$5"
  local descriptor

  descriptor="$(printf '%s\n' "${changed_files}" | scripts/ci/honua-server-targeted-tests.sh --stdin)"
  echo "${name}: ${descriptor}"

  jq -e \
    --arg expected_reason "${expected_reason}" \
    --argjson expected_run_all "${expected_run_all}" \
    '.reason == $expected_reason and .run_all == $expected_run_all' \
    <<< "${descriptor}" >/dev/null

  if [[ -n "${expected_shard}" ]]; then
    jq -e --arg expected_shard "${expected_shard}" \
      '.shards | index($expected_shard)' \
      <<< "${descriptor}" >/dev/null
  fi
}

echo "Dry-running shard router cases..."
assert_descriptor \
  "ci-shards-only" \
  ".github/ci-shards.json" \
  "no_path_match" \
  "false" \
  "Core"

assert_descriptor \
  "ci-script-only" \
  "scripts/ci/honua-server-targeted-tests.sh" \
  "no_path_match" \
  "false" \
  "Core"

assert_descriptor \
  "feature-server-slice" \
  "src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerEndpoints.cs" \
  "targeted" \
  "false" \
  "FeatureServer Endpoints"

assert_descriptor \
  "shared-infrastructure" \
  "src/Honua.Server/Features/Infrastructure/Hosting/FeatureRegistrationExtensions.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

assert_descriptor \
  "unmapped-source" \
  "src/Honua.Server/Features/NewFeature/NewEndpoint.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

echo "CI router validation passed."
